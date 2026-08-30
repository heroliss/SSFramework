using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定跨生成器 output claim 的目录、后缀、文件冲突矩阵与写盘前刷新语义。</summary>
    public sealed class FrameworkGeneratedOutputClaimCatalogTests
    {
        [Test]
        public void ExactFiles_UseNormalizedCaseInsensitiveOwnership()
        {
            FrameworkGeneratedOutputClaim first = Exact(
                "first", "A", "Assets/Generated/Catalog/Output.g.cs");
            FrameworkGeneratedOutputClaim second = Exact(
                "second", "B", "Assets/Generated/Other/../catalog/output.g.cs");

            bool ok = FrameworkGeneratedOutputClaimCatalog.TryValidateAgainst(
                new[] { first, second }, Array.Empty<FrameworkGeneratedOutputClaim>(), out string message);

            Assert.That(ok, Is.False);
            Assert.That(message, Does.Contain("输出所有权冲突").And.Contain("A").And.Contain("B"));
        }

        [Test]
        public void ExclusiveDirectory_ConflictsWithAnyNestedOutput()
        {
            FrameworkGeneratedOutputClaim directory = Exclusive(
                "luban", "Luban", "Assets/Generated/Luban");
            FrameworkGeneratedOutputClaim file = Exact(
                "installer", "服务安装器", "Assets/Generated/Luban/Nested/Installer.cs");

            AssertConflict(directory, file, "独占并整理目录");
        }

        [Test]
        public void RecursiveSuffix_ConflictsWithMatchingNestedExactFile()
        {
            FrameworkGeneratedOutputClaim cleanup = Recursive(
                "proto", "Protobuf", "Assets/Generated/Proto", ".g.cs");
            FrameworkGeneratedOutputClaim file = Exact(
                "ui", "UI 绑定", "Assets/Generated/Proto/Windows/Main.nodes.g.cs");

            AssertConflict(cleanup, file, "递归清理 *.g.cs");
        }

        [Test]
        public void RecursiveSuffix_AllowsNonMatchingNestedExactFile()
        {
            FrameworkGeneratedOutputClaim cleanup = Recursive(
                "proto", "Protobuf", "Assets/Generated/Proto", ".g.cs");
            FrameworkGeneratedOutputClaim file = Exact(
                "logic", "手写逻辑", "Assets/Generated/Proto/Windows/Main.cs");

            bool ok = FrameworkGeneratedOutputClaimCatalog.TryValidateAgainst(
                new[] { cleanup, file }, Array.Empty<FrameworkGeneratedOutputClaim>(), out string message);

            Assert.That(ok, Is.True, message);
        }

        [Test]
        public void RecursiveSuffixes_RecognizeSuffixSubsetOverlap()
        {
            FrameworkGeneratedOutputClaim broad = Recursive(
                "all-cs", "所有 C#", "Assets/Generated/Shared", ".cs");
            FrameworkGeneratedOutputClaim generated = Recursive(
                "generated-cs", "生成 C#", "Assets/Generated/Shared/Nested", ".g.cs");

            AssertConflict(broad, generated, "递归清理");
        }

        [Test]
        public void DisjointClaims_AreAccepted()
        {
            FrameworkGeneratedOutputClaim directory = Exclusive(
                "luban", "Luban", "Assets/Generated/Luban");
            FrameworkGeneratedOutputClaim cleanup = Recursive(
                "proto", "Protobuf", "Assets/Generated/Proto", ".g.cs");
            FrameworkGeneratedOutputClaim file = Exact(
                "installer", "服务安装器", "Assets/Generated/Installers/GameInstaller.g.cs");

            bool ok = FrameworkGeneratedOutputClaimCatalog.TryValidateAgainst(
                new[] { directory, cleanup, file }, Array.Empty<FrameworkGeneratedOutputClaim>(), out string message);

            Assert.That(ok, Is.True, message);
        }

        [Test]
        public void RegisteredSources_CurrentProjectClaimsArePairwiseCompatible()
        {
            var claims = new List<FrameworkGeneratedOutputClaim>();
            foreach (FrameworkGeneratedOutputClaimSource source in
                     FrameworkGeneratedOutputClaimCatalog.SnapshotSources())
            {
                IReadOnlyList<FrameworkGeneratedOutputClaim> collected = source.CollectClaims();
                Assert.That(collected, Is.Not.Null, $"{source.Title} collector 不得返回 null。");
                claims.AddRange(collected);
            }

            bool ok = FrameworkGeneratedOutputClaimCatalog.TryValidateAgainst(
                claims, Array.Empty<FrameworkGeneratedOutputClaim>(), out string message);

            Assert.That(ok, Is.True, message);
        }

        [Test]
        public void Claim_RejectsMismatchedAssetAndAbsoluteIdentity()
        {
            Assert.Throws<ArgumentException>(() => FrameworkGeneratedOutputClaim.ExactFile(
                "mismatch",
                "错误 Adapter",
                "Assets/Generated/Catalog/Visible.g.cs",
                ProjectAbsolute("Assets/Generated/Catalog/Compared.g.cs")));
        }

        [Test]
        public void CurrentSource_RejectsDuplicateClaimIdentity()
        {
            bool ok = FrameworkGeneratedOutputClaimCatalog.TryValidateForPreview(
                "duplicate-id-test",
                new[]
                {
                    Exact("same", "第一项", "Assets/Generated/Catalog/First.g.cs"),
                    Exact("same", "第二项", "Assets/Generated/Catalog/Second.g.cs"),
                },
                out string message);

            Assert.That(ok, Is.False);
            Assert.That(message, Does.Contain("重复声明 id 'same'"));
        }

        [Test]
        public void Register_AllowsIdenticalReentryAndRejectsDifferentSource()
        {
            string id = "claim-test-" + Guid.NewGuid().ToString("N");
            Func<IReadOnlyList<FrameworkGeneratedOutputClaim>> collector =
                () => Array.Empty<FrameworkGeneratedOutputClaim>();
            var source = new FrameworkGeneratedOutputClaimSource(id, "测试来源", collector);
            try
            {
                FrameworkGeneratedOutputClaimCatalog.Register(source);
                Assert.DoesNotThrow(() => FrameworkGeneratedOutputClaimCatalog.Register(
                    new FrameworkGeneratedOutputClaimSource(id, "测试来源", collector)));
                Assert.Throws<InvalidOperationException>(() =>
                    FrameworkGeneratedOutputClaimCatalog.Register(
                        new FrameworkGeneratedOutputClaimSource(
                            id, "另一个来源", () => Array.Empty<FrameworkGeneratedOutputClaim>())));
            }
            finally
            {
                FrameworkGeneratedOutputClaimCatalog.Unregister(id);
            }
        }

        [Test]
        public void Preview_NeverColdStartsCollector_AndBeforeWriteRefreshesEvidence()
        {
            string token = Guid.NewGuid().ToString("N");
            string sourceId = "claim-refresh-test-" + token;
            string currentSourceId = "claim-refresh-current-" + token;
            string safePath = $"Assets/Generated/CatalogTests/{token}/Safe.g.cs";
            string sharedPath = $"Assets/Generated/CatalogTests/{token}/Shared.g.cs";
            string externalPath = safePath;
            int collectionCount = 0;
            IReadOnlyList<FrameworkGeneratedOutputClaim> Collect()
            {
                collectionCount++;
                return new[] { Exact("external", "外部来源", externalPath) };
            }

            try
            {
                FrameworkGeneratedOutputClaimCatalog.Register(
                    new FrameworkGeneratedOutputClaimSource(sourceId, "可变测试来源", Collect));
                FrameworkGeneratedOutputClaim current = Exact("current", "当前来源", sharedPath);

                Assert.That(
                    FrameworkGeneratedOutputClaimCatalog.TryValidateForPreview(
                        currentSourceId, new[] { current }, out string firstMessage),
                    Is.True,
                    firstMessage);
                Assert.That(collectionCount, Is.Zero, "冷启动预览不得执行其它 Module 的 Collector。");
                Assert.That(firstMessage,
                    Does.Contain("尚无预览快照").And.Contain("真正写盘前会强制重采"));
                externalPath = sharedPath;
                Assert.That(
                    FrameworkGeneratedOutputClaimCatalog.TryValidateForPreview(
                        currentSourceId, new[] { current }, out string pendingMessage),
                    Is.True,
                    pendingMessage);
                Assert.That(collectionCount, Is.Zero, "重复预览仍不得把证据缺口变成隐式扫描。");

                Assert.That(
                    FrameworkGeneratedOutputClaimCatalog.TryValidateBeforeWrite(
                        currentSourceId, new[] { current }, out string refreshedMessage),
                    Is.False);
                Assert.That(collectionCount, Is.EqualTo(1));
                Assert.That(refreshedMessage, Does.Contain("输出所有权冲突"));

                Assert.That(
                    FrameworkGeneratedOutputClaimCatalog.TryValidateForPreview(
                        currentSourceId, new[] { current }, out string cachedConflictMessage),
                    Is.False);
                Assert.That(collectionCount, Is.EqualTo(1), "已有写盘快照时预览只读取缓存。");
                Assert.That(cachedConflictMessage, Does.Contain("输出所有权冲突"));
            }
            finally
            {
                FrameworkGeneratedOutputClaimCatalog.Unregister(sourceId);
            }
        }

        [Test]
        public void CollectorFailure_BlocksWriteWithoutHidingEvidenceGap()
        {
            string sourceId = "claim-failure-test-" + Guid.NewGuid().ToString("N");
            try
            {
                FrameworkGeneratedOutputClaimCatalog.Register(
                    new FrameworkGeneratedOutputClaimSource(
                        sourceId,
                        "故障生成器",
                        () => throw new IOException("模拟读取失败")));

                bool ok = FrameworkGeneratedOutputClaimCatalog.TryValidateBeforeWrite(
                    "claim-failure-current",
                    new[] { Exact("current", "当前来源", "Assets/Generated/Catalog/Current.g.cs") },
                    out string message);

                Assert.That(ok, Is.False);
                Assert.That(message,
                    Does.Contain("故障生成器").And.Contain("IOException").And.Contain("模拟读取失败"));
            }
            finally
            {
                FrameworkGeneratedOutputClaimCatalog.Unregister(sourceId);
            }
        }

        [Test]
        public void FailedRefresh_DiscardsThatSourcesOlderPreviewSnapshot()
        {
            string token = Guid.NewGuid().ToString("N");
            string sourceId = "claim-stale-failure-test-" + token;
            string currentSourceId = "claim-stale-failure-current-" + token;
            bool fail = false;
            try
            {
                FrameworkGeneratedOutputClaimCatalog.Register(
                    new FrameworkGeneratedOutputClaimSource(
                        sourceId,
                        "会失效的测试来源",
                        () => fail
                            ? throw new IOException("模拟刷新失败")
                            : new[]
                            {
                                Exact("external", "外部来源",
                                    $"Assets/Generated/CatalogTests/{token}/External.g.cs"),
                            }));
                FrameworkGeneratedOutputClaim current = Exact(
                    "current", "当前来源", $"Assets/Generated/CatalogTests/{token}/Current.g.cs");

                Assert.That(
                    FrameworkGeneratedOutputClaimCatalog.TryValidateBeforeWrite(
                        currentSourceId, new[] { current }, out string initialMessage),
                    Is.True,
                    initialMessage);

                fail = true;
                Assert.That(
                    FrameworkGeneratedOutputClaimCatalog.TryValidateBeforeWrite(
                        currentSourceId, new[] { current }, out string failedMessage),
                    Is.False);
                Assert.That(failedMessage, Does.Contain("模拟刷新失败"));

                Assert.That(
                    FrameworkGeneratedOutputClaimCatalog.TryValidateForPreview(
                        currentSourceId, new[] { current }, out string previewMessage),
                    Is.True,
                    previewMessage);
                Assert.That(previewMessage,
                    Does.Contain("会失效的测试来源").And.Contain("尚无预览快照"),
                    "刷新失败后不能继续把该来源的旧快照显示为当前证据。");
            }
            finally
            {
                FrameworkGeneratedOutputClaimCatalog.Unregister(sourceId);
            }
        }

        private static void AssertConflict(
            FrameworkGeneratedOutputClaim first,
            FrameworkGeneratedOutputClaim second,
            string expectedMessage)
        {
            bool ok = FrameworkGeneratedOutputClaimCatalog.TryValidateAgainst(
                new[] { first, second }, Array.Empty<FrameworkGeneratedOutputClaim>(), out string message);
            Assert.That(ok, Is.False);
            Assert.That(message, Does.Contain(expectedMessage));
        }

        private static FrameworkGeneratedOutputClaim Exact(string id, string owner, string assetPath) =>
            FrameworkGeneratedOutputClaim.ExactFile(id, owner, assetPath, ProjectAbsolute(assetPath));

        private static FrameworkGeneratedOutputClaim Exclusive(string id, string owner, string assetPath) =>
            FrameworkGeneratedOutputClaim.ExclusiveDirectory(id, owner, assetPath, ProjectAbsolute(assetPath));

        private static FrameworkGeneratedOutputClaim Recursive(
            string id, string owner, string assetPath, string suffix) =>
            FrameworkGeneratedOutputClaim.RecursiveFileSuffix(
                id, owner, assetPath, ProjectAbsolute(assetPath), suffix);

        private static string ProjectAbsolute(string assetPath) =>
            Path.GetFullPath(Path.Combine(
                Directory.GetParent(Application.dataPath)!.FullName,
                assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
