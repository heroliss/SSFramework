using System;
using System.IO;
using NUnit.Framework;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定包名 / 版本号不能改变构建与部署目录层级，尤其保护递归清理边界。</summary>
    public sealed class FrameworkBuildArtifactPathTests
    {
        [TestCase("DefaultPackage")]
        [TestCase("code-package_1.2.3+build")]
        [TestCase("资源包-1")]
        public void NormalizeSegment_AcceptsPortableLeafNames(string value)
        {
            Assert.That(FrameworkBuildArtifactPath.TryNormalizeSegment(
                    value, "测试名称", out string normalized, out string error), Is.True, error);
            Assert.That(normalized, Is.EqualTo(value));
        }

        [TestCase("../Escape")]
        [TestCase("..\\Escape")]
        [TestCase("C:/Escape")]
        [TestCase("Package/Child")]
        [TestCase("Package Name")]
        [TestCase("Package#Fragment")]
        [TestCase("CON")]
        [TestCase("NUL.txt")]
        [TestCase(".")]
        [TestCase("..")]
        public void NormalizeSegment_RejectsTraversalAndNonPortableNames(string value)
        {
            Assert.That(FrameworkBuildArtifactPath.TryNormalizeSegment(
                    value, "测试名称", out _, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void Build_RejectsTraversalVersionBeforeStartingPipeline()
        {
            var result = FrameworkAssetBuilder.Build(
                profile: null,
                packages: new[] { "DefaultPackage" },
                version: "../Escape");

            Assert.That(result.ok, Is.False);
            Assert.That(result.message, Does.Contain("版本预检失败"));
        }

        [Test]
        public void Deploy_RejectsTraversalPackageBeforeDeletingOutsideRoot()
        {
            string testRoot = Path.Combine("Library", "SSFrameworkBuildPathTests_" + Guid.NewGuid().ToString("N"));
            string deployRoot = Path.Combine(testRoot, "Deploy");
            string outsideDirectory = Path.Combine(testRoot, "Outside");
            string sentinel = Path.Combine(outsideDirectory, "keep.txt");
            try
            {
                Directory.CreateDirectory(outsideDirectory);
                File.WriteAllText(sentinel, "keep");

                var result = FrameworkAssetBuilder.Deploy(new[] { "../Outside" }, deployRoot);

                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("包名预检失败"));
                Assert.That(File.Exists(sentinel), Is.True, "非法包名不得触达 Deploy 根之外的既有目录。");
            }
            finally
            {
                if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
            }
        }

        [Test]
        public void Deploy_RejectsCaseOnlyDuplicatePackageDirectories()
        {
            var result = FrameworkAssetBuilder.Deploy(
                new[] { "Package", "package" },
                Path.Combine("Library", "SSFrameworkBuildPathTests", "Deploy"));

            Assert.That(result.ok, Is.False);
            Assert.That(result.message, Does.Contain("大小写"));
        }
    }
}
