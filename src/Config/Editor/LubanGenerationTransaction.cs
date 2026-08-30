using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Game.Framework.Config.Editor
{
    /// <summary>
    /// Luban 单套配置的双目录发布事务：CLI 只接触 <see cref="StagingCodeDirectory"/> 与
    /// <see cref="StagingDataDirectory"/>；本类型在暂存快照完整通过校验后，才把代码、数据与清单作为
    /// 同一代产物差量发布到正式目录。发布中任一步失败都会从写前快照恢复两棵目录树。
    ///
    /// <para>这是一项 Config Editor Module 内部能力，不与 Protobuf 共用：Luban 独占代码 / 数据两棵树，
    /// 且清单必须与数据文件集合绑定；Protobuf 只认领单目录中的特定后缀，清理语义并不相同。</para>
    ///
    /// <para>事务保证覆盖当前进程可观察到的异常，不承诺 Editor 被强制终止或机器断电时的跨目录原子性；
    /// 若回滚也失败，会保留 <see cref="RecoveryDirectory"/> 并把发布与回滚两类错误一起上报。</para>
    /// </summary>
    internal sealed class LubanGenerationTransaction : IDisposable
    {
        internal const string ManifestFileName = "LubanTableManifest.g.cs";

        internal enum PublishCheckpoint
        {
            DataTreePublished,
            CodeTreePublished,
        }

        internal readonly struct TreePublishReport
        {
            internal int Added { get; }
            internal int Updated { get; }
            internal int Unchanged { get; }
            internal int Removed { get; }
            internal bool HasChanges => Added > 0 || Updated > 0 || Removed > 0;

            internal TreePublishReport(int added, int updated, int unchanged, int removed)
            {
                Added = added;
                Updated = updated;
                Unchanged = unchanged;
                Removed = removed;
            }

            public override string ToString() =>
                $"新增 {Added} · 更新 {Updated} · 未变 {Unchanged} · 清理陈旧 {Removed}";
        }

        internal readonly struct PublishReport
        {
            internal TreePublishReport Code { get; }
            internal TreePublishReport Data { get; }
            internal IReadOnlyList<string> TableNames { get; }
            internal bool HasChanges => Code.HasChanges || Data.HasChanges;

            internal PublishReport(
                TreePublishReport code,
                TreePublishReport data,
                IReadOnlyList<string> tableNames)
            {
                Code = code;
                Data = data;
                TableNames = tableNames;
            }

            internal string ManifestSummary =>
                $"清单 {TableNames.Count} 张表：{string.Join(", ", TableNames)}";
        }

        private enum FileChangeKind
        {
            Added,
            Updated,
            Unchanged,
        }

        private readonly struct FileChange
        {
            internal string RelativePath { get; }
            internal string SourcePath { get; }
            internal string DestinationPath { get; }
            internal FileChangeKind Kind { get; }

            internal FileChange(
                string relativePath,
                string sourcePath,
                string destinationPath,
                FileChangeKind kind)
            {
                RelativePath = relativePath;
                SourcePath = sourcePath;
                DestinationPath = destinationPath;
                Kind = kind;
            }
        }

        private sealed class TreePublishPlan
        {
            internal string DestinationRoot { get; }
            internal IReadOnlyList<FileChange> ProducedFiles { get; }
            internal IReadOnlyList<string> StaleFiles { get; }
            internal IReadOnlyList<string> StaleMetaFiles { get; }
            internal IReadOnlyList<string> StaleDirectories { get; }
            internal TreePublishReport Report { get; }

            internal TreePublishPlan(
                string destinationRoot,
                IReadOnlyList<FileChange> producedFiles,
                IReadOnlyList<string> staleFiles,
                IReadOnlyList<string> staleMetaFiles,
                IReadOnlyList<string> staleDirectories)
            {
                DestinationRoot = destinationRoot;
                ProducedFiles = producedFiles;
                StaleFiles = staleFiles;
                StaleMetaFiles = staleMetaFiles;
                StaleDirectories = staleDirectories;
                Report = new TreePublishReport(
                    producedFiles.Count(change => change.Kind == FileChangeKind.Added),
                    producedFiles.Count(change => change.Kind == FileChangeKind.Updated),
                    producedFiles.Count(change => change.Kind == FileChangeKind.Unchanged),
                    staleFiles.Count + staleMetaFiles.Count + staleDirectories.Count);
            }
        }

        private readonly string _outputCodeDirectory;
        private readonly string _outputDataDirectory;
        private readonly string _outputBoundaryDirectory;
        private readonly string _backupCodeDirectory;
        private readonly string _backupDataDirectory;
        private bool _codeDirectoryExisted;
        private bool _dataDirectoryExisted;
        private bool _disposed;
        private bool _publishAttempted;
        private bool _preserveRecoveryDirectory;

        internal string RecoveryDirectory { get; }
        internal string StagingCodeDirectory { get; }
        internal string StagingDataDirectory { get; }
        internal string CleanupWarning { get; private set; }

        internal LubanGenerationTransaction(
            string transactionRoot,
            string outputCodeDirectory,
            string outputDataDirectory,
            string outputBoundaryDirectory)
        {
            if (string.IsNullOrWhiteSpace(transactionRoot))
                throw new ArgumentException("Luban 事务临时目录不能为空。", nameof(transactionRoot));
            if (string.IsNullOrWhiteSpace(outputCodeDirectory))
                throw new ArgumentException("Luban 正式代码目录不能为空。", nameof(outputCodeDirectory));
            if (string.IsNullOrWhiteSpace(outputDataDirectory))
                throw new ArgumentException("Luban 正式数据目录不能为空。", nameof(outputDataDirectory));
            if (string.IsNullOrWhiteSpace(outputBoundaryDirectory))
                throw new ArgumentException("Luban 正式输出边界不能为空。", nameof(outputBoundaryDirectory));

            RecoveryDirectory = NormalizeDirectory(transactionRoot);
            _outputCodeDirectory = NormalizeDirectory(outputCodeDirectory);
            _outputDataDirectory = NormalizeDirectory(outputDataDirectory);
            _outputBoundaryDirectory = NormalizeDirectory(outputBoundaryDirectory);
            ValidateOutputPathBoundary(_outputCodeDirectory, _outputBoundaryDirectory);
            ValidateOutputPathBoundary(_outputDataDirectory, _outputBoundaryDirectory);
            if (PathsOverlap(_outputCodeDirectory, _outputDataDirectory))
                throw new InvalidOperationException("Luban 代码与数据正式目录不能相同或互相嵌套。");
            if (PathsOverlap(RecoveryDirectory, _outputCodeDirectory) ||
                PathsOverlap(RecoveryDirectory, _outputDataDirectory))
                throw new InvalidOperationException("Luban 事务临时目录不能位于正式输出目录内，也不能包含正式输出目录。");
            if (File.Exists(RecoveryDirectory))
                throw new InvalidOperationException($"Luban 事务临时路径已被普通文件占用：{RecoveryDirectory}");
            if (Directory.Exists(RecoveryDirectory))
            {
                // 工程或 Temp 的父级可由用户有意重定向；只拒绝将事务 root 本身伪装成空 junction / symlink。
                EnsureNotReparsePoint(RecoveryDirectory);
                if (Directory.EnumerateFileSystemEntries(RecoveryDirectory).Any())
                    throw new InvalidOperationException($"Luban 事务临时目录必须为空：{RecoveryDirectory}");
            }

            StagingCodeDirectory = Path.Combine(RecoveryDirectory, "code");
            StagingDataDirectory = Path.Combine(RecoveryDirectory, "data");
            _backupCodeDirectory = Path.Combine(RecoveryDirectory, "backup-code");
            _backupDataDirectory = Path.Combine(RecoveryDirectory, "backup-data");

            try
            {
                Directory.CreateDirectory(StagingCodeDirectory);
                Directory.CreateDirectory(StagingDataDirectory);
            }
            catch
            {
                TryDeleteDirectory(RecoveryDirectory, out _);
                throw;
            }
        }

        /// <summary>
        /// 校验 CLI 暂存快照、生成清单并联合发布代码 / 数据目录。<paramref name="checkpoint"/> 只作为
        /// Editor 测试的确定性故障注入 Seam；生产调用保持为 <c>null</c>。
        /// </summary>
        internal PublishReport ValidateAndPublish(
            string manifestNamespace,
            Action<PublishCheckpoint> checkpoint = null)
        {
            ThrowIfDisposed();
            if (_publishAttempted)
                throw new InvalidOperationException("同一个 Luban 生成事务只能发布一次。");
            _publishAttempted = true;

            IReadOnlyList<string> tableNames = ValidateStagedArtifacts();
            WriteManifest(tableNames, StagingCodeDirectory, manifestNamespace ?? string.Empty);

            TreePublishPlan codePlan = BuildPlan(StagingCodeDirectory, _outputCodeDirectory);
            TreePublishPlan dataPlan = BuildPlan(StagingDataDirectory, _outputDataDirectory);
            var report = new PublishReport(codePlan.Report, dataPlan.Report, tableNames);
            if (!report.HasChanges) return report;

            CreateBackupSnapshot();
            try
            {
                // 数据先行、manifest 随代码树最后写入；Unity 自动刷新由调用方在整个 commit 期间抑制。
                ApplyPlan(dataPlan, manifestLast: false);
                checkpoint?.Invoke(PublishCheckpoint.DataTreePublished);
                ApplyPlan(codePlan, manifestLast: true);
                checkpoint?.Invoke(PublishCheckpoint.CodeTreePublished);
                return report;
            }
            catch (Exception publishException)
            {
                IReadOnlyList<Exception> rollbackErrors = RestoreBackupSnapshot();
                if (rollbackErrors.Count == 0)
                    throw new InvalidOperationException(
                        "Luban 产物发布失败；代码与数据目录均已恢复到发布前快照。" +
                        $"\n发布错误：{DescribeException(publishException)}",
                        publishException);

                _preserveRecoveryDirectory = true;
                string rollbackMessage = string.Join(
                    " | ",
                    rollbackErrors.Select(DescribeException));
                throw new InvalidOperationException(
                    "Luban 产物发布失败，且自动回滚未能完整恢复。" +
                    $"恢复快照已保留在：{RecoveryDirectory}\n" +
                    $"发布错误：{DescribeException(publishException)}\n" +
                    $"回滚错误：{rollbackMessage}",
                    publishException);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_preserveRecoveryDirectory) return;
            if (!TryDeleteDirectory(RecoveryDirectory, out Exception cleanupError))
                CleanupWarning =
                    $"Luban 已完成处理，但临时目录未能清理：{RecoveryDirectory}（{cleanupError.Message}）";
        }

        private IReadOnlyList<string> ValidateStagedArtifacts()
        {
            IReadOnlyList<string> codeFiles = EnumerateFilesWithoutReparsePoints(StagingCodeDirectory);
            if (codeFiles.Count == 0)
                throw new InvalidDataException("Luban 暂存代码目录没有生成任何 C# 文件；正式产物未修改。");

            foreach (string codeFile in codeFiles)
            {
                string relativePath = RelativePath(StagingCodeDirectory, codeFile);
                if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Luban 暂存代码目录出现非 C# 产物：{relativePath}；正式产物未修改。");
                if (new FileInfo(codeFile).Length == 0)
                    throw new InvalidDataException(
                        $"Luban 暂存代码产物为空文件：{relativePath}；正式产物未修改。");
                if (Path.GetFileName(relativePath).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Luban CLI 产物与框架清单文件重名：{relativePath}；请调整表或生成配置。");
            }
            EnsurePortableUniquePaths(codeFiles.Select(path => RelativePath(StagingCodeDirectory, path)), "代码");
            NormalizeCodeLineEndingsToLf(codeFiles);

            string[] nestedDataDirectories = Directory.GetDirectories(
                StagingDataDirectory, "*", SearchOption.TopDirectoryOnly);
            if (nestedDataDirectories.Length > 0)
                throw new InvalidDataException(
                    "Luban binary 数据必须直接位于数据输出根目录；发现子目录：" +
                    string.Join(", ", nestedDataDirectories.Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal)));

            IReadOnlyList<string> dataFiles = EnumerateFilesWithoutReparsePoints(StagingDataDirectory);
            if (dataFiles.Count == 0)
                throw new InvalidDataException("Luban 暂存数据目录没有生成任何 .bytes 文件；正式产物未修改。");

            var tableNames = new List<string>(dataFiles.Count);
            foreach (string dataFile in dataFiles)
            {
                string relativePath = RelativePath(StagingDataDirectory, dataFile);
                if (relativePath.Contains('/'))
                    throw new InvalidDataException(
                        $"Luban binary 数据必须直接位于数据输出根目录：{relativePath}");
                if (!relativePath.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Luban 暂存数据目录出现非 .bytes 产物：{relativePath}；正式产物未修改。");
                if (new FileInfo(dataFile).Length == 0)
                    throw new InvalidDataException(
                        $"Luban 暂存数据产物为空文件：{relativePath}；正式产物未修改。");
                string tableName = Path.GetFileNameWithoutExtension(relativePath);
                if (string.IsNullOrWhiteSpace(tableName))
                    throw new InvalidDataException($"Luban 数据文件缺少有效 location：{relativePath}");
                tableNames.Add(tableName);
            }

            EnsurePortableUniquePaths(dataFiles.Select(path => RelativePath(StagingDataDirectory, path)), "数据");
            string duplicateLocation = tableNames
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(duplicateLocation))
                throw new InvalidDataException(
                    $"Luban 数据 location 在大小写不敏感平台会冲突：{duplicateLocation}");

            return tableNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        private void CreateBackupSnapshot()
        {
            ValidateDestinationTree(_outputCodeDirectory, _outputBoundaryDirectory);
            ValidateDestinationTree(_outputDataDirectory, _outputBoundaryDirectory);
            _codeDirectoryExisted = Directory.Exists(_outputCodeDirectory);
            _dataDirectoryExisted = Directory.Exists(_outputDataDirectory);

            // 两份快照都成功后才允许首次修改正式目录。
            if (_codeDirectoryExisted) CopyDirectory(_outputCodeDirectory, _backupCodeDirectory);
            if (_dataDirectoryExisted) CopyDirectory(_outputDataDirectory, _backupDataDirectory);
        }

        private IReadOnlyList<Exception> RestoreBackupSnapshot()
        {
            var errors = new List<Exception>();
            TryRestoreDirectory(
                _outputCodeDirectory,
                _backupCodeDirectory,
                _codeDirectoryExisted,
                _outputBoundaryDirectory,
                errors);
            TryRestoreDirectory(
                _outputDataDirectory,
                _backupDataDirectory,
                _dataDirectoryExisted,
                _outputBoundaryDirectory,
                errors);
            return errors;
        }

        private static void TryRestoreDirectory(
            string destination,
            string backup,
            bool existedBeforePublish,
            string outputBoundary,
            ICollection<Exception> errors)
        {
            try
            {
                ValidateDestinationTree(destination, outputBoundary);
                if (Directory.Exists(destination)) DeleteDirectoryWithoutFollowingLinks(destination);
                else if (File.Exists(destination)) File.Delete(destination);
                if (existedBeforePublish) CopyDirectory(backup, destination);
            }
            catch (Exception exception)
            {
                errors.Add(new IOException($"恢复目录失败：{destination}", exception));
            }
        }

        private TreePublishPlan BuildPlan(string sourceRoot, string destinationRoot)
        {
            IReadOnlyList<string> sourceFiles = EnumerateFilesWithoutReparsePoints(sourceRoot);
            ValidateDestinationTree(destinationRoot, _outputBoundaryDirectory);
            IReadOnlyList<string> destinationFiles = Directory.Exists(destinationRoot)
                ? EnumerateFilesWithoutReparsePoints(destinationRoot)
                : Array.Empty<string>();
            IReadOnlyList<string> destinationDirectories = Directory.Exists(destinationRoot)
                ? EnumerateDirectoriesWithoutReparsePoints(destinationRoot)
                : Array.Empty<string>();

            var destinationByRelativePath = destinationFiles
                .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(path => RelativePath(destinationRoot, path), path => path, StringComparer.Ordinal);
            var changes = new List<FileChange>(sourceFiles.Count);
            var producedRelativePaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (string source in sourceFiles.OrderBy(path => RelativePath(sourceRoot, path), StringComparer.Ordinal))
            {
                string relativePath = RelativePath(sourceRoot, source);
                if (!producedRelativePaths.Add(relativePath))
                    throw new InvalidDataException($"Luban 暂存目录含重复路径：{relativePath}");

                string destination = Path.Combine(
                    destinationRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                FileChangeKind kind;
                if (!destinationByRelativePath.TryGetValue(relativePath, out string existing))
                {
                    kind = FileChangeKind.Added;
                }
                else
                {
                    destination = existing;
                    kind = FilesEqual(source, existing)
                        ? FileChangeKind.Unchanged
                        : FileChangeKind.Updated;
                }

                changes.Add(new FileChange(relativePath, source, destination, kind));
            }

            string[] staleFiles = destinationByRelativePath
                .Where(pair => !producedRelativePaths.Contains(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .ToArray();
            HashSet<string> requiredDirectories = CollectRequiredDirectories(producedRelativePaths);
            string[] staleDirectories = destinationDirectories
                .Where(path => !requiredDirectories.Contains(RelativePath(destinationRoot, path)))
                .OrderByDescending(path => RelativePath(destinationRoot, path).Count(character => character == '/'))
                .ThenByDescending(path => RelativePath(destinationRoot, path), StringComparer.Ordinal)
                .ToArray();
            var staleFileRelativePaths = new HashSet<string>(
                staleFiles.Select(path => RelativePath(destinationRoot, path)),
                StringComparer.Ordinal);
            var staleDirectoryRelativePaths = new HashSet<string>(
                staleDirectories.Select(path => RelativePath(destinationRoot, path)),
                StringComparer.Ordinal);
            string[] staleMetaFiles = destinationFiles
                .Where(path => path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .Where(path =>
                {
                    string relativePath = RelativePath(destinationRoot, path);
                    string ownedPath = relativePath.Substring(0, relativePath.Length - ".meta".Length);
                    return !producedRelativePaths.Contains(ownedPath) &&
                           !requiredDirectories.Contains(ownedPath) &&
                           !staleFileRelativePaths.Contains(ownedPath) &&
                           !staleDirectoryRelativePaths.Contains(ownedPath);
                })
                .OrderBy(path => RelativePath(destinationRoot, path), StringComparer.Ordinal)
                .ToArray();
            return new TreePublishPlan(
                destinationRoot,
                changes,
                staleFiles,
                staleMetaFiles,
                staleDirectories);
        }

        private static void ApplyPlan(TreePublishPlan plan, bool manifestLast)
        {
            if (!plan.Report.HasChanges) return;
            Directory.CreateDirectory(plan.DestinationRoot);

            // 先移除本次已不再产出的文件，大小写变化也会按「旧文件删除 + 新文件增加」处理。
            foreach (string staleFile in plan.StaleFiles)
            {
                File.Delete(staleFile);
                string metaFile = staleFile + ".meta";
                if (File.Exists(metaFile)) File.Delete(metaFile);
            }
            foreach (string staleMetaFile in plan.StaleMetaFiles)
                File.Delete(staleMetaFile);

            // 先清空旧拓扑，再创建新文件：这也覆盖目录大小写变化与「目录 ↔ 文件」替换。
            foreach (string staleDirectory in plan.StaleDirectories)
            {
                if (Directory.Exists(staleDirectory)) Directory.Delete(staleDirectory, false);
                string metaFile = staleDirectory + ".meta";
                if (File.Exists(metaFile)) File.Delete(metaFile);
            }

            IEnumerable<FileChange> changes = plan.ProducedFiles
                .Where(change => change.Kind != FileChangeKind.Unchanged);
            if (manifestLast)
                changes = changes.OrderBy(
                    change => change.RelativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ? 1 : 0);

            foreach (FileChange change in changes)
            {
                string parent = Path.GetDirectoryName(change.DestinationPath);
                if (string.IsNullOrEmpty(parent))
                    throw new InvalidOperationException($"Luban 产物缺少目标父目录：{change.DestinationPath}");
                Directory.CreateDirectory(parent);
                PublishFile(change.SourcePath, change.DestinationPath, change.Kind == FileChangeKind.Updated);
            }
        }

        private static void PublishFile(string source, string destination, bool overwrite)
        {
            string temporary = destination + ".ssframework-publish-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, false);
                if (!overwrite)
                {
                    File.Move(temporary, destination);
                    return;
                }

                try
                {
                    File.Replace(temporary, destination, null);
                }
                catch (Exception exception) when (
                    exception is PlatformNotSupportedException or IOException)
                {
                    // 个别文件系统不支持 Replace；事务快照仍保留旧代，覆盖失败会由外层恢复两棵树。
                    File.Copy(temporary, destination, true);
                    File.Delete(temporary);
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void WriteManifest(
            IReadOnlyList<string> tableNames,
            string outputCodeDirectory,
            string manifestNamespace)
        {
            var source = new StringBuilder();
            source.AppendLine("//------------------------------------------------------------------------------");
            source.AppendLine("// <auto-generated>");
            source.AppendLine("//     由 LubanCodeGenerator 随代码/数据一起生成，勿手改；重新生成会覆盖。");
            source.AppendLine("// </auto-generated>");
            source.AppendLine("//------------------------------------------------------------------------------");
            source.AppendLine();
            if (!string.IsNullOrEmpty(manifestNamespace))
            {
                source.AppendLine($"namespace {manifestNamespace}");
                source.AppendLine("{");
            }
            source.AppendLine("/// <summary>本次生成产出的全部表数据文件名（不含扩展名，即资源 location）。初始化 System 据此并行预载。</summary>");
            source.AppendLine("public static class LubanTableManifest");
            source.AppendLine("{");
            source.AppendLine("    public static readonly string[] Files =");
            source.AppendLine("    {");
            foreach (string tableName in tableNames)
                source.AppendLine($"        \"{EscapeCSharpString(tableName)}\",");
            source.AppendLine("    };");
            source.AppendLine("}");
            if (!string.IsNullOrEmpty(manifestNamespace)) source.AppendLine("}");

            File.WriteAllText(
                Path.Combine(outputCodeDirectory, ManifestFileName),
                source.ToString().Replace("\r\n", "\n").Replace('\r', '\n'),
                new UTF8Encoding(false));
        }

        private static void NormalizeCodeLineEndingsToLf(IEnumerable<string> codeFiles)
        {
            var strictUtf8 = new UTF8Encoding(false, true);
            var utf8WithoutBom = new UTF8Encoding(false);
            foreach (string codeFile in codeFiles)
            {
                byte[] source = File.ReadAllBytes(codeFile);
                int offset = source.Length >= 3 &&
                             source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF
                    ? 3
                    : 0;
                if (Array.IndexOf(source, (byte)0) >= 0)
                    throw new InvalidDataException(
                        $"Luban 暂存代码必须是 UTF-8 文本，不能含 NUL 字节：{codeFile}");

                string text;
                try
                {
                    text = strictUtf8.GetString(source, offset, source.Length - offset);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException(
                        $"Luban 暂存代码必须是有效 UTF-8 文本：{codeFile}",
                        exception);
                }
                if (text.Length == 0)
                    throw new InvalidDataException(
                        $"Luban 暂存代码产物规范化后为空文件：{codeFile}");

                byte[] normalized = utf8WithoutBom.GetBytes(
                    text.Replace("\r\n", "\n").Replace('\r', '\n'));
                if (!source.SequenceEqual(normalized)) File.WriteAllBytes(codeFile, normalized);
            }
        }

        private static HashSet<string> CollectRequiredDirectories(IEnumerable<string> producedRelativePaths)
        {
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string producedRelativePath in producedRelativePaths)
            {
                int separator = producedRelativePath.LastIndexOf('/');
                while (separator > 0)
                {
                    string directory = producedRelativePath.Substring(0, separator);
                    directories.Add(directory);
                    separator = directory.LastIndexOf('/');
                }
            }
            return directories;
        }

        private static string DescribeException(Exception exception)
        {
            var messages = new List<string>();
            for (Exception current = exception; current != null; current = current.InnerException)
                messages.Add($"{current.GetType().Name}: {current.Message}");
            return string.Join(" -> ", messages);
        }

        private static string EscapeCSharpString(string value)
        {
            var escaped = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '"': escaped.Append("\\\""); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (char.IsControl(character))
                            escaped.Append("\\u").Append(((int)character).ToString("x4"));
                        else
                            escaped.Append(character);
                        break;
                }
            }
            return escaped.ToString();
        }

        private static IReadOnlyList<string> EnumerateFilesWithoutReparsePoints(string root)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            EnsureNotReparsePoint(root);
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string childDirectory in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    EnsureNotReparsePoint(childDirectory);
                    pending.Push(childDirectory);
                }
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    EnsureNotReparsePoint(file);
                    files.Add(file);
                }
            }
            return files;
        }

        private static IReadOnlyList<string> EnumerateDirectoriesWithoutReparsePoints(string root)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            EnsureNotReparsePoint(root);
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string childDirectory in Directory.GetDirectories(
                             directory, "*", SearchOption.TopDirectoryOnly))
                {
                    EnsureNotReparsePoint(childDirectory);
                    directories.Add(childDirectory);
                    pending.Push(childDirectory);
                }
            }
            return directories;
        }

        private static void ValidateDestinationTree(string root, string outputBoundary)
        {
            ValidateOutputPathBoundary(root, outputBoundary);
            if (File.Exists(root))
                throw new IOException($"Luban 正式输出路径已被普通文件占用：{root}");
            if (Directory.Exists(root)) EnumerateFilesWithoutReparsePoints(root);
        }

        private static void ValidateOutputPathBoundary(string outputDirectory, string outputBoundary)
        {
            string normalizedOutput = NormalizeDirectory(outputDirectory);
            string normalizedBoundary = NormalizeDirectory(outputBoundary);
            if (!normalizedOutput.StartsWith(
                    normalizedBoundary + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Luban 正式输出必须位于受信边界的子目录内：{normalizedOutput}（边界：{normalizedBoundary}）");
            if (!Directory.Exists(normalizedBoundary))
                throw new DirectoryNotFoundException($"Luban 正式输出边界不存在：{normalizedBoundary}");

            // 逐级检查所有已经存在的节点，避免 Assets 内 junction / symlink 把递归备份、删除或恢复导向工程外。
            EnsureNotReparsePoint(normalizedBoundary);
            string relative = Path.GetRelativePath(normalizedBoundary, normalizedOutput);
            string current = normalizedBoundary;
            foreach (string segment in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current))
                    throw new IOException($"Luban 输出路径中的节点已被普通文件占用：{current}");
                if (!Directory.Exists(current)) break;
                EnsureNotReparsePoint(current);
            }
        }

        private static void EnsureNotReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Luban 生成目录不允许符号链接或 junction：{path}");
        }

        private static void EnsurePortableUniquePaths(IEnumerable<string> relativePaths, string kind)
        {
            string duplicate = relativePaths
                .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(duplicate))
                throw new InvalidDataException(
                    $"Luban 暂存{kind}路径在大小写不敏感平台会冲突：{duplicate}");
        }

        private static bool FilesEqual(string left, string right)
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length) return false;

            const int bufferSize = 81920;
            var leftBuffer = new byte[bufferSize];
            var rightBuffer = new byte[bufferSize];
            using var leftStream = File.OpenRead(left);
            using var rightStream = File.OpenRead(right);
            while (true)
            {
                int leftCount = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
                int rightCount = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
                if (leftCount != rightCount) return false;
                if (leftCount == 0) return true;
                for (int index = 0; index < leftCount; index++)
                    if (leftBuffer[index] != rightBuffer[index]) return false;
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            EnsureNotReparsePoint(source);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureNotReparsePoint(file);
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
            }
            foreach (string child in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureNotReparsePoint(child);
                CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
            }
        }

        private static bool TryDeleteDirectory(string path, out Exception error)
        {
            try
            {
                if (Directory.Exists(path)) DeleteDirectoryWithoutFollowingLinks(path);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception;
                return false;
            }
        }

        private static void DeleteDirectoryWithoutFollowingLinks(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(directory, false);
                return;
            }

            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
                File.Delete(file);
            foreach (string childDirectory in Directory.GetDirectories(
                         directory, "*", SearchOption.TopDirectoryOnly))
                DeleteDirectoryWithoutFollowingLinks(childDirectory);
            Directory.Delete(directory, false);
        }

        private static string RelativePath(string root, string path) =>
            Path.GetRelativePath(root, path).Replace('\\', '/');

        private static string NormalizeDirectory(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static bool PathsOverlap(string left, string right)
        {
            string normalizedLeft = NormalizeDirectory(left);
            string normalizedRight = NormalizeDirectory(right);
            return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
                   normalizedLeft.StartsWith(normalizedRight + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   normalizedRight.StartsWith(normalizedLeft + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LubanGenerationTransaction));
        }
    }
}
