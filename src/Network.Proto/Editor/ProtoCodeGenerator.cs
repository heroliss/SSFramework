using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Game.Framework.Editor;
using UnityEditor;

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
        internal const string OutputClaimSourceId = "protobuf";

        internal readonly struct GenerationPrerequisiteReport
        {
            internal bool CanGenerate { get; }
            internal string Message { get; }
            internal string ProtocPath { get; }
            internal string ProtoDirectory { get; }
            internal string OutputAssetPath { get; }
            internal string OutputDirectory { get; }
            internal IReadOnlyList<string> ProtoFiles { get; }
            internal int ProtoFileCount => ProtoFiles?.Count ?? 0;

            internal GenerationPrerequisiteReport(
                bool canGenerate,
                string message,
                string protocPath = "",
                string protoDirectory = "",
                string outputAssetPath = "",
                string outputDirectory = "",
                IReadOnlyList<string> protoFiles = null)
            {
                CanGenerate = canGenerate;
                Message = message;
                ProtocPath = protocPath;
                ProtoDirectory = protoDirectory;
                OutputAssetPath = outputAssetPath;
                OutputDirectory = outputDirectory;
                ProtoFiles = protoFiles ?? Array.Empty<string>();
            }
        }

        private const int TimeoutMs = 60_000;
        private static readonly Dictionary<int, GenerationPrerequisitePreviewEntry>
            GenerationPrerequisitePreviews = new();
        private static int _previewProfileRevision = -1;

        /// <summary>
        /// IMGUI 同一帧可多次进入 Layout / Repaint；预览只需在 Profile 输入或工程 revision
        /// 变化后重新递归扫描。写入路径不读这份缓存，仍直接调用
        /// <see cref="InspectGenerationPrerequisites"/>验证当前磁盘。
        /// </summary>
        internal static bool TryGetGenerationPrerequisitePreview(
            ProtoConfigProfile profile,
            out GenerationPrerequisiteReport report)
        {
            report = default;
            if (profile == null) return false;
            EnsurePreviewRevision();

            int key = profile.GetInstanceID();
            if (GenerationPrerequisitePreviews.TryGetValue(
                    key, out GenerationPrerequisitePreviewEntry cached) &&
                cached.Matches(profile))
            {
                report = cached.Report;
                return true;
            }

            GenerationPrerequisitePreviews.Remove(key);
            return false;
        }

        /// <summary>
        /// 显式重新扫描给定 Profile 的工具、路径与递归 .proto 输入；全部成功后才
        /// 替换预览快照，避免窗口混用两次扫描结果。
        /// </summary>
        internal static void RefreshGenerationPrerequisitePreviews(
            IEnumerable<ProtoConfigProfile> profiles)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));
            var refreshed = new Dictionary<int, GenerationPrerequisitePreviewEntry>();
            foreach (ProtoConfigProfile profile in profiles.Where(profile => profile != null).Distinct())
            {
                GenerationPrerequisiteReport report = InspectGenerationPrerequisites(profile);
                refreshed[profile.GetInstanceID()] = new GenerationPrerequisitePreviewEntry(profile, report);
            }

            GenerationPrerequisitePreviews.Clear();
            foreach (var pair in refreshed) GenerationPrerequisitePreviews.Add(pair.Key, pair.Value);
            _previewProfileRevision = FrameworkEditorProfileCatalog.Revision;
        }

        /// <summary>强制丢弃工作台输入预览；下一次绘制会重新读取当前目录。</summary>
        internal static void InvalidateGenerationPrerequisitePreviews()
        {
            GenerationPrerequisitePreviews.Clear();
            _previewProfileRevision = FrameworkEditorProfileCatalog.Revision;
        }

        private static void EnsurePreviewRevision()
        {
            int revision = FrameworkEditorProfileCatalog.Revision;
            if (_previewProfileRevision == revision) return;
            GenerationPrerequisitePreviews.Clear();
            _previewProfileRevision = revision;
        }

        /// <summary>
        /// 执行一次完整生成（.proto → *.g.cs + 差量同步），返回是否成功与人类可读摘要。
        /// 成功后已 <c>AssetDatabase.Refresh()</c>，Unity 侧产物立即可用。
        /// </summary>
        public static (bool ok, string message) Generate(ProtoConfigProfile profile)
        {
            if (profile == null) return (false, "ProtoConfigProfile 不能为空。");
            var ownershipProfiles = ProtoConfigProfile.ResolveAll().Concat(new[] { profile }).Distinct().ToArray();
            var (ownershipOk, ownershipMessage) = ValidateOutputOwnership(
                ownershipProfiles, beforeWrite: true);
            if (!ownershipOk) return (false, ownershipMessage);
            GenerationPrerequisiteReport prerequisites = InspectGenerationPrerequisites(profile);
            if (!prerequisites.CanGenerate) return (false, prerequisites.Message);

            string tempDir = Path.GetFullPath(FileUtil.GetUniqueTempPathInProject());
            Directory.CreateDirectory(tempDir);
            try
            {
                var psi = new ProcessStartInfo(prerequisites.ProtocPath)
                {
                    WorkingDirectory = prerequisites.ProtoDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add($"--proto_path={prerequisites.ProtoDirectory}");
                psi.ArgumentList.Add($"--csharp_out={tempDir}");
                // 生成文件统一 .g.cs 后缀：既是「生成产物」的显式标记，也是差量同步的认领边界。
                psi.ArgumentList.Add("--csharp_opt=file_extension=.g.cs");
                foreach (string extra in profile.ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    psi.ArgumentList.Add(extra);
                foreach (string file in prerequisites.ProtoFiles)
                    psi.ArgumentList.Add(file);

                var (exitCode, log) = Run(psi);
                if (exitCode != 0)
                    return (false, $"protoc 失败（exit {exitCode}）。输出：\n{log}");

                string syncSummary = SyncGenerated(tempDir, prerequisites.OutputDirectory);
                AssetDatabase.Refresh();
                return (true, $"生成完成（{prerequisites.ProtoFileCount} 个 .proto：{string.Join(", ", prerequisites.ProtoFiles)}）。\n" +
                              $"  代码 → {prerequisites.OutputAssetPath}（{syncSummary}）" +
                              (log.Length > 0 ? $"\nprotoc 输出：\n{log}" : ""));
            }
            catch (Exception exception) when (
                exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or
                InvalidOperationException)
            {
                return (false, "protoc 进程或产物同步未能完成：" + exception.Message);
            }
            finally
            {
                try
                {
                    FrameworkProjectPath.DeleteDirectoryWithinBoundary(
                        tempDir,
                        Path.GetDirectoryName(tempDir) ?? Path.GetFullPath("Temp"));
                }
                catch
                {
                    /* 临时目录清不掉不影响结果；安全删除会拒绝跟随链接。 */
                }
            }
        }

        /// <summary>
        /// 只读检查一套 Profile 的字段、工程内路径、当前平台 protoc 与递归 .proto 输入。
        /// 不创建目录、不启动进程、不解析协议内容；生成动作会在点击后重新执行同一检查。
        /// </summary>
        internal static GenerationPrerequisiteReport InspectGenerationPrerequisites(
            ProtoConfigProfile profile)
        {
            if (profile == null)
                return new GenerationPrerequisiteReport(false, "ProtoConfigProfile 不能为空。");

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(profile.ProtocDir)) missing.Add("protoc 工具目录");
            if (string.IsNullOrWhiteSpace(profile.ProtoDir)) missing.Add(".proto 源目录");
            if (string.IsNullOrWhiteSpace(profile.OutputCodeDir)) missing.Add("代码输出目录");
            if (missing.Count > 0)
                return new GenerationPrerequisiteReport(
                    false,
                    "Profile 尚未配置完整：" + string.Join("、", missing) + "。");

            if (!FrameworkProjectPath.TryResolve(
                    profile.ProtocDir, out _, out string protocRoot, out string protocPathError))
                return new GenerationPrerequisiteReport(false, "protoc 目录无效：" + protocPathError);
            if (!FrameworkProjectPath.TryResolve(
                    profile.ProtoDir, out _, out string protoDirectory, out string protoPathError))
                return new GenerationPrerequisiteReport(false, ".proto 源目录无效：" + protoPathError);
            if (!TryResolveOutputDirectory(
                    profile, out string outputAssetPath, out string outputDirectory, out string outputError))
                return new GenerationPrerequisiteReport(false, outputError);

            string protocPath = ResolveProtocPath(protocRoot, string.Empty);
            var issues = new List<string>();
            if (!File.Exists(protocPath))
                issues.Add(
                    $"protoc 不存在：{protocPath}\n" +
                    "下一步：从 protobuf 官方 release 下载当前平台版本，并把可执行文件放到上述目录。");

            string[] protoFiles = Array.Empty<string>();
            if (!Directory.Exists(protoDirectory))
            {
                issues.Add($".proto 源目录不存在：{protoDirectory}");
            }
            else
            {
                try
                {
                    protoFiles = FrameworkProjectPath
                        .CapturePhysicalTree(protoDirectory, "*.proto")
                        .Files
                        .Select(file => Path.GetRelativePath(protoDirectory, file).Replace('\\', '/'))
                        .OrderBy(file => file, StringComparer.Ordinal)
                        .ToArray();
                    if (protoFiles.Length == 0)
                        issues.Add($".proto 源目录里没有 .proto 文件：{protoDirectory}");
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    issues.Add(
                        $"无法读取 .proto 源目录：{exception.GetType().Name}: {exception.Message}");
                }
            }

            if (issues.Count > 0)
                return new GenerationPrerequisiteReport(
                    false,
                    string.Join("\n", issues),
                    protocPath,
                    protoDirectory,
                    outputAssetPath,
                    outputDirectory,
                    protoFiles);

            return new GenerationPrerequisiteReport(
                true,
                $"已发现 {protoFiles.Length} 个 .proto；protoc、源目录与输出路径均已就绪。",
                protocPath,
                protoDirectory,
                outputAssetPath,
                outputDirectory,
                protoFiles);
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

        /// <summary>
        /// 在批量生成写盘前比较所有已经成立的输出目录声明。缺失或无效路径不形成所有权声明，留给所属
        /// Profile 的就绪检查提示，因此新建中的空白配置不会冻结其它可用配置；已经声明有效输出的未就绪配置
        /// 仍参与冲突比较。递归 <c>*.g.cs</c> 清理 claim 会与其它 Module 的独占目录、后缀范围和精确文件
        /// 共同检查，失败时不会创建、覆盖或清理任何产物。
        /// </summary>
        public static (bool ok, string message) ValidateOutputOwnership(
            IReadOnlyList<ProtoConfigProfile> profiles) =>
            ValidateOutputOwnership(profiles, beforeWrite: false);

        private static (bool ok, string message) ValidateOutputOwnership(
            IReadOnlyList<ProtoConfigProfile> profiles,
            bool beforeWrite)
        {
            if (profiles == null || profiles.Count == 0)
                return (false, "没有可验证的 ProtoConfigProfile。");

            var claims = new List<FrameworkGeneratedOutputClaim>();
            int unresolvedCount = 0;
            foreach (ProtoConfigProfile profile in profiles)
            {
                if (profile == null) return (false, "配置列表含已删除或空的 ProtoConfigProfile。");
                unresolvedCount += AddOutputClaim(profile, claims);
            }

            string ownershipMessage;
            bool ownershipOk = beforeWrite
                ? FrameworkGeneratedOutputClaimCatalog.TryValidateBeforeWrite(
                    OutputClaimSourceId, claims, out ownershipMessage)
                : FrameworkGeneratedOutputClaimCatalog.TryValidateForPreview(
                    OutputClaimSourceId, claims, out ownershipMessage);
            if (!ownershipOk) return (false, ownershipMessage);

            string previewEvidence = beforeWrite ? string.Empty : "\n" + ownershipMessage;
            if (claims.Count == 0)
                return (true,
                    "尚无有效输出目录声明；缺失或无效路径会在对应配置卡片中提示。" +
                    previewEvidence);

            string unresolvedNote = unresolvedCount > 0
                ? $"；另有 {unresolvedCount} 套尚未形成有效输出声明，由各自就绪检查提示"
                : string.Empty;
            return (true, $"{claims.Count} 套配置的递归清理声明当前有效{unresolvedNote}。{previewEvidence}");
        }

        /// <summary>供共享 Catalog 按需读取当前 Module 已成立的递归清理声明。</summary>
        internal static IReadOnlyList<FrameworkGeneratedOutputClaim> CollectRegisteredOutputClaims()
        {
            var claims = new List<FrameworkGeneratedOutputClaim>();
            foreach (ProtoConfigProfile profile in ProtoConfigProfile.ResolveAll())
                AddOutputClaim(profile, claims);
            return claims;
        }

        private static int AddOutputClaim(
            ProtoConfigProfile profile,
            ICollection<FrameworkGeneratedOutputClaim> claims)
        {
            if (!TryResolveOutputDirectory(profile, out string assetPath, out string absolutePath, out _))
                return 1;

            string profilePath = AssetDatabase.GetAssetPath(profile);
            string profileId = string.IsNullOrEmpty(profilePath)
                ? $"transient:{profile.name}:{profile.GetInstanceID()}"
                : profilePath;
            claims.Add(FrameworkGeneratedOutputClaim.RecursiveFileSuffix(
                profileId + ":generated-code",
                $"Protobuf【{profile.name}】",
                assetPath,
                absolutePath,
                ".g.cs"));
            return 0;
        }

        private static bool TryResolveOutputDirectory(
            ProtoConfigProfile profile,
            out string assetPath,
            out string absolutePath,
            out string error)
        {
            if (FrameworkProjectPath.TryResolveAssetsDirectory(
                    profile.OutputCodeDir, out assetPath, out absolutePath, out string pathError))
            {
                error = string.Empty;
                return true;
            }

            error = "代码输出目录无效：" + pathError;
            return false;
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

            foreach (string src in FrameworkProjectPath
                         .CapturePhysicalTree(tempDir, "*.g.cs")
                         .Files)
            {
                string rel = Path.GetRelativePath(tempDir, src);
                string dst = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                produced.Add(Path.GetFullPath(dst));

                if (!File.Exists(dst)) { File.Copy(src, dst); added++; }
                else if (!FilesEqual(src, dst)) { File.Copy(src, dst, true); updated++; }
                else unchanged++;
            }

            foreach (string existing in FrameworkProjectPath
                         .CapturePhysicalTree(outDir, "*.g.cs")
                         .Files)
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

        private sealed class GenerationPrerequisitePreviewEntry
        {
            private readonly ProtoConfigProfile _profile;
            private readonly string _protocDirectory;
            private readonly string _protoDirectory;
            private readonly string _outputDirectory;

            internal GenerationPrerequisitePreviewEntry(
                ProtoConfigProfile profile,
                GenerationPrerequisiteReport report)
            {
                _profile = profile;
                _protocDirectory = profile.ProtocDir;
                _protoDirectory = profile.ProtoDir;
                _outputDirectory = profile.OutputCodeDir;
                Report = report;
            }

            internal GenerationPrerequisiteReport Report { get; }

            internal bool Matches(ProtoConfigProfile profile) =>
                ReferenceEquals(_profile, profile) &&
                string.Equals(_protocDirectory, profile.ProtocDir, StringComparison.Ordinal) &&
                string.Equals(_protoDirectory, profile.ProtoDir, StringComparison.Ordinal) &&
                string.Equals(_outputDirectory, profile.OutputCodeDir, StringComparison.Ordinal);
        }
    }
}
