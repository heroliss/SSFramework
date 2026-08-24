using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor.Tests
{
    /// <summary>
    /// 锁定 Module 审计的真实引用闭包、删除测试与窄窗口结构；不把原始 DLL 字节锁成包体基线。
    /// </summary>
    public sealed class FrameworkModuleAuditTests
    {
        [Test]
        public void Reachability_UsesActualMetadataReferences()
        {
            var assemblies = new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>
            {
                ["Core"] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = "Core",
                    ActualReferences = new[] { "Reactive", "netstandard" },
                },
                ["Reactive"] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = "Reactive",
                    ActualReferences = new[] { "ThirdParty" },
                },
                // 该程序集存在于编译图，却没有写进 Core 的真实元数据引用；不应仅因“编译可见”被算入。
                ["UnusedUi"] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = "UnusedUi",
                    ActualReferences = Array.Empty<string>(),
                },
            };

            var reachable = FrameworkModuleAudit.ComputeReachableAssemblies(
                assemblies,
                new[] { "Core" },
                name => name == "ThirdParty" ? new[] { "System.Runtime" } : Array.Empty<string>());

            Assert.That(reachable, Is.EquivalentTo(new[] { "Core", "Reactive", "ThirdParty" }));
            Assert.That(reachable, Does.Not.Contain("UnusedUi"));
        }

        [Test]
        public void UndeclaredReferences_ExposeAutoReferenceCoupling()
        {
            var info = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.UI",
                DeclaredReferences = new[] { "Game.Framework", "UniTask" },
                ActualReferences = new[]
                {
                    "Game.Framework", "UniTask", "ObservableCollections", "ObservableCollections.R3", "netstandard",
                },
            };

            var hidden = FrameworkModuleAudit.FindUndeclaredDirectReferences(
                info,
                reference => reference != "Game.Framework" && reference != "netstandard");

            Assert.That(hidden, Is.EqualTo(new[] { "ObservableCollections", "ObservableCollections.R3" }));
        }

        [Test]
        public void CurrentProject_ExternalDependenciesAreExplicit_AndDeletionTestsHold()
        {
            var snapshot = FrameworkModuleAudit.Capture();
            foreach (var module in snapshot.Assemblies.Values)
            {
                if (!module.IsFrameworkRuntime) continue;
                var hidden = FrameworkModuleAudit.FindUndeclaredExternalReferences(snapshot, module);
                Assert.That(hidden, Is.Empty, $"{module.Name} 存在 asmdef 不可见的真实外部依赖");
            }

            var core = FrameworkModuleAudit.ComputeReachableAssemblies(
                snapshot.Assemblies, new[] { FrameworkModuleAudit.CoreAssemblyName });
            var ugui = FrameworkModuleAudit.ComputeReachableAssemblies(
                snapshot.Assemblies, new[] { FrameworkModuleAudit.UGuiAssemblyName });
            var toolkit = FrameworkModuleAudit.ComputeReachableAssemblies(
                snapshot.Assemblies, new[] { FrameworkModuleAudit.ToolkitAssemblyName });

            Assert.That(core, Does.Not.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
            Assert.That(ugui, Does.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
            Assert.That(ugui, Does.Not.Contain(FrameworkModuleAudit.ToolkitAssemblyName));
            Assert.That(ugui, Does.Not.Contain(FrameworkModuleAudit.BridgeAssemblyName));
            Assert.That(toolkit, Does.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
            Assert.That(toolkit, Does.Not.Contain(FrameworkModuleAudit.UGuiAssemblyName));
            Assert.That(toolkit, Does.Not.Contain(FrameworkModuleAudit.BridgeAssemblyName));

            var result = FrameworkModuleAudit.Analyze(snapshot);
            Assert.That(result.IsHealthy, Is.True);
            Assert.That(result.CommonProfiles.Select(profile => profile.Key),
                Is.EqualTo(new[] { "core", "ugui", "toolkit" }));
            Assert.That(result.Recommendations, Has.Some.Contains("Player BuildReport"));

            string report = FrameworkModuleAudit.CreateReport(result);
            Assert.That(report, Does.Not.Contain("⚠ 无法定位程序集文件"),
                "当前轻量档位或热更清单里存在无法解析的程序集时，字节闭包不能算完整。");
            Assert.That(report, Does.Not.Contain("✗ "), "报告中的删除测试不得只靠测试代码另算后假绿。");
        }

        [Test]
        public void Window_UsesProgressiveDisclosureAndResponsiveRows()
        {
            var window = ScriptableObject.CreateInstance<FrameworkModuleAuditWindow>();
            try
            {
                window.position = new Rect(0f, 0f, 360f, 520f);
                window.CreateGUI();

                var actions = window.rootVisualElement.Q<VisualElement>("module-audit-actions");
                var content = window.rootVisualElement.Q<ScrollView>("module-audit-content");
                var summary = window.rootVisualElement.Q<VisualElement>("module-audit-summary");
                var coreProfile = window.rootVisualElement.Q<VisualElement>("module-audit-profile-core");
                var raw = window.rootVisualElement.Q<Foldout>("module-audit-raw-details");
                Assert.That(actions, Is.Not.Null);
                Assert.That(content, Is.Not.Null);
                Assert.That(content.horizontalScrollerVisibility, Is.EqualTo(ScrollerVisibility.Hidden));
                Assert.That(summary, Is.Not.Null);
                Assert.That(coreProfile, Is.Not.Null);
                Assert.That(raw, Is.Not.Null);
                Assert.That(raw.value, Is.False, "程序集明细和原始报告默认折叠，先展示结论与建议。");
                Assert.That(window.rootVisualElement.Q<TextField>(), Is.Null,
                    "主体不再使用会整片选中、产生横向长行的多行 TextField。");

                window.ApplyResponsiveLayoutForTests(360f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));
                var summaryMetrics = window.rootVisualElement
                    .Q<VisualElement>("module-audit-summary-metrics");
                Assert.That(summaryMetrics.style.flexDirection.value,
                    Is.EqualTo(FlexDirection.Column));
                Assert.That(actions[0].style.flexBasis.keyword, Is.EqualTo(StyleKeyword.Auto));
                Assert.That(summaryMetrics[0].style.flexGrow.value, Is.EqualTo(0f),
                    "窄窗纵排时使用内容高度，避免 flexBasis:0 把按钮或指标压扁。 ");

                window.ApplyResponsiveLayoutForTests(900f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Row));
                Assert.That(actions[0].style.flexGrow.value, Is.EqualTo(1f));
                Assert.That(window.minSize.x, Is.LessThanOrEqualTo(360f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }
    }
}
