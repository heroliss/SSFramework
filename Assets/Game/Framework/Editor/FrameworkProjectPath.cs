using System;
using System.IO;
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

        private static bool IsSameOrChild(string path, string root, StringComparison comparison)
        {
            string normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalizedPath.Equals(normalizedRoot, comparison)) return true;
            return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
        }

        private static bool LooksLikeAbsolutePath(string path) =>
            Path.IsPathRooted(path) ||
            (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') ||
            path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal);

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

        private static StringComparison FileSystemPathComparison =>
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        // Unity Asset Path 会入库并在 Windows/macOS/Linux 间流转。所有权检查用最保守的大小写口径，
        // 防止两个仅大小写不同的配置在另一开发机上合并成同一真实产物。
        private const StringComparison PortableAssetPathComparison = StringComparison.OrdinalIgnoreCase;
    }
}
