using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

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

        private static IEnumerable<(MethodInfo method, MenuItem attribute)> FindExecuteItems()
        {
            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<MenuItem>())
            foreach (MenuItem attribute in method.GetCustomAttributes(typeof(MenuItem), inherit: false))
                if (!attribute.validate)
                    yield return (method, attribute);
        }
    }
}
