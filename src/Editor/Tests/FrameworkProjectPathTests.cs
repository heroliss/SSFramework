using System.IO;
using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定生成器共享的工程路径边界，防止字符串前缀检查被父目录跳转绕过。</summary>
    public sealed class FrameworkProjectPathTests
    {
        [Test]
        public void AssetsFile_NormalizesSafePathWithoutWriting()
        {
            string uniqueName = "PathOnly_" + Guid.NewGuid().ToString("N") + ".g.cs";
            bool ok = FrameworkProjectPath.TryResolveAssetsFile(
                "Assets/Generated/../Generated/" + uniqueName,
                ".cs",
                out string assetPath,
                out string absolutePath,
                out string error);

            Assert.That(ok, Is.True, error);
            Assert.That(assetPath, Is.EqualTo("Assets/Generated/" + uniqueName));
            Assert.That(absolutePath, Is.EqualTo(Path.GetFullPath(
                Path.Combine(Application.dataPath, "Generated", uniqueName))));
            Assert.That(File.Exists(absolutePath), Is.False, "路径解析本身不得创建输出文件。");
        }

        [Test]
        public void AssetsFile_NormalizesWindowsSeparatorsOnEveryEditorPlatform()
        {
            bool ok = FrameworkProjectPath.TryResolveAssetsFile(
                @"Assets\Generated\Portable.g.cs", ".cs",
                out string assetPath, out _, out string error);

            Assert.That(ok, Is.True, error);
            Assert.That(assetPath, Is.EqualTo("Assets/Generated/Portable.g.cs"));
        }

        [TestCase("Assets/../ProjectSettings/Escape.cs")]
        [TestCase("Assets/../../Escape.cs")]
        [TestCase("ProjectSettings/Escape.cs")]
        [TestCase("C:/Escape.cs")]
        public void AssetsFile_RejectsPathsOutsideAssets(string configuredPath)
        {
            Assert.That(FrameworkProjectPath.TryResolveAssetsFile(
                    configuredPath, ".cs", out _, out _, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void GeneratedDirectory_RejectsAssetsRoot()
        {
            Assert.That(FrameworkProjectPath.TryResolveAssetsDirectory(
                    "Assets", out _, out _, out string error), Is.False);
            Assert.That(error, Does.Contain("根目录"));
        }

        [Test]
        public void OutputKind_RejectsDirectoryPathOccupiedByFileAndFilePathOccupiedByDirectory()
        {
            string fileAssetPath = "Assets/FrameworkPathKindFile_" + Guid.NewGuid().ToString("N");
            string directoryAssetPath = "Assets/FrameworkPathKindDirectory_" + Guid.NewGuid().ToString("N") + ".txt";
            string fileAbsolutePath = Path.GetFullPath(fileAssetPath);
            string directoryAbsolutePath = Path.GetFullPath(directoryAssetPath);
            try
            {
                File.WriteAllText(fileAbsolutePath, "occupied");
                Directory.CreateDirectory(directoryAbsolutePath);

                Assert.That(FrameworkProjectPath.TryResolveAssetsDirectory(
                    fileAssetPath, out _, out _, out string directoryError), Is.False);
                Assert.That(directoryError, Does.Contain("目标当前是普通文件"));

                Assert.That(FrameworkProjectPath.TryResolveAssetsFile(
                    directoryAssetPath, ".txt", out _, out _, out string fileError), Is.False);
                Assert.That(fileError, Does.Contain("目标当前是目录"));
            }
            finally
            {
                if (File.Exists(fileAbsolutePath)) File.Delete(fileAbsolutePath);
                if (Directory.Exists(directoryAbsolutePath)) Directory.Delete(directoryAbsolutePath, true);
                if (File.Exists(fileAbsolutePath + ".meta")) File.Delete(fileAbsolutePath + ".meta");
                if (File.Exists(directoryAbsolutePath + ".meta")) File.Delete(directoryAbsolutePath + ".meta");
            }
        }

        [Test]
        public void Outputs_RejectParentPathOccupiedByFile()
        {
            string ancestorAssetPath = "Assets/FrameworkPathAncestor_" + Guid.NewGuid().ToString("N");
            string ancestorAbsolutePath = Path.GetFullPath(ancestorAssetPath);
            try
            {
                File.WriteAllText(ancestorAbsolutePath, "occupied");

                Assert.That(FrameworkProjectPath.TryResolveAssetsDirectory(
                    ancestorAssetPath + "/Generated",
                    out _, out _, out string directoryError), Is.False);
                Assert.That(directoryError, Does.Contain("父级已被普通文件占用").And.Contain(ancestorAssetPath));

                Assert.That(FrameworkProjectPath.TryResolveAssetsFile(
                    ancestorAssetPath + "/Generated/Output.txt", ".txt",
                    out _, out _, out string fileError), Is.False);
                Assert.That(fileError, Does.Contain("父级已被普通文件占用").And.Contain(ancestorAssetPath));
            }
            finally
            {
                if (File.Exists(ancestorAbsolutePath)) File.Delete(ancestorAbsolutePath);
                if (File.Exists(ancestorAbsolutePath + ".meta")) File.Delete(ancestorAbsolutePath + ".meta");
            }
        }

        [Test]
        public void DirectoryOwnership_DetectsSameAndNestedPathsOnly()
        {
            string assets = Application.dataPath;
            string first = Path.Combine(assets, "Generated", "ProtoA");
            string child = Path.Combine(first, "Nested");
            string sibling = Path.Combine(assets, "Generated", "ProtoB");

            Assert.That(FrameworkProjectPath.DirectoriesOverlap(first, first), Is.True);
            Assert.That(FrameworkProjectPath.DirectoriesOverlap(first, child), Is.True);
            Assert.That(FrameworkProjectPath.DirectoriesOverlap(first, sibling), Is.False);
            Assert.That(FrameworkProjectPath.DirectoriesOverlap(first, Path.Combine(assets, "generated", "protoa")),
                Is.True, "Assets 输出所有权要按跨平台最保守的大小写语义判断。");
        }
    }
}
