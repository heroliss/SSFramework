using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 将用户配置的工程相对路径解析为规范化 Asset Path 与绝对路径，并在任何文件写入前锁定工程边界。
    /// 可选 Editor Module 共用本类型，避免各生成器只检查字符串前缀而被 <c>Assets/../..</c> 绕过。
    /// 本类型只做路径与当前文件系统对象类型校验，不创建目录或资产。
    /// </summary>
    public static class FrameworkProjectPath
    {
        /// <summary>
        /// 一次递归读取所得的物理目录树。目录和文件都是规范化绝对路径；采集遇到 symlink、junction
        /// 或其它 reparse point 会在返回前失败，因此调用方可安全地继续只读、复制或清理。
        /// </summary>
        public sealed class PhysicalTreeSnapshot
        {
            /// <summary>规范化后的扫描根目录。</summary>
            public string Root { get; }
            /// <summary>根目录下的全部子目录，不包含 <see cref="Root"/>。</summary>
            public IReadOnlyList<string> Directories { get; }
            /// <summary>符合文件名模式的全部文件。</summary>
            public IReadOnlyList<string> Files { get; }

            internal PhysicalTreeSnapshot(string root, string[] directories, string[] files)
            {
                Root = root;
                Directories = directories;
                Files = files;
            }
        }

        /// <summary>
        /// 解析工程相对路径。成功时返回使用正斜杠的规范化工程相对路径和绝对路径；空值、绝对路径、
        /// 非法路径或通过 <c>..</c> 逃出工程根目录时返回 <c>false</c>，并在 <paramref name="error"/> 说明原因。
        /// </summary>
        public static bool TryResolve(
            string configuredPath,
            out string projectRelativePath,
            out string absolutePath,
            out string error)
        {
            projectRelativePath = string.Empty;
            absolutePath = string.Empty;
            error = string.Empty;

            string candidate = configuredPath?.Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                error = "路径不能为空。";
                return false;
            }
            if (LooksLikeAbsolutePath(candidate))
            {
                error = $"路径必须相对工程根目录，不能使用绝对路径：{configuredPath}";
                return false;
            }

            try
            {
                // Profile 会入库并跨 OS 使用；先把两种分隔符都解释为目录边界，避免 Windows 写入的反斜杠
                // 在 Unix Editor 上变成普通文件名字符。绝对路径已在上方按两套平台语法共同拒绝。
                candidate = candidate
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                string projectRoot = Path.GetFullPath(
                    Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory());
                string resolved = Path.GetFullPath(Path.Combine(projectRoot, candidate));
                if (!IsSameOrChild(resolved, projectRoot, FileSystemPathComparison))
                {
                    error = $"路径越过了工程根目录：{configuredPath}";
                    return false;
                }
                if (!TryValidatePhysicalPath(projectRoot, resolved, out string physicalError))
                {
                    error = ToStableProjectError(physicalError, projectRoot);
                    return false;
                }

                string relative = Path.GetRelativePath(projectRoot, resolved).Replace('\\', '/');
                projectRelativePath = relative == "." ? string.Empty : relative;
                absolutePath = resolved;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"路径格式无效：{configuredPath}（{exception.Message}）";
                return false;
            }
        }

        /// <summary>
        /// 解析必须位于 <c>Assets</c> 子目录内的输出目录。默认拒绝 <c>Assets</c> 根目录，避免具有清理语义的
        /// 生成器误把整个工程当成自己的产物目录；本方法不要求目录已经存在，但会拒绝目标或任一父级
        /// 已被普通文件占用的路径。
        /// </summary>
        public static bool TryResolveAssetsDirectory(
            string configuredPath,
            out string assetPath,
            out string absolutePath,
            out string error,
            bool allowAssetsRoot = false)
        {
            if (!TryResolve(configuredPath, out assetPath, out absolutePath, out error)) return false;
            if (!assetPath.Equals("Assets", StringComparison.Ordinal) &&
                !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                error = $"路径必须位于 Assets 内：{configuredPath}";
                assetPath = string.Empty;
                absolutePath = string.Empty;
                return false;
            }
            if (!allowAssetsRoot && assetPath.Equals("Assets", StringComparison.Ordinal))
            {
                error = "不能把 Assets 根目录作为生成输出；请为该生成器分配独立子目录。";
                assetPath = string.Empty;
                absolutePath = string.Empty;
                return false;
            }
            if (File.Exists(absolutePath))
            {
                error = $"目标当前是普通文件，不能作为输出目录：{configuredPath}";
                assetPath = string.Empty;
                absolutePath = string.Empty;
                return false;
            }
            if (TryFindBlockingFileAncestor(absolutePath, out string blockingAssetPath))
            {
                error = $"路径中的父级已被普通文件占用，无法在其下创建输出：{blockingAssetPath}";
                assetPath = string.Empty;
                absolutePath = string.Empty;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 解析必须位于 <c>Assets</c> 子目录内的输出文件，并校验扩展名。成功时不会创建父目录或文件，
        /// 但会拒绝已被目录占用的目标，或任一父级已被普通文件占用的路径；
        /// <paramref name="requiredExtension"/> 应包含点号，例如 <c>.cs</c>。
        /// </summary>
        public static bool TryResolveAssetsFile(
            string configuredPath,
            string requiredExtension,
            out string assetPath,
            out string absolutePath,
            out string error)
        {
            if (!TryResolve(configuredPath, out assetPath, out absolutePath, out error)) return false;
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                error = $"输出文件必须位于 Assets 子目录内：{configuredPath}";
                assetPath = string.Empty;
                absolutePath = string.Empty;
                return false;
            }
            if (string.IsNullOrEmpty(Path.GetFileName(assetPath)))
            {
                error = $"输出路径必须指向文件：{configuredPath}";
                assetPath = string.Empty;
                absolutePath = string.Empty;
                return false;
            }
            if (!string.IsNullOrEmpty(requiredExtension) &&
                !assetPath.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            {
                error = $"输出文件必须以 {requiredExtension} 结尾：{configuredPath}";
                assetPath = string.Empty;
                absolutePath = string.Empty;
                return false;
            }
            if (Directory.Exists(absolutePath))
            {
                error = $"目标当前是目录，不能作为输出文件：{configuredPath}";
                assetPath = string.Empty;
                absolutePath = string.Empty;
                return false;
            }
            if (TryFindBlockingFileAncestor(absolutePath, out string blockingAssetPath))
            {
                error = $"路径中的父级已被普通文件占用，无法在其下创建输出：{blockingAssetPath}";
                assetPath = string.Empty;
                absolutePath = string.Empty;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 判断两个已规范化的绝对目录是否相同或存在父子关系。生成器用它确保不同配置不会共同认领、清理同一目录树。
        /// </summary>
        public static bool DirectoriesOverlap(string leftAbsoluteDirectory, string rightAbsoluteDirectory) =>
            IsSameOrChild(leftAbsoluteDirectory, rightAbsoluteDirectory, PortableAssetPathComparison) ||
            IsSameOrChild(rightAbsoluteDirectory, leftAbsoluteDirectory, PortableAssetPathComparison);

        /// <summary>按跨平台保守的大小写不敏感语义比较两个产物路径；比较前会转换为规范化绝对路径。</summary>
        public static bool PathsEqual(string leftPath, string rightPath) =>
            Path.GetFullPath(leftPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(rightPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    PortableAssetPathComparison);

        /// <summary>
        /// 按跨平台保守口径判断 <paramref name="candidateAbsolutePath"/> 是否等于或位于
        /// <paramref name="absoluteDirectory"/> 内。参数必须是已经解析的绝对路径。
        /// </summary>
        internal static bool ContainsPath(string absoluteDirectory, string candidateAbsolutePath) =>
            IsSameOrChild(candidateAbsolutePath, absoluteDirectory, PortableAssetPathComparison);

        /// <summary>
        /// 验证绝对候选路径位于指定绝对边界内，并逐级拒绝已经存在的 symlink、junction、其它 reparse point
        /// 与阻塞后续子路径的普通文件。候选末端可以是普通文件、目录或尚不存在；本方法不扫描其子树。
        /// </summary>
        public static bool TryValidatePhysicalPath(
            string boundaryAbsoluteDirectory,
            string candidateAbsolutePath,
            out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(boundaryAbsoluteDirectory) ||
                string.IsNullOrWhiteSpace(candidateAbsolutePath))
            {
                error = "物理路径边界与候选路径都不能为空。";
                return false;
            }
            if (!Path.IsPathRooted(boundaryAbsoluteDirectory) || !Path.IsPathRooted(candidateAbsolutePath))
            {
                error = "物理路径安全检查只接受绝对路径。";
                return false;
            }

            try
            {
                string boundary = NormalizeAbsolutePath(boundaryAbsoluteDirectory);
                string candidate = NormalizeAbsolutePath(candidateAbsolutePath);
                if (!Directory.Exists(boundary))
                {
                    error = "物理路径边界不存在或不是目录：" + boundary;
                    return false;
                }
                if (!IsSameOrChild(candidate, boundary, FileSystemPathComparison))
                {
                    error = $"物理路径越过了受信边界：{candidate}（边界：{boundary}）";
                    return false;
                }

                string relative = Path.GetRelativePath(boundary, candidate);
                string[] segments = relative == "."
                    ? Array.Empty<string>()
                    : relative.Split(
                        new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries);
                string current = boundary;
                if (!TryValidateExistingNode(current, allowFile: false, out error)) return false;
                for (int index = 0; index < segments.Length; index++)
                {
                    current = Path.Combine(current, segments[index]);
                    if (!TryGetAttributes(current, out FileAttributes attributes)) break;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "路径不允许穿过符号链接、目录联接或其它 reparse point：" + current;
                        return false;
                    }
                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    if (!isDirectory && index < segments.Length - 1)
                    {
                        error = "路径中的父级已被普通文件占用：" + current;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException or
                IOException or UnauthorizedAccessException)
            {
                error = $"无法验证物理路径：{exception.GetType().Name}: {exception.Message}";
                return false;
            }
        }

        /// <summary>
        /// 递归采集物理目录树，但绝不跟随 symlink、junction 或其它 reparse point。文件名模式只能是
        /// <c>*.proto</c> 这类单段模式，不能包含目录分隔符；任何不安全节点都会让整次采集在返回前失败。
        /// </summary>
        public static PhysicalTreeSnapshot CapturePhysicalTree(
            string absoluteRootDirectory,
            string searchPattern = "*")
        {
            if (string.IsNullOrWhiteSpace(absoluteRootDirectory))
                throw new ArgumentException("扫描根目录不能为空。", nameof(absoluteRootDirectory));
            if (!Path.IsPathRooted(absoluteRootDirectory))
                throw new ArgumentException("扫描根目录必须是绝对路径。", nameof(absoluteRootDirectory));
            ValidateSearchPattern(searchPattern);

            string root = NormalizeAbsolutePath(absoluteRootDirectory);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("扫描根目录不存在：" + root);
            if (!TryValidatePhysicalPath(root, root, out string rootError))
                throw new InvalidDataException(rootError);

            var directories = new List<string>();
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                string[] childDirectories = Directory.GetDirectories(
                    directory, "*", SearchOption.TopDirectoryOnly);
                foreach (string childDirectory in childDirectories)
                {
                    EnsureNotReparsePoint(childDirectory);
                    directories.Add(Path.GetFullPath(childDirectory));
                    pending.Push(childDirectory);
                }

                string[] allFiles = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
                foreach (string file in allFiles) EnsureNotReparsePoint(file);
                IEnumerable<string> matchingFiles = searchPattern == "*"
                    ? allFiles
                    : Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
                files.AddRange(matchingFiles.Select(Path.GetFullPath));
            }

            return new PhysicalTreeSnapshot(
                root,
                directories.Distinct(FileSystemPathComparer)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray(),
                files.Distinct(FileSystemPathComparer)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray());
        }

        /// <summary>
        /// 删除边界内部的一棵目录树。删除前先完整验证目标与全部后代都不是 reparse point，目标不能等于边界；
        /// 因此遇到符号链接时不会先删一半再失败，也不会跟随链接删除边界外内容。
        /// </summary>
        public static void DeleteDirectoryWithinBoundary(
            string absoluteDirectory,
            string boundaryAbsoluteDirectory)
        {
            if (!TryValidatePhysicalPath(
                    boundaryAbsoluteDirectory, absoluteDirectory, out string validationError))
                throw new InvalidOperationException(validationError);
            string directory = NormalizeAbsolutePath(absoluteDirectory);
            string boundary = NormalizeAbsolutePath(boundaryAbsoluteDirectory);
            if (directory.Equals(boundary, FileSystemPathComparison))
                throw new InvalidOperationException("拒绝删除受信边界本身：" + boundary);
            if (!Directory.Exists(directory)) return;

            PhysicalTreeSnapshot tree = CapturePhysicalTree(directory);
            foreach (string file in tree.Files) File.Delete(file);
            foreach (string child in tree.Directories.OrderByDescending(path => path.Length))
                Directory.Delete(child, recursive: false);
            Directory.Delete(directory, recursive: false);
        }

        private static bool IsSameOrChild(string path, string root, StringComparison comparison)
        {
            string normalizedPath = NormalizeAbsolutePath(path);
            string normalizedRoot = NormalizeAbsolutePath(root);
            if (normalizedPath.Equals(normalizedRoot, comparison)) return true;
            string rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), comparison) ||
                                normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar.ToString(), comparison)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(rootPrefix, comparison);
        }

        private static bool LooksLikeAbsolutePath(string path) =>
            Path.IsPathRooted(path) ||
            (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') ||
            path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal);

        private static string NormalizeAbsolutePath(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full) ?? string.Empty;
            return full.Equals(root, FileSystemPathComparison)
                ? full
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool TryValidateExistingNode(string path, bool allowFile, out string error)
        {
            error = string.Empty;
            if (!TryGetAttributes(path, out FileAttributes attributes))
            {
                error = "物理路径节点不存在：" + path;
                return false;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                error = "路径不允许穿过符号链接、目录联接或其它 reparse point：" + path;
                return false;
            }
            if (!allowFile && (attributes & FileAttributes.Directory) == 0)
            {
                error = "物理路径边界不是目录：" + path;
                return false;
            }
            return true;
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default;
                return false;
            }
        }

        private static void EnsureNotReparsePoint(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "递归文件操作不允许符号链接、目录联接或其它 reparse point：" + path);
        }

        private static void ValidateSearchPattern(string searchPattern)
        {
            if (string.IsNullOrWhiteSpace(searchPattern) ||
                Path.IsPathRooted(searchPattern) ||
                searchPattern.IndexOf('/') >= 0 ||
                searchPattern.IndexOf('\\') >= 0 ||
                searchPattern is "." or "..")
                throw new ArgumentException(
                    "文件名模式不能为空、不能是 . / ..，也不能包含根路径或目录分隔符。",
                    nameof(searchPattern));
        }

        private static bool TryFindBlockingFileAncestor(
            string absoluteTargetPath,
            out string blockingAssetPath)
        {
            blockingAssetPath = string.Empty;
            string assetsRoot = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string projectRoot = Directory.GetParent(assetsRoot)?.FullName ?? assetsRoot;
            string current = Path.GetDirectoryName(Path.GetFullPath(absoluteTargetPath));

            while (!string.IsNullOrEmpty(current) &&
                   IsSameOrChild(current, assetsRoot, FileSystemPathComparison))
            {
                if (File.Exists(current))
                {
                    blockingAssetPath = Path.GetRelativePath(projectRoot, current).Replace('\\', '/');
                    return true;
                }

                string normalizedCurrent = Path.GetFullPath(current)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (normalizedCurrent.Equals(assetsRoot, FileSystemPathComparison)) break;
                current = Path.GetDirectoryName(normalizedCurrent);
            }

            return false;
        }

        private static string ToStableProjectError(string physicalError, string projectRoot)
        {
            if (string.IsNullOrEmpty(physicalError)) return string.Empty;
            string normalizedRoot = NormalizeAbsolutePath(projectRoot);
            int rootIndex = physicalError.IndexOf(normalizedRoot, FileSystemPathComparison);
            if (rootIndex < 0) return physicalError;

            int suffixStart = rootIndex + normalizedRoot.Length;
            while (suffixStart < physicalError.Length &&
                   (physicalError[suffixStart] == Path.DirectorySeparatorChar ||
                    physicalError[suffixStart] == Path.AltDirectorySeparatorChar))
                suffixStart++;
            string relative = physicalError[suffixStart..].Replace('\\', '/');
            return physicalError[..rootIndex] + (string.IsNullOrEmpty(relative) ? "." : relative);
        }

        private static StringComparison FileSystemPathComparison =>
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static StringComparer FileSystemPathComparer =>
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        // Unity Asset Path 会入库并在 Windows/macOS/Linux 间流转。所有权检查用最保守的大小写口径，
        // 防止两个仅大小写不同的配置在另一开发机上合并成同一真实产物。
        private const StringComparison PortableAssetPathComparison = StringComparison.OrdinalIgnoreCase;
    }
}
