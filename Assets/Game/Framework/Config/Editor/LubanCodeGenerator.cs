using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Framework.Build
{
    /// <summary>
    /// 配置表生成管线：封装 Luban CLI 调用——读表定义与数据（路径由 <see cref="LubanConfigProfile"/> 提供），
    /// 产出配置 C# 代码 + 二进制数据文件，并附带生成<b>表清单类</b>（LubanTableManifest.g.cs）。
    ///
    /// <para><b>为什么要清单</b>：生成的表根（Tables）构造函数按文件名同步向 loader 要字节，而运行时资源加载是异步的——
    /// 初始化 System 需要先知道「有哪些数据文件」才能并行预载。清单与代码/数据同一次生成，天然同步，
    /// 不存在手工维护漏表（机制同热更代码包的 manifest）。</para>
    ///
    /// <para>CLI 进程同步等待（生成通常秒级）；stdout/stderr 全量转记到返回摘要与 Console，
    /// 失败时直接给出 Luban 的原始报错，不二次包装。</para>
    /// </summary>
    public static class LubanCodeGenerator
    {
        private const int TimeoutMs = 120_000;

        /// <summary>
        /// 执行一次完整生成（代码 + 数据 + 清单），返回是否成功与人类可读摘要。
        /// 成功后已 <c>AssetDatabase.Refresh()</c>，Unity 侧产物立即可用。
        /// </summary>
        public static (bool ok, string message) Generate(LubanConfigProfile profile)
        {
            if (profile == null)
                return (false, "没有 LubanConfigProfile，无法生成。");

            var missing = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(profile.LubanToolPath)) missing.Add("Luban CLI 路径");
            if (string.IsNullOrWhiteSpace(profile.ConfPath)) missing.Add("luban.conf 路径");
            if (string.IsNullOrWhiteSpace(profile.OutputCodeDir)) missing.Add("代码输出目录");
            if (string.IsNullOrWhiteSpace(profile.OutputDataDir)) missing.Add("数据输出目录");
            if (string.IsNullOrWhiteSpace(profile.Target)) missing.Add("生成目标");
            if (string.IsNullOrWhiteSpace(profile.CodeTarget)) missing.Add("代码模板");
            if (string.IsNullOrWhiteSpace(profile.DataTarget)) missing.Add("数据格式");
            if (missing.Count > 0)
                return (false, "Luban profile 尚未配置完整：" + string.Join("、", missing) +
                               "。请先在配置总览中定位该资产并填写项目实际路径。");

            var ownershipProfiles = LubanConfigProfile.ResolveAll().Concat(new[] { profile }).Distinct().ToArray();
            var (ownershipOk, ownershipMessage) = ValidateOutputOwnership(ownershipProfiles);
            if (!ownershipOk) return (false, ownershipMessage);

            if (!FrameworkProjectPath.TryResolve(
                    profile.LubanToolPath, out _, out string toolPath, out string toolError))
                return (false, "Luban CLI 路径无效：" + toolError);
            if (!FrameworkProjectPath.TryResolve(
                    profile.ConfPath, out _, out string confPath, out string confError))
                return (false, "luban.conf 路径无效：" + confError);
            if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                    profile.OutputCodeDir, out string outputCodeAssetPath, out string outputCodeDir, out string codeError))
                return (false, "代码输出目录无效：" + codeError);
            if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                    profile.OutputDataDir, out string outputDataAssetPath, out string outputDataDir, out string dataError))
                return (false, "数据输出目录无效：" + dataError);

            if (!File.Exists(toolPath))
                return (false, $"Luban CLI 不存在：{toolPath}\n" +
                               "工具不入库：从 https://github.com/focus-creative-games/luban 的 release 下载，" +
                               $"解压到 {profile.LubanToolPath} 所在目录。");
            if (!File.Exists(confPath))
                return (false, $"luban.conf 不存在：{confPath}（检查 Luban profile 的 confPath）。");

            try
            {
                Directory.CreateDirectory(outputCodeDir);
                Directory.CreateDirectory(outputDataDir);

                string args =
                    $"-t {profile.Target} -c {profile.CodeTarget} -d {profile.DataTarget} " +
                    $"--conf \"{confPath}\" " +
                    $"-x \"outputCodeDir={outputCodeDir}\" " +
                    $"-x \"outputDataDir={outputDataDir}\"";
                if (!string.IsNullOrEmpty(profile.ExtraArgs))
                    args += " " + profile.ExtraArgs;

                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
                var (exitCode, log) = Run(toolPath, args, projectRoot);
                if (exitCode != 0)
                    return (false, $"Luban 生成失败（exit {exitCode}）。CLI 输出：\n{log}");

                string manifestSummary = WriteManifest(outputDataDir, outputCodeDir, profile.ManifestNamespace);
                AssetDatabase.Refresh();

                return (true, $"生成完成。\n  代码 → {outputCodeAssetPath}\n  数据 → {outputDataAssetPath}\n  {manifestSummary}\n\nCLI 输出：\n{log}");
            }
            catch (Exception exception) when (
                exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or
                InvalidOperationException)
            {
                return (false, "Luban 进程未能完成，未继续生成后续清单：" + exception.Message);
            }
        }

        /// <summary>
        /// 验证所有配置对代码 / 数据输出目录的独占所有权。每个目录都必须是 <c>Assets</c> 的非根子目录，
        /// 任意两个输出（包括同一 Profile 的代码与数据）不得相同或互为父子；失败不创建目录、不启动 CLI。
        /// </summary>
        public static (bool ok, string message) ValidateOutputOwnership(
            System.Collections.Generic.IReadOnlyList<LubanConfigProfile> profiles)
        {
            if (profiles == null || profiles.Count == 0)
                return (false, "没有可验证的 LubanConfigProfile。");

            var claims = new System.Collections.Generic.List<(
                LubanConfigProfile profile, string kind, string assetPath, string absolutePath)>();
            foreach (LubanConfigProfile candidate in profiles)
            {
                if (candidate == null) return (false, "配置列表含已删除或空的 LubanConfigProfile。");
                if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                        candidate.OutputCodeDir, out string codeAssetPath, out string codeAbsolutePath, out string codeError))
                    return (false, $"【{candidate.name}】代码输出目录无效：{codeError}");
                if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                        candidate.OutputDataDir, out string dataAssetPath, out string dataAbsolutePath, out string dataError))
                    return (false, $"【{candidate.name}】数据输出目录无效：{dataError}");
                claims.Add((candidate, "代码", codeAssetPath, codeAbsolutePath));
                claims.Add((candidate, "数据", dataAssetPath, dataAbsolutePath));
            }

            for (int i = 0; i < claims.Count; i++)
            for (int j = i + 1; j < claims.Count; j++)
            {
                var left = claims[i];
                var right = claims[j];
                if (!FrameworkProjectPath.DirectoriesOverlap(left.absolutePath, right.absolutePath)) continue;
                return (false,
                    $"输出目录所有权冲突：【{left.profile.name}】{left.kind} {left.assetPath} 与" +
                    $"【{right.profile.name}】{right.kind} {right.assetPath} 相同或互相嵌套。\n" +
                    "Luban 会整理输出目录；请为每项代码 / 数据产物分配互不嵌套的独立目录。");
            }

            return (true, $"{profiles.Count} 套配置的代码 / 数据输出目录彼此独立。");
        }

        private static (int exitCode, string log) Run(string toolPath, string args, string workingDir)
        {
            var psi = new ProcessStartInfo(toolPath, args)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Luban CLI 按 .NET 8 发布；本机只装更高版本时允许向上滚动运行，避免强制再装一套旧运行时。
            psi.EnvironmentVariables["DOTNET_ROLL_FORWARD"] = "LatestMajor";

            var log = new StringBuilder();
            using var process = new Process { StartInfo = psi };
            // 异步读两路输出（同步 ReadToEnd 在另一路缓冲填满时会互相等死）。
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(TimeoutMs))
            {
                process.Kill();
                process.WaitForExit();
                lock (log) return (-1, log + $"\n（超过 {TimeoutMs / 1000}s 未结束，进程已终止）");
            }
            // 带超时的 WaitForExit 返回后再无参等待一次，确保异步输出回调全部排空（.NET 的既定用法）。
            process.WaitForExit();
            lock (log) return (process.ExitCode, log.ToString());
        }

        // 扫数据输出目录生成表清单类：文件名（不含扩展名）= 资源 location = 表根构造时向 loader 请求的键。
        private static string WriteManifest(string outputDataDir, string outputCodeDir, string manifestNamespace)
        {
            var files = Directory.GetFiles(outputDataDir, "*.bytes")
                                 .Select(Path.GetFileNameWithoutExtension)
                                 .OrderBy(n => n, StringComparer.Ordinal)
                                 .ToList();
            if (files.Count == 0)
                Debug.LogWarning($"[配置表构建] 数据输出目录没有 .bytes 文件，清单为空：{outputDataDir}");

            var sb = new StringBuilder();
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//     由 LubanCodeGenerator 随代码/数据一起生成，勿手改；重新生成会覆盖。");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(manifestNamespace))
            {
                sb.AppendLine($"namespace {manifestNamespace}");
                sb.AppendLine("{");
            }
            sb.AppendLine("/// <summary>本次生成产出的全部表数据文件名（不含扩展名，即资源 location）。初始化 System 据此并行预载。</summary>");
            sb.AppendLine("public static class LubanTableManifest");
            sb.AppendLine("{");
            sb.AppendLine("    public static readonly string[] Files =");
            sb.AppendLine("    {");
            foreach (var f in files)
                sb.AppendLine($"        \"{f}\",");
            sb.AppendLine("    };");
            sb.AppendLine("}");
            if (!string.IsNullOrEmpty(manifestNamespace))
                sb.AppendLine("}");

            string path = Path.Combine(outputCodeDir, "LubanTableManifest.g.cs");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return $"清单 {files.Count} 张表：{string.Join(", ", files)}";
        }
    }
}
