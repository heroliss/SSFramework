using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
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
                Name = "Project.Runtime",
                AsmdefPath = "Assets/Project/Project.Runtime.asmdef",
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
                    [optional.Name] = new[] { "Sample.Editor.Consumer", "Project.Runtime" },
                });

            var status = FrameworkModuleAudit.BuildModuleStatuses(snapshot, new[] { optional }).Single();

            Assert.That(status.DirectConsumers, Is.EqualTo(new[] { "Project.Runtime" }));
            Assert.That(status.FrameworkConsumers, Is.Empty);
            Assert.That(status.ProjectConsumers, Is.EqualTo(new[] { "Project.Runtime" }));
            Assert.That(status.RemovalBlockers, Is.EquivalentTo(new[] { "Project.Runtime", "Sample.Editor.Consumer" }));
            Assert.That(status.HotUpdateDependencies,
                Is.EqualTo(new[] { FrameworkModuleAudit.CoreAssemblyName }));
            Assert.That(status.IsHotUpdateRoot, Is.True);
            Assert.That(status.HasUnconditionalPreservation, Is.True);
            Assert.That(status.RetentionReasons, Has.Some.Contains("CodePackage"));
            Assert.That(status.RetentionReasons, Has.Some.Contains("Project.Runtime"));
            Assert.That(status.RetentionReasons, Has.Some.Contains("ThirdParty"));
            Assert.That(status.RetentionReasons, Has.Some.Contains("AOT → 热更"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("FrameworkHotUpdateProfile"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("不要先单独同步取消热更"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("Sample.Editor.Consumer"));
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
        public void LinkerOwner_UsesUniqueDeepestModuleAndNeverGuessesAmbiguousDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "SSFrameworkLinkOwner");
            string shared = Path.Combine(root, "Shared");
            var sharedA = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.SharedA",
                SourceDirectory = shared,
            };
            var sharedB = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.SharedB",
                SourceDirectory = shared,
            };
            Assert.That(FrameworkModuleAudit.ResolveLinkerOwner(
                Path.Combine(shared, "link.xml"), new[] { sharedA, sharedB }), Is.Empty,
                "同一物理目录的多个 asmdef 必须保持歧义，不能按枚举顺序猜 owner。");

            var parent = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.Parent",
                SourceDirectory = root,
            };
            var child = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.Child",
                SourceDirectory = Path.Combine(root, "Child"),
            };
            Assert.That(FrameworkModuleAudit.ResolveLinkerOwner(
                    Path.Combine(root, "Child", "link.xml"), new[] { parent, child }),
                Is.EqualTo(child.Name));
            Assert.That(FrameworkModuleAudit.ResolveLinkerOwner(
                    Path.Combine(Path.GetDirectoryName(root)!, "link.xml"), new[] { parent, child }),
                Is.Empty, "位于所有 asmdef 上层的 package 根规则应保留为全局/Package 证据。");
        }

        [Test]
        public void HotUpdateInspection_MapsBuildEvidenceWithoutCompileTimeDependency()
        {
            var target = new FrameworkModuleAudit.HotUpdateDeploymentEvidence
            {
                BuildModuleAvailable = true,
                ProfileAvailable = true,
                ProfileCount = 1,
                ProfilePath = "Assets/Game/Settings/Profile.asset",
                ProfileAssemblies = new[] { "Old" },
            };
            var raw = new FakeHotUpdateEvidence
            {
                ProfileAssemblies = new[] { "Core", "Game.Main" },
                HybridClrSettingsAssemblies = new[] { "Core" },
                HybridClrLegacyAssemblies = new[] { "Legacy" },
                SettingsAvailable = true,
                SettingsMatch = false,
                SettingsMessage = "settings drift",
                GenerationRequired = true,
                GenerationFresh = false,
                GenerationMessage = "stamp stale",
                StagingRequired = true,
                StagedManifestExists = true,
                StagedManifestAvailable = true,
                StagedManifestMatches = false,
                StagedVersion = "42",
                StagedAssemblies = new[] { "Core" },
                ExpectedAotMetadataDlls = new[] { "mscorlib.dll" },
                StagedAotMetadataDlls = Array.Empty<string>(),
                MissingStagedFiles = new[] { "Game.Main.dll" },
                UnexpectedStagedFiles = new[] { "Legacy.dll.bytes" },
                InvalidStagedEntries = new[] { "../Unsafe.dll" },
                StagedMessage = "manifest drift",
            };

            FrameworkModuleAudit.ApplyHotUpdateInspection(target, raw);

            Assert.That(target.InspectionAvailable, Is.True);
            Assert.That(target.ProfileAssemblies, Is.EqualTo(new[] { "Core", "Game.Main" }));
            Assert.That(target.SettingsAssemblies, Is.EqualTo(new[] { "Core" }));
            Assert.That(target.LegacySettingsAssemblies, Is.EqualTo(new[] { "Legacy" }));
            Assert.That(target.StagingRequired, Is.True);
            Assert.That(target.StagedManifestExists, Is.True);
            Assert.That(target.StagedVersion, Is.EqualTo("42"));
            Assert.That(target.ExpectedAotMetadataDlls, Is.EqualTo(new[] { "mscorlib.dll" }));
            Assert.That(target.MissingStagedFiles, Is.EqualTo(new[] { "Game.Main.dll" }));
            Assert.That(target.UnexpectedStagedFiles, Is.EqualTo(new[] { "Legacy.dll.bytes" }));
            Assert.That(target.InvalidStagedEntries, Is.EqualTo(new[] { "../Unsafe.dll" }));
            Assert.That(target.RequiresAttention, Is.True);
        }

        [Test]
        public void HotUpdateAttention_DistinguishesMissingProfileFromOptionalPureAotStage()
        {
            var missingProfile = new FrameworkModuleAudit.HotUpdateDeploymentEvidence
            {
                BuildModuleAvailable = true,
            };
            Assert.That(missingProfile.RequiresAttention, Is.True,
                "Build 菜单会创建默认热更档位；缺少唯一真源不能静默判作纯 AOT。 ");

            var pureAot = new FrameworkModuleAudit.HotUpdateDeploymentEvidence
            {
                BuildModuleAvailable = true,
                ProfileAvailable = true,
                ProfileCount = 1,
                InspectionAvailable = true,
                SettingsAvailable = true,
                SettingsMatch = true,
                GenerationRequired = false,
                StagingRequired = false,
                StagedManifestExists = false,
                StagedManifestAvailable = false,
                StagedManifestMatches = true,
            };
            Assert.That(pureAot.RequiresAttention, Is.False,
                "空 Profile 可以明确选择无 CodePackage 的纯 AOT 档位。 ");

            pureAot.StagedManifestExists = true;
            pureAot.StagedManifestAvailable = true;
            pureAot.StagedManifestMatches = false;
            Assert.That(pureAot.RequiresAttention, Is.True,
                "纯 AOT 虽不要求中转，但磁盘上已有漂移清单时应提醒，避免误部署旧代码包。 ");

            pureAot.StagedManifestMatches = true;
            pureAot.ProfileCount = 2;
            Assert.That(pureAot.RequiresAttention, Is.True,
                "多个 Profile 会破坏单一真源，即使首项派生证据全绿也必须提醒。 ");
        }

        [Test]
        public void InstalledModules_ExternalDependenciesAreExplicit_AndDeletionTestsHold()
        {
            var snapshot = FrameworkModuleAudit.Capture();
            foreach (var module in snapshot.Assemblies.Values)
            {
                if (!module.IsFrameworkRuntime) continue;
                Assert.That(module.AsmdefPath,
                    Does.StartWith("Assets/").Or.StartWith("Packages/"),
                    module.Name + " 应保留可由 Unity 定位的稳定 Asset Path。");
                Assert.That(Directory.Exists(module.SourceDirectory), Is.True,
                    module.Name + " 应保留可由 System.IO 读取的真实物理源码目录。");
                var hidden = FrameworkModuleAudit.FindUndeclaredExternalReferences(snapshot, module);
                Assert.That(hidden, Is.Empty, $"{module.Name} 存在 asmdef 不可见的真实外部依赖");
            }

            var core = FrameworkModuleAudit.ComputeReachableAssemblies(
                snapshot.Assemblies, new[] { FrameworkModuleAudit.CoreAssemblyName });
            Assert.That(core, Does.Not.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
            if (snapshot.Assemblies.ContainsKey(FrameworkModuleAudit.UGuiAssemblyName))
            {
                var ugui = FrameworkModuleAudit.ComputeReachableAssemblies(
                    snapshot.Assemblies, new[] { FrameworkModuleAudit.UGuiAssemblyName });
                Assert.That(ugui, Does.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
                Assert.That(ugui, Does.Not.Contain(FrameworkModuleAudit.ToolkitAssemblyName));
                Assert.That(ugui, Does.Not.Contain(FrameworkModuleAudit.BridgeAssemblyName));
            }
            if (snapshot.Assemblies.ContainsKey(FrameworkModuleAudit.ToolkitAssemblyName))
            {
                var toolkit = FrameworkModuleAudit.ComputeReachableAssemblies(
                    snapshot.Assemblies, new[] { FrameworkModuleAudit.ToolkitAssemblyName });
                Assert.That(toolkit, Does.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
                Assert.That(toolkit, Does.Not.Contain(FrameworkModuleAudit.UGuiAssemblyName));
                Assert.That(toolkit, Does.Not.Contain(FrameworkModuleAudit.BridgeAssemblyName));
            }

            var result = FrameworkModuleAudit.Analyze(snapshot);
            Assert.That(result.IsHealthy, Is.True);
            var bridgeStatus = result.ModuleStatuses.FirstOrDefault(status =>
                status.Module.Name == FrameworkModuleAudit.BridgeAssemblyName);
            if (bridgeStatus != null)
                foreach (string blocker in bridgeStatus.RemovalBlockers)
                    Assert.That(bridgeStatus.RemovalSteps, Has.Some.Contains(blocker),
                        "物理删除计划必须列出当前完整 asmdef 图实际发现的每个声明引用。");
            if (result.HotUpdateDeployment.BuildModuleAvailable)
                Assert.That(result.HotUpdateDeployment.InspectionAvailable, Is.True,
                    "安装 Build Editor Module 时，通用审计应经只读反射接缝读取证据，不能建立编译期反向依赖。");
            Assert.That(result.Recommendations, Has.Some.Contains("Player BuildReport"));
            if (result.HasRetentionWarnings)
                Assert.That(result.Recommendations, Has.Some.Contains("link.xml"));

            string report = FrameworkModuleAudit.CreateReport(result);
            Assert.That(report, Does.Not.Contain("⚠ 无法定位程序集文件"),
                "当前轻量档位或热更清单里存在无法解析的程序集时，字节闭包不能算完整。");
            string deletionSection = report.Substring(
                report.IndexOf("删除检查（真实元数据引用闭包）", StringComparison.Ordinal));
            Assert.That(deletionSection, Does.Not.Contain("✗ "),
                "删除检查的文本结论不得只靠测试代码另算后假绿；本地 Generate / 中转证据可在干净 clone 中独立告警。 ");
            Assert.That(report, Does.Contain("Module 当前保留原因"));
            Assert.That(report, Does.Contain("全局与生成的 link.xml 证据"));
            Assert.That(report, Does.Contain("热更派生证据（只读）"));
            if (result.HotUpdateDeployment.BuildModuleAvailable)
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
                var hotUpdateEvidence = window.rootVisualElement.Q<VisualElement>(
                    "module-audit-hot-update-evidence");
                var hotUpdateMetrics = window.rootVisualElement.Q<VisualElement>(
                    "module-audit-hot-update-metrics");
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
                Assert.That(hotUpdateEvidence, Is.Not.Null);
                Assert.That(hotUpdateMetrics, Is.Not.Null);
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
                Assert.That(hotUpdateMetrics.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));

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

        [Test]
        public void LocateProjectAsset_SelectsCommonEditorAssetsWithoutOpeningAnExternalFileHandler()
        {
            string coreAsmdefPath = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .First(path => string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    FrameworkModuleAudit.CoreAssemblyName,
                    StringComparison.Ordinal));
            string auditWindowScriptPath = AssetDatabase.FindAssets(
                    nameof(FrameworkModuleAuditWindow) + " t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .First(path => AssetDatabase.LoadAssetAtPath<MonoScript>(path)?.GetClass() ==
                               typeof(FrameworkModuleAuditWindow));
            string[] projectPaths =
            {
                coreAsmdefPath,
                auditWindowScriptPath,
            };

            var previousSelection = Selection.activeObject;
            try
            {
                foreach (string path in projectPaths)
                {
                    var expected = AssetDatabase.LoadMainAssetAtPath(path);
                    Assert.That(expected, Is.Not.Null, path + " 必须能被 Unity AssetDatabase 定位。");
                    Selection.activeObject = null;
                    Assert.That(FrameworkModuleAuditWindow.TryLocateProjectAsset(path), Is.True);
                    Assert.That(Selection.activeObject, Is.SameAs(expected),
                        "“定位”必须把 Unity Project 的选择切到目标，而不是直接唤起外部编辑器：" + path);
                }
                Assert.That(FrameworkModuleAuditWindow.TryLocateProjectAsset("docs/framework-module-map.md"),
                    Is.False, "Assets/Packages 之外的文档不是 Unity Asset，应交给打开文件或文件浏览器语义。");
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        private sealed class FakeHotUpdateEvidence
        {
            public string[] ProfileAssemblies;
            public string[] HybridClrSettingsAssemblies;
            public string[] HybridClrLegacyAssemblies;
            public bool SettingsAvailable;
            public bool SettingsMatch;
            public string SettingsMessage;
            public bool GenerationRequired;
            public bool GenerationFresh;
            public string GenerationMessage;
            public bool StagingRequired;
            public bool StagedManifestExists;
            public bool StagedManifestAvailable;
            public bool StagedManifestMatches;
            public string StagedVersion;
            public string[] StagedAssemblies;
            public string[] ExpectedAotMetadataDlls;
            public string[] StagedAotMetadataDlls;
            public string[] MissingStagedFiles;
            public string[] UnexpectedStagedFiles;
            public string[] InvalidStagedEntries;
            public string StagedMessage;
        }
    }
}
