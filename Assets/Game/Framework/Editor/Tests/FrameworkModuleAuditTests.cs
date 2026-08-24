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
        public void LinkXml_SeparatesUnconditionalAndConditionalRoots()
        {
            var rules = FrameworkModuleAudit.ParseLinkerPreservations(@"
<linker>
  <assembly fullname=""Game.Optional"" preserve=""all"" />
  <assembly fullname=""ThirdParty, Version=1.0.0.0"" ignoreIfUnreferenced=""1"">
    <type fullname=""ThirdParty.Entry"" preserve=""all"" />
  </assembly>
</linker>", "Assets/Optional/link.xml", "Game.Optional");

            Assert.That(rules, Has.Length.EqualTo(2));
            Assert.That(rules[0].AssemblyName, Is.EqualTo("Game.Optional"));
            Assert.That(rules[0].IsUnconditional, Is.True);
            Assert.That(rules[1].AssemblyName, Is.EqualTo("ThirdParty"));
            Assert.That(rules[1].IgnoreIfUnreferenced, Is.True);
            Assert.That(rules[1].Scope, Does.Contain("1 条"));
        }

        [Test]
        public void LinkXml_RequiredZeroTypeRulesDoNotCreateRoots()
        {
            var rules = FrameworkModuleAudit.ParseLinkerPreservations(@"
<linker>
  <assembly fullname=""Game.Optional"">
    <type fullname=""Game.Optional.Entry"" preserve=""all"" required=""0"" />
  </assembly>
</linker>", "Assets/Optional/link.xml", "Game.Optional");

            Assert.That(rules, Has.Length.EqualTo(1));
            Assert.That(rules[0].RequiredOnlyIfReferenced, Is.True);
            Assert.That(rules[0].IsUnconditional, Is.False);
        }

        [Test]
        public void ModuleStatus_ExplainsConsumersHotUpdateAndLinkerRoots()
        {
            var optional = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.Optional",
                DeclaredReferences = new[] { FrameworkModuleAudit.CoreAssemblyName, "ThirdParty" },
                ActualReferences = new[] { FrameworkModuleAudit.CoreAssemblyName, "ThirdParty" },
            };
            var consumer = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Main",
                AsmdefPath = "Assets/Game/Main/Game.Main.asmdef",
                ActualReferences = new[] { optional.Name },
            };
            var assemblies = new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>
            {
                [optional.Name] = optional,
                [consumer.Name] = consumer,
                [FrameworkModuleAudit.CoreAssemblyName] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = FrameworkModuleAudit.CoreAssemblyName,
                    ActualReferences = Array.Empty<string>(),
                },
            };
            var rule = new FrameworkModuleAudit.LinkerPreservation
            {
                OwnerModuleName = optional.Name,
                Path = "Assets/Game/Framework/Optional/link.xml",
                AssemblyName = "ThirdParty",
                Scope = "preserve=all",
            };
            var snapshot = new FrameworkModuleAudit.Snapshot(
                assemblies,
                new Dictionary<string, string>(),
                new[] { FrameworkModuleAudit.CoreAssemblyName, optional.Name },
                "test",
                new[] { rule },
                new Dictionary<string, string[]>
                {
                    [optional.Name] = new[] { "Game.Framework.Demo", "Game.Main" },
                });

            var status = FrameworkModuleAudit.BuildModuleStatuses(snapshot, new[] { optional }).Single();

            Assert.That(status.DirectConsumers, Is.EqualTo(new[] { "Game.Main" }));
            Assert.That(status.FrameworkConsumers, Is.Empty);
            Assert.That(status.ProjectConsumers, Is.EqualTo(new[] { "Game.Main" }));
            Assert.That(status.RemovalBlockers, Is.EqualTo(new[] { "Game.Framework.Demo", "Game.Main" }));
            Assert.That(status.HotUpdateDependencies,
                Is.EqualTo(new[] { FrameworkModuleAudit.CoreAssemblyName }));
            Assert.That(status.IsHotUpdateRoot, Is.True);
            Assert.That(status.HasUnconditionalPreservation, Is.True);
            Assert.That(status.RetentionReasons, Has.Some.Contains("CodePackage"));
            Assert.That(status.RetentionReasons, Has.Some.Contains("Game.Main"));
            Assert.That(status.RetentionReasons, Has.Some.Contains("ThirdParty"));
            Assert.That(status.RetentionReasons, Has.Some.Contains("AOT → 热更"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("FrameworkHotUpdateProfile"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("不要先单独同步取消热更"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("Game.Framework.Demo"));
        }

        [Test]
        public void ModuleStatus_FailsHealthWhenAotModuleReferencesHotAssembly()
        {
            var optional = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.Optional",
                DeclaredReferences = new[] { FrameworkModuleAudit.CoreAssemblyName },
                ActualReferences = Array.Empty<string>(),
            };
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo> { [optional.Name] = optional },
                new Dictionary<string, string>(),
                new[] { FrameworkModuleAudit.CoreAssemblyName },
                "test");

            var status = FrameworkModuleAudit.BuildModuleStatuses(snapshot, new[] { optional }).Single();
            var result = new FrameworkModuleAudit.AuditResult
            {
                ModuleStatuses = new[] { status },
                AllRuntimeModulesOptIn = true,
            };

            Assert.That(status.HasHotUpdateViolation, Is.True);
            Assert.That(status.RetentionReasons, Has.Some.Contains("非法的 AOT → 热更"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("非法中间状态"));
            Assert.That(result.IsHealthy, Is.False);
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
            Assert.That(result.ModuleProfiles.SelectMany(profile => profile.Roots),
                Does.Contain(FrameworkModuleAudit.BridgeAssemblyName));
            Assert.That(result.ModuleStatuses.Select(status => status.Module.Name),
                Does.Contain("Game.Framework.Asset.Yoo"));
            var bridgeStatus = result.ModuleStatuses.Single(status =>
                status.Module.Name == FrameworkModuleAudit.BridgeAssemblyName);
            Assert.That(bridgeStatus.RemovalBlockers, Has.Some.Contains("Game.Framework.Demo"),
                "物理删除计划必须覆盖不会进入 Player 的 Demo / Editor / Tests asmdef 引用。 ");
            Assert.That(result.HasRetentionWarnings, Is.True,
                "当前可选 Module 的无条件 link.xml 必须显式显示，不能让“边界健康”掩盖最终保留原因。");
            Assert.That(result.GlobalPreservations, Is.Not.Empty,
                "HybridCLR 生成物与第三方 link.xml 也要可追踪，但不能误归罪于 Framework Module。");
            Assert.That(result.GlobalPreservations, Has.Some.Matches<FrameworkModuleAudit.LinkerPreservation>(
                rule => rule.IsGenerated));
            Assert.That(result.Recommendations, Has.Some.Contains("Player BuildReport"));
            Assert.That(result.Recommendations, Has.Some.Contains("link.xml"));

            string report = FrameworkModuleAudit.CreateReport(result);
            Assert.That(report, Does.Not.Contain("⚠ 无法定位程序集文件"),
                "当前轻量档位或热更清单里存在无法解析的程序集时，字节闭包不能算完整。");
            Assert.That(report, Does.Not.Contain("✗ "), "报告中的删除测试不得只靠测试代码另算后假绿。");
            Assert.That(report, Does.Contain("Module 当前保留原因"));
            Assert.That(report, Does.Contain("全局与生成的 link.xml 证据"));
            Assert.That(report, Does.Contain("CodePackage"));
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
                var statuses = window.rootVisualElement.Q<Foldout>("module-audit-module-statuses");
                var attentionStatuses = window.rootVisualElement.Q<Foldout>(
                    "module-audit-attention-statuses");
                var moduleProfiles = window.rootVisualElement.Q<Foldout>("module-audit-module-profiles");
                var globalPreservations = window.rootVisualElement.Q<Foldout>(
                    "module-audit-global-preservations");
                Assert.That(actions, Is.Not.Null);
                Assert.That(content, Is.Not.Null);
                Assert.That(content.horizontalScrollerVisibility, Is.EqualTo(ScrollerVisibility.Hidden));
                Assert.That(summary, Is.Not.Null);
                Assert.That(coreProfile, Is.Not.Null);
                Assert.That(raw, Is.Not.Null);
                Assert.That(statuses, Is.Not.Null);
                Assert.That(statuses.value, Is.False, "全部 Module 默认折叠，避免把常用组合推到数屏以后。 ");
                Assert.That(attentionStatuses, Is.Not.Null);
                Assert.That(attentionStatuses.value, Is.True,
                    "无条件根或热更违规应优先显示，健康 Module 再按需展开。 ");
                Assert.That(moduleProfiles, Is.Not.Null);
                Assert.That(moduleProfiles.value, Is.False, "任意 Module 的完整闭包默认折叠，避免淹没主结论。 ");
                Assert.That(globalPreservations, Is.Not.Null);
                Assert.That(globalPreservations.value, Is.False,
                    "全局和生成规则用于追踪，不应抢占新手的首屏结论。 ");
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
