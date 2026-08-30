using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;
using UpmPackageSource = UnityEditor.PackageManager.PackageSource;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 统一解析 Framework Module 的 Unity Asset Path、真实物理路径与 Package 所有权。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity 对 <c>Assets/...</c> 与 <c>Packages/...</c> 都暴露稳定资产路径，但 registry/Git 包的真实文件位于
    /// <c>Library/PackageCache</c> 或外部缓存；直接把 Asset Path 交给 <see cref="File"/> 会得到不存在的路径。
    /// 本 Catalog 把这项布局差异收口，调用者只消费同一份 <see cref="SourceLocation"/> 证据。
    /// </para>
    /// <para>
    /// Catalog 只描述已安装源码，不接管 Package Manager 的安装、版本解析或卸载。
    /// </para>
    /// </remarks>
    internal static class FrameworkModuleSourceCatalog
    {
        private static UpmPackageInfo[] _registeredPackages;

        static FrameworkModuleSourceCatalog()
        {
            UnityEditor.PackageManager.Events.registeredPackages += _ => _registeredPackages = null;
        }

        /// <summary>源码在当前工程中的安装形态；只描述来源，不推导能否安全移除。</summary>
        internal enum SourceKind
        {
            ProjectAssets,
            BuiltInPackage,
            EmbeddedPackage,
            GitPackage,
            LocalPackage,
            LocalTarballPackage,
            RegistryPackage,
            UnknownPackage,
        }

        internal sealed class SourceLocation
        {
            internal string AssetPath = string.Empty;
            internal string PhysicalPath = string.Empty;
            internal string AssetRoot = string.Empty;
            internal string PhysicalRoot = string.Empty;
            internal string PackageName = string.Empty;
            internal string PackageVersion = string.Empty;
            internal string PackageId = string.Empty;
            internal SourceKind Kind = SourceKind.ProjectAssets;
            internal bool HasPackageDirectness;
            internal bool IsDirectPackageDependency;

            internal bool IsPackage => !string.IsNullOrEmpty(PackageName);
            internal string AssetDirectory => NormalizeAssetPath(Path.GetDirectoryName(AssetPath));
            internal string PhysicalDirectory => Path.GetDirectoryName(PhysicalPath) ?? string.Empty;
        }

        internal static SourceLocation Resolve(string assetOrPhysicalPath)
        {
            if (!TryResolve(assetOrPhysicalPath, out SourceLocation location, out string reason))
                throw new InvalidOperationException(reason);
            return location;
        }

        internal static bool TryResolve(
            string assetOrPhysicalPath,
            out SourceLocation location,
            out string reason)
        {
            location = null;
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(assetOrPhysicalPath))
            {
                reason = "源码路径为空。";
                return false;
            }

            string normalized = NormalizeAssetPath(assetOrPhysicalPath.Trim());
            if (Path.IsPathRooted(assetOrPhysicalPath))
                return TryResolvePhysical(assetOrPhysicalPath, out location, out reason);
            if (normalized.Equals("Assets", StringComparison.Ordinal) ||
                normalized.StartsWith("Assets/", StringComparison.Ordinal))
                return TryResolveAssets(normalized, out location, out reason);
            if (normalized.StartsWith("Packages/", StringComparison.Ordinal))
                return TryResolvePackageAsset(normalized, out location, out reason);

            reason = $"只支持 Assets、Packages 或绝对物理路径：{assetOrPhysicalPath}";
            return false;
        }

        /// <summary>枚举当前项目 Assets 与全部已注册 Package 中指定文件名的真实源码。</summary>
        internal static SourceLocation[] EnumerateFiles(string fileName) =>
            EnumerateFiles(fileName, AssetDatabase.GetAllAssetPaths());

        /// <summary>
        /// 从调用方同一轮采集的 AssetDatabase 路径快照枚举指定文件，避免一次审计为不同证据重复扫描全工程。
        /// </summary>
        internal static SourceLocation[] EnumerateFiles(
            string fileName,
            System.Collections.Generic.IEnumerable<string> knownAssetPaths)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new ArgumentException("只能按单个文件名枚举。", nameof(fileName));
            if (knownAssetPaths == null) throw new ArgumentNullException(nameof(knownAssetPaths));

            return ResolveKnownAssetPaths(knownAssetPaths
                    .Where(path => string.Equals(
                        Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(item => item.AssetPath, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// 将 AssetDatabase 已知的候选严格还原为物理源码。候选无法解析或物理缺失时必须失败，
        /// 避免审计把“证据不可读”静默解释成“没有这份证据”。
        /// </summary>
        internal static SourceLocation[] ResolveKnownAssetPaths(System.Collections.Generic.IEnumerable<string> paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            var locations = new System.Collections.Generic.List<SourceLocation>();
            var issues = new System.Collections.Generic.List<string>();
            foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                if (!TryResolve(path, out SourceLocation location, out string reason))
                {
                    issues.Add(path + " → " + reason);
                    continue;
                }
                if (!File.Exists(location.PhysicalPath))
                {
                    issues.Add(location.AssetPath + " → 物理文件不存在：" + location.PhysicalPath);
                    continue;
                }
                locations.Add(location);
            }
            if (issues.Count > 0)
                throw new InvalidDataException(
                    "AssetDatabase 已登记源码，但 Source Catalog 无法读取；拒绝生成不完整证据：\n  " +
                    string.Join("\n  ", issues));
            return locations
                .GroupBy(location => location.AssetPath, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }

        /// <summary>只在指定程序集的源码域内查找唯一文件，避免被无关 Package 的同名文件干扰。</summary>
        internal static SourceLocation FindUniqueFileInAssemblySource(string fileName, string assemblyName)
        {
            if (TryFindUniqueFileInAssemblySource(fileName, assemblyName, out SourceLocation location))
                return location;
            throw new FileNotFoundException($"程序集 {assemblyName} 的源码域中找不到 {fileName}。");
        }

        internal static bool TryFindUniqueFileInAssemblySource(
            string fileName,
            string assemblyName,
            out SourceLocation location)
        {
            location = null;
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new ArgumentException("只能按单个文件名查找。", nameof(fileName));
            if (string.IsNullOrWhiteSpace(assemblyName))
                throw new ArgumentException("程序集名为空。", nameof(assemblyName));

            string asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
            if (string.IsNullOrWhiteSpace(asmdefPath)) return false;
            SourceLocation asmdef = Resolve(asmdefPath);
            string sourceAssetDirectory = asmdef.AssetDirectory;
            SourceLocation[] matches = ResolveKnownAssetPaths(AssetDatabase.GetAllAssetPaths()
                    .Where(path => string.Equals(
                        Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                    .Where(path => path.StartsWith(
                        sourceAssetDirectory + "/", StringComparison.Ordinal)))
                .Where(candidate => !candidate.AssetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                                    AssemblyNameEquals(
                                        CompilationPipeline.GetAssemblyNameFromScriptPath(candidate.AssetPath),
                                        assemblyName))
                .ToArray();
            if (matches.Length == 0) return false;
            if (matches.Length > 1)
                throw new InvalidOperationException(
                    $"程序集 {assemblyName} 的源码域中找到多个 {fileName}：\n  " +
                    string.Join("\n  ", matches.Select(item => item.AssetPath)));
            location = matches[0];
            return true;
        }

        private static bool TryResolveAssets(
            string assetPath,
            out SourceLocation location,
            out string reason)
        {
            string assetsRoot = Path.GetFullPath(Application.dataPath);
            string relative = assetPath.Length == "Assets".Length
                ? string.Empty
                : assetPath.Substring("Assets/".Length);
            string physical = Path.GetFullPath(Path.Combine(assetsRoot, relative));
            if (!IsInside(physical, assetsRoot))
            {
                location = null;
                reason = "Assets 路径规范化后逃逸项目目录：" + assetPath;
                return false;
            }
            string canonicalRelative = RelativeTo(assetsRoot, physical);
            string canonicalAssetPath = string.IsNullOrEmpty(canonicalRelative)
                ? "Assets"
                : "Assets/" + canonicalRelative;
            location = CreateLocation(
                canonicalAssetPath, physical, "Assets", assetsRoot,
                string.Empty, string.Empty, string.Empty,
                SourceKind.ProjectAssets, false, false);
            reason = string.Empty;
            return true;
        }

        private static bool TryResolvePackageAsset(
            string assetPath,
            out SourceLocation location,
            out string reason)
        {
            UpmPackageInfo package = FindOwningPackage(assetPath);
            if (package == null || string.IsNullOrWhiteSpace(package.resolvedPath))
            {
                location = null;
                reason = "找不到资产所属的已注册 Package：" + assetPath;
                return false;
            }

            string assetRoot = NormalizeAssetPath(package.assetPath);
            if (!assetPath.Equals(assetRoot, StringComparison.Ordinal) &&
                !assetPath.StartsWith(assetRoot + "/", StringComparison.Ordinal))
            {
                location = null;
                reason = $"Package 资产路径不位于 {assetRoot}：{assetPath}";
                return false;
            }
            string relative = assetPath.Length == assetRoot.Length
                ? string.Empty
                : assetPath.Substring(assetRoot.Length + 1);
            string physicalRoot = Path.GetFullPath(package.resolvedPath);
            string physical = Path.GetFullPath(Path.Combine(physicalRoot, relative));
            if (!IsInside(physical, physicalRoot))
            {
                location = null;
                reason = "Package 路径规范化后逃逸源码根：" + assetPath;
                return false;
            }
            string canonicalRelative = RelativeTo(physicalRoot, physical);
            string canonicalAssetPath = string.IsNullOrEmpty(canonicalRelative)
                ? assetRoot
                : assetRoot + "/" + canonicalRelative;
            location = CreateLocation(
                canonicalAssetPath,
                physical,
                assetRoot,
                physicalRoot,
                package.name,
                package.version,
                package.packageId,
                ClassifyPackageSource(package.source),
                true,
                package.isDirectDependency);
            reason = string.Empty;
            return true;
        }

        private static UpmPackageInfo FindOwningPackage(string assetPath)
        {
            UpmPackageInfo direct = UpmPackageInfo.FindForAssetPath(assetPath);
            if (direct != null) return direct;

            // FindForAssetPath 在部分 Unity 版本中无法从 "Packages/<name>" 根目录命中，
            // 但 Build Size Probe 需要复制整个 package。注册表回退让文件与目录共享同一契约。
            return RegisteredPackages()
                .Where(package => !string.IsNullOrWhiteSpace(package.assetPath))
                .OrderByDescending(package => NormalizeAssetPath(package.assetPath).Length)
                .FirstOrDefault(package =>
                {
                    string root = NormalizeAssetPath(package.assetPath);
                    return assetPath.Equals(root, StringComparison.Ordinal) ||
                           assetPath.StartsWith(root + "/", StringComparison.Ordinal);
                });
        }

        private static bool TryResolvePhysical(
            string physicalPath,
            out SourceLocation location,
            out string reason)
        {
            string normalized = Path.GetFullPath(physicalPath);
            string assetsRoot = Path.GetFullPath(Application.dataPath);
            if (IsInside(normalized, assetsRoot))
            {
                string relative = RelativeTo(assetsRoot, normalized);
                location = CreateLocation(
                    string.IsNullOrEmpty(relative) ? "Assets" : "Assets/" + relative,
                    normalized,
                    "Assets",
                    assetsRoot,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    SourceKind.ProjectAssets,
                    false,
                    false);
                reason = string.Empty;
                return true;
            }

            foreach (UpmPackageInfo package in RegisteredPackages())
            {
                if (string.IsNullOrWhiteSpace(package.resolvedPath)) continue;
                string physicalRoot = Path.GetFullPath(package.resolvedPath);
                if (!IsInside(normalized, physicalRoot)) continue;
                string relative = RelativeTo(physicalRoot, normalized);
                string assetRoot = NormalizeAssetPath(package.assetPath);
                location = CreateLocation(
                    string.IsNullOrEmpty(relative) ? assetRoot : assetRoot + "/" + relative,
                    normalized,
                    assetRoot,
                    physicalRoot,
                    package.name,
                    package.version,
                    package.packageId,
                    ClassifyPackageSource(package.source),
                    true,
                    package.isDirectDependency);
                reason = string.Empty;
                return true;
            }

            location = null;
            reason = "物理路径不属于当前项目 Assets 或任何已注册 Package：" + normalized;
            return false;
        }

        private static SourceLocation CreateLocation(
            string assetPath,
            string physicalPath,
            string assetRoot,
            string physicalRoot,
            string packageName,
            string packageVersion,
            string packageId,
            SourceKind kind,
            bool hasPackageDirectness,
            bool isDirectPackageDependency) => new()
        {
            AssetPath = NormalizeAssetPath(assetPath),
            PhysicalPath = Path.GetFullPath(physicalPath),
            AssetRoot = NormalizeAssetPath(assetRoot),
            PhysicalRoot = Path.GetFullPath(physicalRoot),
            PackageName = packageName ?? string.Empty,
            PackageVersion = packageVersion ?? string.Empty,
            PackageId = packageId ?? string.Empty,
            Kind = kind,
            HasPackageDirectness = hasPackageDirectness,
            IsDirectPackageDependency = isDirectPackageDependency,
        };

        internal static SourceKind ClassifyPackageSource(UpmPackageSource source) => source switch
        {
            UpmPackageSource.BuiltIn => SourceKind.BuiltInPackage,
            UpmPackageSource.Embedded => SourceKind.EmbeddedPackage,
            UpmPackageSource.Git => SourceKind.GitPackage,
            UpmPackageSource.Local => SourceKind.LocalPackage,
            UpmPackageSource.LocalTarball => SourceKind.LocalTarballPackage,
            UpmPackageSource.Registry => SourceKind.RegistryPackage,
            _ => SourceKind.UnknownPackage,
        };

        private static UpmPackageInfo[] RegisteredPackages() =>
            _registeredPackages ??= UpmPackageInfo.GetAllRegisteredPackages()
                .Where(package => package != null)
                .OrderBy(package => package.name, StringComparer.Ordinal)
                .ToArray();

        private static string RelativeTo(string root, string path)
        {
            string relative = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return NormalizeAssetPath(relative);
        }

        internal static bool IsPhysicalPathInside(string path, string root)
        {
            string normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.Equals(normalizedRoot, PathComparison) ||
                   normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison);
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string normalized = path.Replace('\\', '/');
            while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
            return normalized.TrimEnd('/');
        }

        private static bool AssemblyNameEquals(string reportedName, string assemblyName) =>
            string.Equals(
                reportedName?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true
                    ? reportedName.Substring(0, reportedName.Length - ".dll".Length)
                    : reportedName,
                assemblyName,
                StringComparison.Ordinal);

        private static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static bool IsInside(string path, string root) => IsPhysicalPathInside(path, root);
    }
}
