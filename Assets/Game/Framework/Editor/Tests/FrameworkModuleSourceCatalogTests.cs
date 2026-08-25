using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定 Module 源码在 Assets、Packages 与 PackageCache 中使用同一份可追溯身份。</summary>
    public sealed class FrameworkModuleSourceCatalogTests
    {
        [Test]
        public void AssetsSource_RoundTripsBetweenAssetAndPhysicalPath()
        {
            string asmdefPath = AssetDatabase.FindAssets("Game.Framework.Editor t:AssemblyDefinitionAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .First(path => Path.GetFileName(path) == "Game.Framework.Editor.asmdef");

            FrameworkModuleSourceCatalog.SourceLocation fromAsset =
                FrameworkModuleSourceCatalog.Resolve(asmdefPath);
            FrameworkModuleSourceCatalog.SourceLocation fromPhysical =
                FrameworkModuleSourceCatalog.Resolve(fromAsset.PhysicalPath);

            Assert.That(fromAsset.AssetPath, Is.EqualTo(asmdefPath.Replace('\\', '/')));
            Assert.That(File.Exists(fromAsset.PhysicalPath), Is.True);
            Assert.That(fromAsset.IsPackage, Is.False);
            Assert.That(fromPhysical.AssetPath, Is.EqualTo(fromAsset.AssetPath));
        }

        [Test]
        public void RegisteredPackageSource_ResolvesToExistingPhysicalFile()
        {
            string packageAsmdef = AssetDatabase.GetAllAssetPaths()
                .FirstOrDefault(path => path.StartsWith("Packages/") &&
                                        path.EndsWith(".asmdef") &&
                                        TryResolveExistingFile(path));
            if (string.IsNullOrEmpty(packageAsmdef))
                Assert.Ignore("当前极简工程没有可解析的已注册 Package asmdef。");

            FrameworkModuleSourceCatalog.SourceLocation source =
                FrameworkModuleSourceCatalog.Resolve(packageAsmdef);
            FrameworkModuleSourceCatalog.SourceLocation roundTrip =
                FrameworkModuleSourceCatalog.Resolve(source.PhysicalPath);

            Assert.That(source.IsPackage, Is.True);
            Assert.That(source.PackageName, Is.Not.Empty);
            Assert.That(source.PackageVersion, Is.Not.Empty);
            Assert.That(File.Exists(source.PhysicalPath), Is.True);
            Assert.That(roundTrip.AssetPath, Is.EqualTo(source.AssetPath));
            Assert.That(roundTrip.PackageName, Is.EqualTo(source.PackageName));
            Assert.That(source.PackageId, Is.Not.Empty);
        }

        [Test]
        public void RegisteredPackageRoot_ResolvesAsDirectory()
        {
            string packageAsset = AssetDatabase.GetAllAssetPaths()
                .FirstOrDefault(path => path.StartsWith("Packages/") && TryResolvePackage(path));
            if (string.IsNullOrEmpty(packageAsset))
                Assert.Ignore("当前极简工程没有已注册 Package 资产。");

            FrameworkModuleSourceCatalog.SourceLocation file =
                FrameworkModuleSourceCatalog.Resolve(packageAsset);
            FrameworkModuleSourceCatalog.SourceLocation root =
                FrameworkModuleSourceCatalog.Resolve(file.AssetRoot);

            Assert.That(root.IsPackage, Is.True);
            Assert.That(Directory.Exists(root.PhysicalPath), Is.True);
            Assert.That(root.PackageName, Is.EqualTo(file.PackageName));

            bool escaped = FrameworkModuleSourceCatalog.TryResolve(
                root.AssetRoot + "/../manifest.json", out _, out string reason);
            Assert.That(escaped, Is.False);
            Assert.That(reason, Does.Contain("逃逸源码根"));
        }

        [Test]
        public void PackageCacheSource_RoundTripsWithoutProjectRelativeIoAssumption()
        {
            FrameworkModuleSourceCatalog.SourceLocation cached = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Packages/") && path.EndsWith(".asmdef"))
                .Select(TryResolveExistingSource)
                .FirstOrDefault(source => source != null &&
                                          source.PhysicalPath.Replace('\\', '/')
                                              .Contains("/Library/PackageCache/"));
            if (cached == null)
                Assert.Ignore("当前工程没有位于 Library/PackageCache 的已注册 Package asmdef。");

            FrameworkModuleSourceCatalog.SourceLocation roundTrip =
                FrameworkModuleSourceCatalog.Resolve(cached.PhysicalPath);

            Assert.That(File.Exists(cached.PhysicalPath), Is.True);
            Assert.That(cached.AssetPath, Does.StartWith("Packages/"));
            Assert.That(roundTrip.AssetPath, Is.EqualTo(cached.AssetPath));
            Assert.That(roundTrip.PackageName, Is.EqualTo(cached.PackageName));
        }

        [Test]
        public void EscapingAssetRoot_IsRejected()
        {
            bool resolved = FrameworkModuleSourceCatalog.TryResolve(
                "Assets/../Packages/manifest.json", out _, out string reason);

            Assert.That(resolved, Is.False);
            Assert.That(reason, Does.Contain("逃逸项目目录"));
        }

        [Test]
        public void DotSegments_AreCanonicalizedToOneStableAssetIdentity()
        {
            string canonical = AssetDatabase.FindAssets("Game.Framework.Editor t:AssemblyDefinitionAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .First(path => Path.GetFileName(path) == "Game.Framework.Editor.asmdef")
                .Replace('\\', '/');
            string directory = Path.GetDirectoryName(canonical)?.Replace('\\', '/');
            string dotted = directory + "/../" + Path.GetFileName(directory) + "/" +
                            Path.GetFileName(canonical);

            FrameworkModuleSourceCatalog.SourceLocation source =
                FrameworkModuleSourceCatalog.Resolve(dotted);
            FrameworkModuleSourceCatalog.SourceLocation duplicateSeparator =
                FrameworkModuleSourceCatalog.Resolve(canonical.Replace("Assets/", "Assets//"));

            Assert.That(source.AssetPath, Is.EqualTo(canonical));
            Assert.That(duplicateSeparator.AssetPath, Is.EqualTo(canonical));
            Assert.That(FrameworkModuleSourceCatalog.Resolve(source.PhysicalPath).AssetPath,
                Is.EqualTo(canonical));
        }

        [Test]
        public void KnownAssetCandidates_FailInsteadOfSilentlyDroppingUnreadableEvidence()
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                FrameworkModuleSourceCatalog.ResolveKnownAssetPaths(new[]
                {
                    "Assets/../Packages/manifest.json",
                }));

            Assert.That(exception?.Message, Does.Contain("拒绝生成不完整证据"));
        }

        [Test]
        public void LinkerSources_ExposeStableAssetPathAndReadablePhysicalPath()
        {
            FrameworkModuleSourceCatalog.SourceLocation[] sources =
                FrameworkModuleSourceCatalog.EnumerateFiles("link.xml");

            Assert.That(sources, Is.Not.Empty);
            Assert.That(sources.Select(source => source.AssetPath), Is.Unique);
            Assert.That(sources.All(source => File.Exists(source.PhysicalPath)), Is.True);
            Assert.That(sources, Has.Some.Matches<FrameworkModuleSourceCatalog.SourceLocation>(source =>
                source.AssetPath.StartsWith("Packages/") || source.AssetPath.StartsWith("Assets/")));
            if (AssetDatabase.GetAllAssetPaths().Any(path =>
                    path.StartsWith("Packages/") && Path.GetFileName(path) == "link.xml"))
                Assert.That(sources.Any(source => source.IsPackage), Is.True,
                    "已注册 Package 的 linker 根必须进入与项目 Assets 相同的审计证据。");
        }

        [Test]
        public void ProbeTemplate_IsFoundWithoutRepositorySpecificRoot()
        {
            FrameworkModuleSourceCatalog.SourceLocation source =
                FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                    FrameworkBuildSizeProbe.ChildTemplateFileName,
                    FrameworkModuleAudit.CoreAssemblyName + ".Editor");

            Assert.That(source.AssetPath, Does.EndWith("/" + FrameworkBuildSizeProbe.ChildTemplateFileName));
            Assert.That(File.Exists(source.PhysicalPath), Is.True);
        }

        private static bool TryResolveExistingFile(string path) =>
            FrameworkModuleSourceCatalog.TryResolve(path, out var source, out _) &&
            File.Exists(source.PhysicalPath);

        private static bool TryResolvePackage(string path) =>
            FrameworkModuleSourceCatalog.TryResolve(path, out var source, out _) && source.IsPackage;

        private static FrameworkModuleSourceCatalog.SourceLocation TryResolveExistingSource(string path) =>
            FrameworkModuleSourceCatalog.TryResolve(path, out var source, out _) &&
            File.Exists(source.PhysicalPath)
                ? source
                : null;
    }
}
