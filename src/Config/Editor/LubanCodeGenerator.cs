using System;
using System.Collections.Generic;
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
        internal readonly struct GenerationPrerequisiteReport
        {
            internal bool CanGenerate { get; }
            internal string Message { get; }
            internal string ToolPath { get; }
            internal string ConfPath { get; }
            internal string OutputCodeAssetPath { get; }
            internal string OutputCodeDirectory { get; }
            internal string OutputDataAssetPath { get; }
            internal string OutputDataDirectory { get; }

            internal GenerationPrerequisiteReport(
                bool canGenerate,
                string message,
                string toolPath = "",
                string confPath = "",
                string outputCodeAssetPath = "",
                string outputCodeDirectory = "",
                string outputDataAssetPath = "",
                string outputDataDirectory = "")
            {
                CanGenerate = canGenerate;
                Message = message;
                ToolPath = toolPath;
                ConfPath = confPath;
                OutputCodeAssetPath = outputCodeAssetPath;
                OutputCodeDirectory = outputCodeDirectory;
                OutputDataAssetPath = outputDataAssetPath;
                OutputDataDirectory = outputDataDirectory;
            }
        }

        private const int TimeoutMs = 120_000;

        /// <summary>
        /// 执行一次完整生成（代码 + 数据 + 清单），返回是否成功与人类可读摘要。
        /// 成功后已 <c>AssetDatabase.Refresh()</c>，Unity 侧产物立即可用。
        /// </summary>
        public static (bool ok, string message) Generate(LubanConfigProfile profile)
        {
            if (profile == null)
                return (false, "没有 LubanConfigProfile，无法生成。");

            var ownershipProfiles = LubanConfigProfile.ResolveAll().Concat(new[] { profile }).Distinct().ToArray();
            var (ownershipOk, ownershipMessage) = ValidateOutputOwnership(ownershipProfiles);
            if (!ownershipOk) return (false, ownershipMessage);
            GenerationPrerequisiteReport prerequisites = InspectGenerationPrerequisites(profile);
            if (!prerequisites.CanGenerate) return (false, prerequisites.Message);

            try
            {
                Directory.CreateDirectory(prerequisites.OutputCodeDirectory);
                Directory.CreateDirectory(prerequisites.OutputDataDirectory);

                string args =
                    $"-t {profile.Target} -c {profile.CodeTarget} -d {profile.DataTarget} " +
                    $"--conf \"{prerequisites.ConfPath}\" " +
                    $"-x \"outputCodeDir={prerequisites.OutputCodeDirectory}\" " +
                    $"-x \"outputDataDir={prerequisites.OutputDataDirectory}\"";
                if (!string.IsNullOrEmpty(profile.ExtraArgs))
                    args += " " + profile.ExtraArgs;

                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
                var (exitCode, log) = Run(prerequisites.ToolPath, args, projectRoot);
                if (exitCode != 0)
                    return (false, $"Luban 生成失败（exit {exitCode}）。CLI 输出：\n{log}");

                string manifestSummary = WriteManifest(
                    prerequisites.OutputDataDirectory,
                    prerequisites.OutputCodeDirectory,
                    profile.ManifestNamespace);
                AssetDatabase.Refresh();

                return (true,
                    $"生成完成。\n  代码 → {prerequisites.OutputCodeAssetPath}" +
                    $"\n  数据 → {prerequisites.OutputDataAssetPath}\n  {manifestSummary}\n\nCLI 输出：\n{log}");
            }
            catch (Exception exception) when (
                exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or
                InvalidOperationException)
            {
                return (false, "Luban 进程未能完成，未继续生成后续清单：" + exception.Message);
            }
        }

        /// <summary>
        /// 只读检查 Luban 字段、路径、CLI 与 luban.conf 是否就绪。不创建输出目录、不启动 CLI，
        /// 也不解析 conf；生成动作会在点击后重新执行同一检查。
        /// </summary>
        internal static GenerationPrerequisiteReport InspectGenerationPrerequisites(
            LubanConfigProfile profile)
        {
            if (profile == null)
                return new GenerationPrerequisiteReport(false, "LubanConfigProfile 不能为空。");

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(profile.LubanToolPath)) missing.Add("Luban CLI 路径");
            if (string.IsNullOrWhiteSpace(profile.ConfPath)) missing.Add("luban.conf 路径");
            if (string.IsNullOrWhiteSpace(profile.OutputCodeDir)) missing.Add("代码输出目录");
            if (string.IsNullOrWhiteSpace(profile.OutputDataDir)) missing.Add("数据输出目录");
            if (string.IsNullOrWhiteSpace(profile.Target)) missing.Add("生成目标");
            if (string.IsNullOrWhiteSpace(profile.CodeTarget)) missing.Add("代码模板");
            if (string.IsNullOrWhiteSpace(profile.DataTarget)) missing.Add("数据格式");
            if (missing.Count > 0)
                return new GenerationPrerequisiteReport(
                    false,
                    "Profile 尚未配置完整：" + string.Join("、", missing) + "。");
            if (!string.IsNullOrEmpty(profile.ManifestNamespace) &&
                !FrameworkCSharpSyntax.TryValidateNamespace(
                    profile.ManifestNamespace, out string namespaceError))
                return new GenerationPrerequisiteReport(
                    false,
                    "清单命名空间无效：" + namespaceError);

            if (!FrameworkProjectPath.TryResolve(
                    profile.LubanToolPath, out _, out string toolPath, out string toolError))
                return new GenerationPrerequisiteReport(false, "Luban CLI 路径无效：" + toolError);
            if (!FrameworkProjectPath.TryResolve(
                    profile.ConfPath, out _, out string confPath, out string confError))
                return new GenerationPrerequisiteReport(false, "luban.conf 路径无效：" + confError);
            if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                    profile.OutputCodeDir,
                    out string outputCodeAssetPath,
                    out string outputCodeDirectory,
                    out string codeError))
                return new GenerationPrerequisiteReport(false, "代码输出目录无效：" + codeError);
            if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                    profile.OutputDataDir,
                    out string outputDataAssetPath,
                    out string outputDataDirectory,
                    out string dataError))
                return new GenerationPrerequisiteReport(false, "数据输出目录无效：" + dataError);

            var issues = new List<string>();
            if (!File.Exists(toolPath))
                issues.Add(
                    $"Luban CLI 不存在：{toolPath}\n" +
                    "下一步：从 Luban 官方 release 下载后，将可执行文件放到配置路径。");
            if (!File.Exists(confPath))
                issues.Add($"luban.conf 不存在：{confPath}");
            if (issues.Count > 0)
                return new GenerationPrerequisiteReport(
                    false,
                    string.Join("\n", issues),
                    toolPath,
                    confPath,
                    outputCodeAssetPath,
                    outputCodeDirectory,
                    outputDataAssetPath,
                    outputDataDirectory);

            return new GenerationPrerequisiteReport(
                true,
                "Luban CLI、luban.conf 与代码/数据输出路径均已就绪。",
                toolPath,
                confPath,
                outputCodeAssetPath,
                outputCodeDirectory,
                outputDataAssetPath,
                outputDataDirectory);
        }

        /// <summary>
        /// 比较所有已经成立的代码 / 数据输出声明。缺失或无效路径不形成声明，留给所属 Profile 的就绪检查；
        /// 因此空白新配置不会冻结其它配置，但未就绪 Profile 中任何有效的单项输出仍参与比较。
        /// 任意两个声明（包括同一 Profile 的代码与数据）不得相同或互为父子；失败不创建目录、不启动 CLI。
        /// </summary>
        public static (bool ok, string message) ValidateOutputOwnership(
            System.Collections.Generic.IReadOnlyList<LubanConfigProfile> profiles)
        {
            if (profiles == null || profiles.Count == 0)
                return (false, "没有可验证的 LubanConfigProfile。");

            var claims = new System.Collections.Generic.List<(
                LubanConfigProfile profile, string kind, string assetPath, string absolutePath)>();
            int unresolvedCount = 0;
            foreach (LubanConfigProfile candidate in profiles)
            {
                if (candidate == null) return (false, "配置列表含已删除或空的 LubanConfigProfile。");
                if (FrameworkProjectPath.TryResolveAssetsDirectory(
                        candidate.OutputCodeDir,
                        out string codeAssetPath,
                        out string codeAbsolutePath,
                        out _))
                    claims.Add((candidate, "代码", codeAssetPath, codeAbsolutePath));
                else
                    unresolvedCount++;

                if (FrameworkProjectPath.TryResolveAssetsDirectory(
                        candidate.OutputDataDir,
                        out string dataAssetPath,
                        out string dataAbsolutePath,
                        out _))
                    claims.Add((candidate, "数据", dataAssetPath, dataAbsolutePath));
                else
                    unresolvedCount++;
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

            if (claims.Count == 0)
                return (true, "尚无有效代码 / 数据输出声明；缺失或无效路径会在对应配置卡片中提示。");

            string unresolvedNote = unresolvedCount > 0
                ? $"；另有 {unresolvedCount} 项尚未形成有效声明，由各自就绪检查提示"
                : string.Empty;
            return (true, $"{claims.Count} 项代码 / 数据输出声明彼此独立{unresolvedNote}。");
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
