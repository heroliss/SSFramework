using System;
using System.IO;
using Game.Framework.Editor;
using NUnit.Framework;
using UnityEditor;

namespace Game.Framework.Build.Tests
{
    /// <summary>
    /// 锁定 CI 构建后部署只消费本轮精确产物；人工 latest 部署仍可独立重做，不把两种所有权语义混在一起。
    /// </summary>
    public sealed class FrameworkAssetBatchDeploymentTests
    {
        private string _testRoot;
        private string _targetBuildRoot;

        [SetUp]
        public void SetUp()
        {
            string id = Guid.NewGuid().ToString("N");
            _testRoot = Path.GetFullPath(Path.Combine("Library", "SSFrameworkBatchDeployTests_" + id));
            _targetBuildRoot = Path.GetFullPath(Path.Combine(
                AssetBuildLayout.BundlesRoot,
                EditorUserBuildSettings.activeBuildTarget.ToString()));
            Directory.CreateDirectory(_testRoot);
            Directory.CreateDirectory(_targetBuildRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testRoot))
                FrameworkProjectPath.DeleteDirectoryWithinBoundary(
                    _testRoot,
                    Path.GetFullPath("Library"));
        }

        [Test]
        public void DeployBatch_UsesExactCurrentVersionAndRemovesSkippedOldDeployment()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string builtPackage = "BatchBuilt_" + suffix;
            string skippedPackage = "BatchSkipped_" + suffix;
            string version = "current_" + suffix;
            string historicalVersion = "historical_" + suffix;
            string builtPackageRoot = Path.Combine(_targetBuildRoot, builtPackage);
            string skippedPackageRoot = Path.Combine(_targetBuildRoot, skippedPackage);
            string currentSource = Path.Combine(builtPackageRoot, version);
            string historicalSource = Path.Combine(builtPackageRoot, historicalVersion);
            string deployRoot = Path.Combine(_testRoot, "Deploy");
            string unrelatedDeployment = Path.Combine(deployRoot, "Unrelated");

            try
            {
                Directory.CreateDirectory(currentSource);
                File.WriteAllText(Path.Combine(currentSource, "current.manifest"), "current");
                Directory.CreateDirectory(historicalSource);
                File.WriteAllText(Path.Combine(historicalSource, "historical.manifest"), "historical");
                Directory.SetLastWriteTimeUtc(historicalSource, DateTime.UtcNow.AddMinutes(5));

                Directory.CreateDirectory(Path.Combine(deployRoot, builtPackage));
                File.WriteAllText(Path.Combine(deployRoot, builtPackage, "stale.txt"), "stale");
                Directory.CreateDirectory(Path.Combine(deployRoot, skippedPackage));
                File.WriteAllText(Path.Combine(deployRoot, skippedPackage, "old.manifest"), "old");
                Directory.CreateDirectory(unrelatedDeployment);
                File.WriteAllText(Path.Combine(unrelatedDeployment, "keep.txt"), "keep");

                var batch = new FrameworkAssetBuilder.BuildBatchResult(
                    ok: true,
                    message: "build ok",
                    version: version,
                    requestedPackages: new[] { builtPackage, skippedPackage },
                    builtPackages: new[] { builtPackage });

                var result = FrameworkAssetBuilder.DeployBatch(batch, deployRoot);

                Assert.That(result.ok, Is.True, result.message);
                Assert.That(File.Exists(Path.Combine(deployRoot, builtPackage, "current.manifest")), Is.True);
                Assert.That(File.Exists(Path.Combine(deployRoot, builtPackage, "historical.manifest")), Is.False,
                    "即使历史目录修改时间更晚，批次部署也只能消费本轮 version。");
                Assert.That(File.Exists(Path.Combine(deployRoot, builtPackage, "stale.txt")), Is.False);
                Assert.That(Directory.Exists(Path.Combine(deployRoot, skippedPackage)), Is.False,
                    "本轮空包不能让同名旧 manifest / bundle 留在待上传目录。");
                Assert.That(File.Exists(Path.Combine(unrelatedDeployment, "keep.txt")), Is.True,
                    "未参与本轮请求的包不属于本批次清理范围。");
                Assert.That(result.message, Does.Contain("本轮版本 " + version));
                Assert.That(result.message, Does.Contain("已移除同名旧部署目录"));
            }
            finally
            {
                DeletePackageOutput(builtPackageRoot);
                DeletePackageOutput(skippedPackageRoot);
            }
        }

        [Test]
        public void DeployBatch_FailedBuildLeavesExistingDeploymentUntouched()
        {
            string packageName = "BatchFailed_" + Guid.NewGuid().ToString("N");
            string deployRoot = Path.Combine(_testRoot, "DeployFailed");
            string sentinel = Path.Combine(deployRoot, packageName, "keep.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sentinel));
            File.WriteAllText(sentinel, "keep");
            var batch = new FrameworkAssetBuilder.BuildBatchResult(
                ok: false,
                message: "build failed",
                version: "v1",
                requestedPackages: new[] { packageName },
                builtPackages: Array.Empty<string>());

            var result = FrameworkAssetBuilder.DeployBatch(batch, deployRoot);

            Assert.That(result.ok, Is.False);
            Assert.That(File.Exists(sentinel), Is.True,
                "整批构建失败时不能先清理上一份可用部署结果。");
        }

        [Test]
        public void Deploy_LatestModeKeepsOldTargetWhenNoBuildOutputExists()
        {
            string packageName = "ManualLatest_" + Guid.NewGuid().ToString("N");
            string packageOutput = Path.Combine(_targetBuildRoot, packageName);
            string deployRoot = Path.Combine(_testRoot, "DeployLatest");
            string sentinel = Path.Combine(deployRoot, packageName, "keep.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sentinel));
            File.WriteAllText(sentinel, "keep");

            try
            {
                var result = FrameworkAssetBuilder.Deploy(new[] { packageName }, deployRoot);

                Assert.That(result.ok, Is.True, result.message);
                Assert.That(result.message, Does.Contain("无构建产物，跳过"));
                Assert.That(File.Exists(sentinel), Is.True,
                    "人工 latest 部署用于重做已有包；没有源产物时保持旧目标是其兼容语义。");
            }
            finally
            {
                DeletePackageOutput(packageOutput);
            }
        }

        private void DeletePackageOutput(string packageDirectory)
        {
            if (!Directory.Exists(packageDirectory)) return;
            FrameworkProjectPath.DeleteDirectoryWithinBoundary(packageDirectory, _targetBuildRoot);
        }
    }
}
