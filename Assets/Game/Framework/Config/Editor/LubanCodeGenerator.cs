using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string toolPath = Path.GetFullPath(Path.Combine(projectRoot, profile.LubanToolPath));
            string confPath = Path.GetFullPath(Path.Combine(projectRoot, profile.ConfPath));

            if (!File.Exists(toolPath))
                return (false, $"Luban CLI 不存在：{toolPath}\n" +
                               "工具不入库：从 https://github.com/focus-creative-games/luban 的 release 下载，" +
                               $"解压到 {profile.LubanToolPath} 所在目录。");
            if (!File.Exists(confPath))
                return (false, $"luban.conf 不存在：{confPath}（检查 Luban profile 的 confPath）。");

            string outputCodeDir = Path.GetFullPath(Path.Combine(projectRoot, profile.OutputCodeDir));
            string outputDataDir = Path.GetFullPath(Path.Combine(projectRoot, profile.OutputDataDir));
            Directory.CreateDirectory(outputCodeDir);
            Directory.CreateDirectory(outputDataDir);

            string args =
                $"-t {profile.Target} -c {profile.CodeTarget} -d {profile.DataTarget} " +
                $"--conf \"{confPath}\" " +
                $"-x \"outputCodeDir={outputCodeDir}\" " +
                $"-x \"outputDataDir={outputDataDir}\"";
            if (!string.IsNullOrEmpty(profile.ExtraArgs))
                args += " " + profile.ExtraArgs;

            var (exitCode, log) = Run(toolPath, args, projectRoot);
            if (exitCode != 0)
                return (false, $"Luban 生成失败（exit {exitCode}）。CLI 输出：\n{log}");

            string manifestSummary = WriteManifest(outputDataDir, outputCodeDir, profile.ManifestNamespace);
            AssetDatabase.Refresh();

            return (true, $"生成完成。\n  代码 → {profile.OutputCodeDir}\n  数据 → {profile.OutputDataDir}\n  {manifestSummary}\n\nCLI 输出：\n{log}");
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
