using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Network.Proto.Editor
{
    /// <summary>
    /// Protobuf 协议生成管线：封装官方 protoc CLI——把一套 <see cref="ProtoConfigProfile"/> 指向的
    /// .proto 源目录整体生成为 C#（<c>*.g.cs</c>）到输出目录。
    ///
    /// <para><b>差量同步</b>：protoc 先产出到临时目录，再与输出目录比对——内容没变的文件不落盘
    /// （Unity 不重导入、不触发无谓重编译）；.proto 改名 / 删除后遗留的陈旧 <c>*.g.cs</c> 连 .meta 一并清理，
    /// 杜绝「重命名后新旧生成文件类型重复定义」。输出目录里生成器只认领 <c>*.g.cs</c>，其他文件不动。</para>
    ///
    /// <para>CLI 进程异步读两路输出（同步 ReadToEnd 在另一路缓冲填满时会互相等死）+ 超时终止；
    /// 失败时原样转出 protoc 的报错，不二次包装。</para>
    /// </summary>
    public static class ProtoCodeGenerator
    {
        private const int TimeoutMs = 60_000;

        /// <summary>
        /// 执行一次完整生成（.proto → *.g.cs + 差量同步），返回是否成功与人类可读摘要。
        /// 成功后已 <c>AssetDatabase.Refresh()</c>，Unity 侧产物立即可用。
        /// </summary>
        public static (bool ok, string message) Generate(ProtoConfigProfile profile)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);

            if (string.IsNullOrEmpty(profile.ProtoDir) || string.IsNullOrEmpty(profile.OutputCodeDir))
                return (false, "profile 未配置完整：.proto 源目录与代码输出目录都必填（选中该资产在 Inspector 填写）。");

            string protoc = ResolveProtocPath(projectRoot, profile.ProtocDir);
            if (!File.Exists(protoc))
                return (false, $"protoc 不存在：{protoc}\n" +
                               "从 https://github.com/protocolbuffers/protobuf/releases 下载对应平台的 protoc，" +
                               "解压出的 bin/protoc 放到该路径。");

            string protoDirAbs = Path.GetFullPath(Path.Combine(projectRoot, profile.ProtoDir));
            if (!Directory.Exists(protoDirAbs))
                return (false, $".proto 源目录不存在：{protoDirAbs}（检查 profile 的 protoDir）。");

            // 输入文件用相对 --proto_path 的路径（protoc 要求输入位于某个 proto_path 之下，相对写法两者天然一致）。
            var protoFiles = Directory.GetFiles(protoDirAbs, "*.proto", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(protoDirAbs, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (protoFiles.Count == 0)
                return (false, $".proto 源目录里没有 .proto 文件：{protoDirAbs}");

            string outDirAbs = Path.GetFullPath(Path.Combine(projectRoot, profile.OutputCodeDir));
            string tempDir = Path.GetFullPath(FileUtil.GetUniqueTempPathInProject());
            Directory.CreateDirectory(tempDir);
            try
            {
                var psi = new ProcessStartInfo(protoc)
                {
                    WorkingDirectory = protoDirAbs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add($"--proto_path={protoDirAbs}");
                psi.ArgumentList.Add($"--csharp_out={tempDir}");
                // 生成文件统一 .g.cs 后缀：既是「生成产物」的显式标记，也是差量同步的认领边界。
                psi.ArgumentList.Add("--csharp_opt=file_extension=.g.cs");
                foreach (string extra in profile.ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    psi.ArgumentList.Add(extra);
                foreach (string file in protoFiles)
                    psi.ArgumentList.Add(file);

                var (exitCode, log) = Run(psi);
                if (exitCode != 0)
                    return (false, $"protoc 失败（exit {exitCode}）。输出：\n{log}");

                string syncSummary = SyncGenerated(tempDir, outDirAbs);
                AssetDatabase.Refresh();
                return (true, $"生成完成（{protoFiles.Count} 个 .proto：{string.Join(", ", protoFiles)}）。\n" +
                              $"  代码 → {profile.OutputCodeDir}（{syncSummary}）" +
                              (log.Length > 0 ? $"\nprotoc 输出：\n{log}" : ""));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* 临时目录清不掉不影响结果 */ }
            }
        }

        /// <summary>当前编辑器平台的 protoc 可执行文件路径（<paramref name="protocDir"/> 相对工程根目录）。</summary>
        public static string ResolveProtocPath(string projectRoot, string protocDir)
        {
#if UNITY_EDITOR_WIN
            return Path.GetFullPath(Path.Combine(projectRoot, protocDir, "windows_x64/protoc.exe"));
#elif UNITY_EDITOR_OSX
            return Path.GetFullPath(Path.Combine(projectRoot, protocDir, "macosx_x64/protoc"));
#else
            return Path.GetFullPath(Path.Combine(projectRoot, protocDir, "linux_x64/protoc"));
#endif
        }

        private static (int exitCode, string log) Run(ProcessStartInfo psi)
        {
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
            lock (log) return (process.ExitCode, log.ToString().Trim());
        }

        // 临时目录 → 输出目录差量同步：新增 / 内容变化才写（Unity 才重导入），本次未产出的陈旧 *.g.cs 连 .meta 删除。
        private static string SyncGenerated(string tempDir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int added = 0, updated = 0, unchanged = 0, removed = 0;

            foreach (string src in Directory.GetFiles(tempDir, "*.g.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(tempDir, src);
                string dst = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                produced.Add(Path.GetFullPath(dst));

                if (!File.Exists(dst)) { File.Copy(src, dst); added++; }
                else if (!FilesEqual(src, dst)) { File.Copy(src, dst, true); updated++; }
                else unchanged++;
            }

            foreach (string existing in Directory.GetFiles(outDir, "*.g.cs", SearchOption.AllDirectories))
            {
                if (produced.Contains(Path.GetFullPath(existing))) continue;
                File.Delete(existing);
                if (File.Exists(existing + ".meta")) File.Delete(existing + ".meta");
                removed++;
            }

            return $"新增 {added} · 更新 {updated} · 未变 {unchanged} · 清理陈旧 {removed}";
        }

        private static bool FilesEqual(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
