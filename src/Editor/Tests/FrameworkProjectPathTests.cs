using System.IO;
using System;
using System.Diagnostics;
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

        [Test]
        public void RecursiveOperations_RejectJunctionBeforeReadingOrDeletingItsTarget()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("此用例用 Windows junction 验证 reparse-point 集成边界；其它平台仍走同一 FileAttributes 契约。 ");

            string id = Guid.NewGuid().ToString("N");
            string rootAssetPath = "Assets/FrameworkPathReparse_" + id;
            string root = Path.GetFullPath(rootAssetPath);
            string target = Path.GetFullPath(Path.Combine("Temp", "FrameworkPathTarget_" + id));
            string junction = Path.Combine(root, "LinkedOutside");
            string marker = Path.Combine(target, "must-survive.txt");
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(target);
                File.WriteAllText(marker, "outside");
                if (!TryCreateDirectoryJunction(junction, target, out string junctionError))
                    Assert.Ignore("当前 Windows 环境无法创建测试 junction：" + junctionError);

                Assert.That(FrameworkProjectPath.TryResolveAssetsDirectory(
                    rootAssetPath + "/LinkedOutside/Generated",
                    out _, out _, out string resolveError), Is.False);
                Assert.That(resolveError, Does.Contain("目录联接"));

                Assert.Throws<InvalidDataException>(() =>
                    FrameworkProjectPath.CapturePhysicalTree(root));
                Assert.Throws<InvalidDataException>(() =>
                    FrameworkProjectPath.DeleteDirectoryWithinBoundary(root, Application.dataPath));
                Assert.That(File.Exists(marker), Is.True,
                    "安全扫描或删除遇到 junction 时必须在任何变更前失败，不能触及链接目标。 ");
            }
            finally
            {
                RemoveDirectoryJunction(junction);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                if (File.Exists(root + ".meta")) File.Delete(root + ".meta");
            }
        }

        [TestCase("../*.cs")]
        [TestCase("Nested/*.cs")]
        [TestCase(".")]
        public void PhysicalTree_RejectsPatternsThatCanEscapeOrChangeTheScanRoot(string pattern)
        {
            Assert.Throws<ArgumentException>(() =>
                FrameworkProjectPath.CapturePhysicalTree(Application.dataPath, pattern));
        }

        private static bool TryCreateDirectoryJunction(string junction, string target, out string error)
        {
            var startInfo = new ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                $"/d /c mklink /J \"{junction}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "无法启动 mklink。";
                return false;
            }
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { /* 测试清理尽力而为。 */ }
                error = "mklink 超时。";
                return false;
            }
            error = (standardError + standardOutput).Trim();
            return process.ExitCode == 0 && Directory.Exists(junction) &&
                   (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0;
        }

        private static void RemoveDirectoryJunction(string junction)
        {
            if (!Directory.Exists(junction)) return;
            FileAttributes attributes = File.GetAttributes(junction);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(junction, recursive: false);
        }
    }
}
