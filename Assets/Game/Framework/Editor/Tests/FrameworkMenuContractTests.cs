using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定“人工顶层菜单只导航，机器自动化入口显式例外”的编辑器交互契约。</summary>
    public sealed class FrameworkMenuContractTests
    {
        private static readonly HashSet<string> AutomationPaths = new(StringComparer.Ordinal)
        {
            FrameworkMenuPaths.PlayModeTestPreflight,
            FrameworkMenuPaths.CoreBuildSizeProbe,
            FrameworkMenuPaths.CommonBuildSizeProbe,
        };

        [Test]
        public void TopLevelMenu_OnlyOpensWindowsExceptStableAutomationInterfaces()
        {
            var executeItems = FindExecuteItems()
                .Where(item => item.attribute.menuItem.StartsWith(FrameworkMenuPaths.Root, StringComparison.Ordinal))
                .ToArray();

            foreach (var item in executeItems)
            {
                string path = item.attribute.menuItem;
                if (AutomationPaths.Contains(path)) continue;

                Assert.That(item.method.DeclaringType, Is.Not.Null, path);
                Assert.That(typeof(EditorWindow).IsAssignableFrom(item.method.DeclaringType), Is.True,
                    path + " 会直接执行操作。人工 SSFramework 顶层菜单只能打开说明充分的 EditorWindow；" +
                    "上下文操作放 Assets/GameObject 菜单，机器 Interface 需加入已审查的自动化白名单。");
                Assert.That(item.method.GetParameters(), Is.Empty, path + " 的窗口入口必须是无参方法。");
            }
        }

        [Test]
        public void TopLevelMenu_ExecutePathsAreUniqueAndAutomationInterfacesRemainAvailable()
        {
            var paths = FindExecuteItems()
                .Select(item => item.attribute.menuItem)
                .Where(path => path.StartsWith(FrameworkMenuPaths.Root, StringComparison.Ordinal))
                .ToArray();

            Assert.That(paths.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(paths.Length),
                "两个执行方法注册了相同 SSFramework 菜单路径；Unity 最终调用对象会变得不透明。");
            Assert.That(paths, Does.Contain(FrameworkMenuPaths.PlayModeTestPreflight));
            Assert.That(paths, Does.Contain(FrameworkMenuPaths.CoreBuildSizeProbe));
            Assert.That(paths, Does.Contain(FrameworkMenuPaths.CommonBuildSizeProbe));
        }

        [Test]
        public void AutomationMenuPaths_RemainExactExternalContracts()
        {
            Assert.That(FrameworkMenuPaths.AutomationGuide,
                Is.EqualTo("SSFramework/诊断/AI 自动化/使用说明（人工入口）"));
            Assert.That(FrameworkMenuPaths.PlayModeTestPreflight,
                Is.EqualTo("SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）"));
            Assert.That(FrameworkMenuPaths.CoreBuildSizeProbe,
                Is.EqualTo("SSFramework/诊断/AI 自动化/Core 隔离构建（Player Build）"));
            Assert.That(FrameworkMenuPaths.CommonBuildSizeProbe,
                Is.EqualTo("SSFramework/诊断/AI 自动化/常用档位隔离构建（Core + UGUI + Toolkit）"));
        }

        [Test]
        public void ToolRegistry_AllowsExactReentryButRejectsIdCollisions()
        {
            string id = "menu-contract-test-" + Guid.NewGuid().ToString("N");
            var first = new FrameworkToolDescriptor(
                id, FrameworkToolCategory.Development, 999,
                "契约测试", "仅测试 exact reentry。", FrameworkMenuPaths.Tools);
            try
            {
                FrameworkToolRegistry.Register(first);
                Assert.DoesNotThrow(() => FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                    id, FrameworkToolCategory.Development, 999,
                    "契约测试", "仅测试 exact reentry。", FrameworkMenuPaths.Tools)));
                Assert.Throws<InvalidOperationException>(() => FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                    id, FrameworkToolCategory.Development, 999,
                    "冲突工具", "不同元数据不得静默覆盖。", FrameworkMenuPaths.Configuration)));
            }
            finally
            {
                FrameworkToolRegistry.Unregister(id);
            }
        }

        [Test]
        public void ToolRegistry_HasStableUniqueMetadata()
        {
            var tools = FrameworkToolRegistry.Snapshot();
            Assert.That(tools, Is.Not.Empty);
            Assert.That(tools.Select(tool => tool.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(tools.Count));
            Assert.That(tools, Has.All.Matches<FrameworkToolDescriptor>(tool =>
                !string.IsNullOrWhiteSpace(tool.Title) &&
                !string.IsNullOrWhiteSpace(tool.Summary) &&
                tool.MenuPath.StartsWith(FrameworkMenuPaths.Root, StringComparison.Ordinal)));
        }

        [Test]
        public void ConfigRegistry_AllowsExactReentryButRejectsInvalidOrConflictingEntries()
        {
            string id = "config-contract-test-" + Guid.NewGuid().ToString("N");
            var first = new FrameworkConfigDescriptor(
                id, 999, "契约测试配置", typeof(ServiceInstallerProfile), singleton: false,
                "仅测试 exact reentry。", FrameworkMenuPaths.ServiceInstaller);
            try
            {
                FrameworkConfigRegistry.Register(first);
                Assert.DoesNotThrow(() => FrameworkConfigRegistry.Register(new FrameworkConfigDescriptor(
                    id, 999, "契约测试配置", typeof(ServiceInstallerProfile), singleton: false,
                    "仅测试 exact reentry。", FrameworkMenuPaths.ServiceInstaller)));
                Assert.Throws<InvalidOperationException>(() => FrameworkConfigRegistry.Register(new FrameworkConfigDescriptor(
                    id, 999, "冲突配置", typeof(ServiceInstallerProfile), singleton: true,
                    "不同元数据不得静默覆盖。", FrameworkMenuPaths.ServiceInstaller)));
                Assert.Throws<ArgumentException>(() => new FrameworkConfigDescriptor(
                    id + "-invalid", 999, "非法配置", typeof(string), singleton: false,
                    "普通托管类型不能冒充资产配置。", FrameworkMenuPaths.ServiceInstaller));
            }
            finally
            {
                FrameworkConfigRegistry.Unregister(id);
            }
        }

        [Test]
        public void ConfigRegistry_HasStableUniqueModuleOwnedMetadata()
        {
            var configurations = FrameworkConfigRegistry.Snapshot();
            Assert.That(configurations, Is.Not.Empty);
            Assert.That(configurations.Select(configuration => configuration.Id)
                .Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(configurations.Count));
            Assert.That(configurations, Is.Ordered.By(nameof(FrameworkConfigDescriptor.Order)));
            Assert.That(configurations, Has.All.Matches<FrameworkConfigDescriptor>(configuration =>
                !string.IsNullOrWhiteSpace(configuration.Title) &&
                typeof(ScriptableObject).IsAssignableFrom(configuration.ProfileType) &&
                !string.IsNullOrWhiteSpace(configuration.Note) &&
                configuration.MenuPath.StartsWith(FrameworkMenuPaths.Root, StringComparison.Ordinal) &&
                (configuration.SecondaryProfileType == null ||
                 typeof(ScriptableObject).IsAssignableFrom(configuration.SecondaryProfileType))));
            Assert.That(configurations.Select(configuration => configuration.Id), Does.Contain("service-installer"));
            Assert.That(configurations.Select(configuration => configuration.Id), Does.Contain("scene-shortcuts"));
        }

        [Test]
        public void ProfileCatalog_ReusesDiscoveryWithinRevisionAndRefreshesExplicitly()
        {
            Type profileType = typeof(ServiceInstallerProfile);
            FrameworkEditorProfileCatalog.Invalidate();
            IReadOnlyList<string> first = FrameworkEditorProfileCatalog.GetPaths(profileType);
            IReadOnlyList<string> second = FrameworkEditorProfileCatalog.GetPaths(profileType);

            Assert.That(second, Is.SameAs(first),
                "同一工程 revision 内应复用 Profile 路径快照，不能在每次 OnGUI / ResolveAll 时重扫。 ");
            Assert.That(first, Is.EqualTo(first.OrderBy(path => path, StringComparer.Ordinal).ToArray()));

            int beforeRefresh = FrameworkEditorProfileCatalog.Revision;
            FrameworkEditorProfileCatalog.Refresh(new[] { profileType });
            IReadOnlyList<string> refreshed = FrameworkEditorProfileCatalog.GetPaths(profileType);
            Assert.That(FrameworkEditorProfileCatalog.Revision, Is.GreaterThan(beforeRefresh));
            Assert.That(refreshed, Is.Not.SameAs(first));
            Assert.That(refreshed, Is.EqualTo(first));
        }

        [Test]
        public void ConfigOverview_UsesCachedCatalogAndResponsiveVisualHierarchy()
        {
            FrameworkEditorProfileCatalog.Invalidate();
            var window = ScriptableObject.CreateInstance<FrameworkConfigOverviewWindow>();
            try
            {
                window.position = new Rect(0f, 0f, 360f, 620f);
                window.CreateGUI();

                VisualElement root = window.rootVisualElement;
                var actions = root.Q<VisualElement>("config-overview-actions");
                var content = root.Q<ScrollView>("config-overview-content");
                var rescan = root.Q<Button>("config-overview-rescan");
                Assert.That(root.Q<VisualElement>("config-overview-hero"), Is.Not.Null);
                Assert.That(root.Q<VisualElement>("config-overview-loading"), Is.Not.Null,
                    "首次绘制应先显示轻量窗口壳，不能在 CreateGUI 中同步扫描全工程。 ");
                Assert.That(actions, Is.Not.Null);
                Assert.That(content, Is.Not.Null);
                Assert.That(content.horizontalScrollerVisibility, Is.EqualTo(ScrollerVisibility.Hidden));
                Assert.That(rescan?.text, Is.EqualTo("重新扫描"));

                window.RefreshForTests();

                var metrics = root.Q<VisualElement>("config-overview-metrics");
                Assert.That(root.Q<VisualElement>("config-overview-hero"), Is.Not.Null,
                    "刷新内容只能重建 ScrollView，不能移除固定用途说明。 ");
                Assert.That(metrics, Is.Not.Null);
                Assert.That(root.Q<VisualElement>("config-overview-section-service-installer"), Is.Not.Null,
                    "配置卡片应从 Module-local Registry 生成，而非中央窗口维护类型表。 ");

                window.ApplyResponsiveLayoutForTests(360f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));
                Assert.That(metrics.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));
                Assert.That(rescan.style.flexBasis.keyword, Is.EqualTo(StyleKeyword.Auto));

                window.ApplyResponsiveLayoutForTests(800f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Row));
                Assert.That(metrics.style.flexDirection.value, Is.EqualTo(FlexDirection.Row));
                Assert.That(window.minSize.x, Is.LessThanOrEqualTo(360f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void EditorRegistries_OnlyNavigateToExistingWindowMenus()
        {
            var windowPaths = FindExecuteItems()
                .Where(item => item.method.DeclaringType != null &&
                               typeof(EditorWindow).IsAssignableFrom(item.method.DeclaringType))
                .Select(item => item.attribute.menuItem)
                .ToHashSet(StringComparer.Ordinal);

            Assert.That(FrameworkToolRegistry.Snapshot().Select(item => item.MenuPath),
                Has.All.Matches<string>(windowPaths.Contains));
            Assert.That(FrameworkConfigRegistry.Snapshot().Select(item => item.MenuPath),
                Has.All.Matches<string>(windowPaths.Contains));
        }

        private static IEnumerable<(MethodInfo method, MenuItem attribute)> FindExecuteItems()
        {
            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<MenuItem>())
            foreach (MenuItem attribute in method.GetCustomAttributes(typeof(MenuItem), inherit: false))
                if (!attribute.validate)
                    yield return (method, attribute);
        }
    }
}
