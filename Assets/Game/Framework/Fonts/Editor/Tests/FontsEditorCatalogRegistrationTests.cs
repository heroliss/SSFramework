using System;
using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Fonts.Editor.Tests
{
    /// <summary>锁定字体 Editor Module 在 <c>autoReferenced:false</c> 下仍登记自己的配置导航。</summary>
    public sealed class FontsEditorCatalogRegistrationTests
    {
        [Test]
        public void FontsModule_RegistersOwnedConfiguration() =>
            Assert.That(FrameworkConfigRegistry.Snapshot().Select(item => item.Id), Does.Contain("font-charset"));

        [Test]
        public void FontsModule_RegistersOwnedOutputClaims() =>
            Assert.That(
                FrameworkGeneratedOutputClaimCatalog.SnapshotSources().Select(item => item.Id),
                Does.Contain(FontCharsetGenerator.OutputClaimSourceId));

        [Test]
        public void FontCharsetWorkbench_ConsumesSharedOperationGate()
        {
            string source = ReadScriptSource(nameof(FontCharsetWindow));

            Assert.That(source, Does.Contain("FrameworkEditorOperationGate.CanStart"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isPlayingOrWillChangePlaymode"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isCompiling"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isUpdating"));
            Assert.That(source, Does.Not.Contain("BuildPipeline.isBuildingPlayer"));
        }

        [Test]
        public void FontCharsetProfile_UsesSharedStableDiscoveryWithoutPrivateInvalidation()
        {
            string folderName = "__FontCharsetProfileCatalogTests_" + Guid.NewGuid().ToString("N");
            string folderPath = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                CreateProfile(folderPath + "/Second.asset");
                CreateProfile(folderPath + "/First.asset");
                FrameworkEditorProfileCatalog.Refresh(new[] { typeof(FontCharsetProfile) });
                var paths = FrameworkEditorProfileCatalog.GetPaths(typeof(FontCharsetProfile));
                int warningCount = 0;
                Application.LogCallback capture = (condition, _, type) =>
                {
                    if (type == LogType.Warning && condition.StartsWith(
                            "[FontCharset] 找到多个常用字集 profile", StringComparison.Ordinal))
                        warningCount++;
                };
                Application.logMessageReceived += capture;
                try
                {
                    Assert.That(FontCharsetProfile.TryResolve(out var first), Is.True);
                    Assert.That(AssetDatabase.GetAssetPath(first), Is.EqualTo(paths[0]));
                    int revision = FrameworkEditorProfileCatalog.Revision;
                    Assert.That(FontCharsetProfile.TryResolve(out var second), Is.True);
                    Assert.That(second, Is.SameAs(first));
                    Assert.That(FrameworkEditorProfileCatalog.Revision, Is.EqualTo(revision));
                }
                finally
                {
                    Application.logMessageReceived -= capture;
                }

                Assert.That(warningCount, Is.EqualTo(1),
                    "同一 Catalog revision 内重复绘制不能重复刷出单例冲突 Warning。");

                string source = ReadScriptSource(nameof(FontCharsetProfile));
                Assert.That(source, Does.Contain("FrameworkEditorProfileCatalog.TryResolveFirst"));
                Assert.That(source, Does.Contain("FrameworkEditorProfileCatalog.Refresh"));
                Assert.That(source, Does.Contain("FrameworkProjectSettingsLocation.EnsureDirectory"));
                Assert.That(source, Does.Contain("GetExistingProfileOrThrow"));
                AssertSafeCreationOrdering(source);
                Assert.That(source, Does.Not.Contain("EditorApplication.projectChanged"));
                Assert.That(source, Does.Not.Contain(
                    "AssetDatabase.FindAssets(\"t:\" + nameof(FontCharsetProfile))"));
                Assert.That(source, Does.Not.Contain("EnsureChildFolder("),
                    "项目设置目录创建应由共享 Implementation 拥有，字体 Module 不再复制一份。");
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
                FrameworkEditorProfileCatalog.Invalidate();
            }
        }

        private static void CreateProfile(string path) =>
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<FontCharsetProfile>(), path);

        private static void AssertSafeCreationOrdering(string source)
        {
            int create = source.IndexOf("AssetDatabase.CreateAsset(profile, path);", StringComparison.Ordinal);
            int firstRefresh = source.IndexOf("FrameworkEditorProfileCatalog.Refresh", StringComparison.Ordinal);
            int lastRefresh = source.LastIndexOf("FrameworkEditorProfileCatalog.Refresh", StringComparison.Ordinal);
            int ensureDirectory = source.IndexOf("FrameworkProjectSettingsLocation.EnsureDirectory", StringComparison.Ordinal);
            int collisionCheck = source.IndexOf("GetExistingProfileOrThrow", StringComparison.Ordinal);
            int effectiveCheck = source.IndexOf("effective != profile", StringComparison.Ordinal);
            Assert.That(create, Is.GreaterThan(firstRefresh), "创建前必须强制刷新，修复尚未送达的 projectChanged。 ");
            Assert.That(ensureDirectory, Is.GreaterThan(firstRefresh), "确认确实缺少 Profile 前不得创建默认目录。 ");
            Assert.That(collisionCheck, Is.GreaterThan(ensureDirectory));
            Assert.That(create, Is.GreaterThan(collisionCheck), "CreateAsset 前必须拒绝固定路径碰撞。 ");
            Assert.That(lastRefresh, Is.GreaterThan(create), "创建后必须刷新并验证实际生效项。 ");
            Assert.That(effectiveCheck, Is.GreaterThan(create), "创建后必须确认新资产就是 stable-first 生效项。 ");
        }

        private static string ReadScriptSource(string typeName)
        {
            string[] paths = AssetDatabase.FindAssets(typeName + " t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith("/" + typeName + ".cs", StringComparison.Ordinal))
                .ToArray();
            Assert.That(paths, Has.Length.EqualTo(1), "应精确找到字体 owner Module 内的源码。");
            return AssetDatabase.LoadAssetAtPath<MonoScript>(paths[0]).text;
        }
    }
}
