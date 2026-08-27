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

    }
}
