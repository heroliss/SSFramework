using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    public sealed class SceneShortcutProfileTests
    {
        [Test]
        public void SeedFromBuildSettings_UsesEnabledValidScenesWithoutProjectSpecificDefaults()
        {
            string scenePath = AssetDatabase.FindAssets("t:Scene")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(path => AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null);
            if (string.IsNullOrEmpty(scenePath))
                Assert.Ignore("当前极简消费工程没有任何 SceneAsset；播种算法的纯消歧契约仍由另一用例覆盖。");
            var expected = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            var profile = ScriptableObject.CreateInstance<SceneShortcutProfile>();
            try
            {
                profile.PlayFromBootScene = true;
                profile.SeedFromBuildSettings(new[]
                {
                    new EditorBuildSettingsScene(scenePath, true),
                    new EditorBuildSettingsScene(scenePath, true),
                    new EditorBuildSettingsScene(scenePath, false),
                    new EditorBuildSettingsScene("Assets/DoesNotExist.unity", true),
                });

                Assert.That(profile.Entries.Count, Is.EqualTo(1),
                    "初始快捷入口应只导入 Build Settings 中已启用、有效且不重复的场景。");
                Assert.That(profile.Entries[0].Scene, Is.SameAs(expected));
                Assert.That(profile.Entries[0].Group, Is.Null.Or.Empty,
                    "通用框架不能猜测业务分组。");
                Assert.That(profile.BootScene, Is.SameAs(expected),
                    "首个有效场景可作为默认启动候选，但 Play 开关仍保持关闭。");
                Assert.That(profile.PlayFromBootScene, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ResolveUniqueMenuPath_DisambiguatesWithinGroupWithoutChangingOtherGroups()
        {
            var used = SceneShortcutMenu.CreateReservedMenuPaths();

            Assert.That(SceneShortcutMenu.ResolveUniqueMenuPath("", "Shared", "Assets/A/Shared.unity", used).label,
                Is.EqualTo("Shared"));
            Assert.That(SceneShortcutMenu.ResolveUniqueMenuPath("", "Shared", "Assets/B/Shared.unity", used).label,
                Is.EqualTo("Shared (B)"));
            Assert.That(SceneShortcutMenu.ResolveUniqueMenuPath("", "Shared", "Assets/B/Shared.unity", used).label,
                Is.EqualTo("Shared (B) #2"));
            Assert.That(SceneShortcutMenu.ResolveUniqueMenuPath("Other", "Shared", "Assets/B/Shared.unity", used).label,
                Is.EqualTo("Shared"), "不同分组的完整菜单路径不冲突，不需要改显示名。");

            Assert.That(SceneShortcutMenu.ResolveUniqueMenuPath(
                    SceneShortcutMenu.PlaySub, "Shared", "Assets/C/Shared.unity", used).label,
                Is.EqualTo("Shared (C)"), "普通打开路径不能与已有条目的 Play 子菜单路径重合。");
            Assert.That(SceneShortcutMenu.ResolveUniqueMenuPath(
                    "", "▶ 打开并 Play", "Assets/Scenes/Boot.unity", used).label,
                Is.EqualTo("▶ 打开并 Play (Scenes)"),
                "普通打开项不能占用“打开并 Play”结构子菜单的路径。");
        }

        [Test]
        public void ResolveUniqueMenuPath_NeverTurnsClickableLeafIntoSubmenuParent()
        {
            var used = SceneShortcutMenu.CreateReservedMenuPaths();

            var first = SceneShortcutMenu.ResolveUniqueMenuPath(
                string.Empty,
                "Navigation",
                "Assets/Scenes/Navigation.unity",
                used);
            var fixedLeafGroup = SceneShortcutMenu.ResolveUniqueMenuPath(
                first.label,
                "Child",
                "Assets/Scenes/Child.unity",
                used);
            Assert.That(fixedLeafGroup.group, Is.EqualTo("Navigation (场景组)/"));

            var slashInDisplayName = SceneShortcutMenu.ResolveUniqueMenuPath(
                "",
                "Refresh/Child",
                "Assets/Scenes/Named.unity",
                used);
            Assert.That(slashInDisplayName.label, Does.Contain("／"));
            Assert.That(slashInDisplayName.label, Does.Not.Contain("/"));
        }

        [Test]
        public void SceneShortcutProfile_UsesSharedStableDiscoveryWithoutPrivateInvalidation()
        {
            string folderName = "__SceneShortcutProfileCatalogTests_" + Guid.NewGuid().ToString("N");
            string folderPath = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                CreateProfile(folderPath + "/Second.asset");
                CreateProfile(folderPath + "/First.asset");
                FrameworkEditorProfileCatalog.Refresh(new[] { typeof(SceneShortcutProfile) });
                var paths = FrameworkEditorProfileCatalog.GetPaths(typeof(SceneShortcutProfile));
                int warningCount = 0;
                Application.LogCallback capture = (condition, _, type) =>
                {
                    if (type == LogType.Warning && condition.StartsWith(
                            "[场景快捷入口] 找到多个配置", StringComparison.Ordinal))
                        warningCount++;
                };
                Application.logMessageReceived += capture;
                SceneShortcutProfile first;
                try
                {
                    first = SceneShortcutProfile.Find();
                    Assert.That(first, Is.Not.Null);
                    Assert.That(AssetDatabase.GetAssetPath(first), Is.EqualTo(paths[0]));
                    int revision = FrameworkEditorProfileCatalog.Revision;
                    Assert.That(SceneShortcutProfile.Find(), Is.SameAs(first));
                    Assert.That(FrameworkEditorProfileCatalog.Revision, Is.EqualTo(revision));
                }
                finally
                {
                    Application.logMessageReceived -= capture;
                }

                Assert.That(warningCount, Is.EqualTo(1),
                    "同一 Catalog revision 内重复绘制不能重复刷出单例冲突 Warning。");

                string source = ReadScriptSource(nameof(SceneShortcutProfile));
                Assert.That(source, Does.Contain("FrameworkEditorProfileCatalog.TryResolveFirst"));
                Assert.That(source, Does.Contain("FrameworkEditorProfileCatalog.Refresh"));
                Assert.That(source, Does.Contain("GetExistingProfileOrThrow"));
                AssertSafeCreationOrdering(source);
                Assert.That(source, Does.Not.Contain("EditorApplication.projectChanged"));
                Assert.That(source, Does.Not.Contain(
                    "AssetDatabase.FindAssets(\"t:\" + nameof(SceneShortcutProfile))"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
                FrameworkEditorProfileCatalog.Invalidate();
            }
        }

        private static void CreateProfile(string path) =>
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<SceneShortcutProfile>(), path);

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
            Assert.That(paths, Has.Length.EqualTo(1), "应精确找到 Core Editor owner Module 内的源码。");
            return AssetDatabase.LoadAssetAtPath<MonoScript>(paths[0]).text;
        }

    }
}
