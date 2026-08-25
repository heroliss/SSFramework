using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor.Tests
{
    /// <summary>
    /// 锁定隔离构建探针的组合来源、依赖最小化、删除复制规则与窄窗结构；真实 Player Build 留给显式慢任务。
    /// </summary>
    public sealed class FrameworkBuildSizeProbeTests
    {
        private const string Manifest = @"{
  ""dependencies"": {
    ""com.cysharp.r3"": ""r3-version"",
    ""com.cysharp.unitask"": ""unitask-version"",
    ""com.tuyoogame.yooasset"": ""3.0.5"",
    ""com.unity.inputsystem"": ""1.20.0"",
    ""com.unity.ugui"": ""2.0.0"",
    ""com.unity.entities"": ""1.4.8"",
    ""com.unity.modules.audio"": ""1.0.0"",
    ""com.unity.modules.ui"": ""1.0.0""
  }
}";

        [Test]
        public void Plans_ReuseAuditProfilesAndRuntimeClosures()
        {
            var plans = FrameworkBuildSizeProbe.CreatePlans();

            Assert.That(plans.Take(4).Select(plan => plan.Key),
                Is.EqualTo(new[] { "core", "ugui", "toolkit", "full" }));
            Assert.That(plans.Skip(4), Is.Not.Empty);
            Assert.That(plans.Skip(4).All(plan => plan.IsAdvanced), Is.True);
            Assert.That(plans.Where(plan => plan.IsAdvanced).SelectMany(plan => plan.RootAssemblies),
                Does.Contain(FrameworkModuleAudit.BridgeAssemblyName));
            Assert.That(plans.Single(plan => plan.Key == "core").Assemblies,
                Is.EqualTo(new[] { FrameworkModuleAudit.CoreAssemblyName }));
            Assert.That(plans.Single(plan => plan.Key == "ugui").Assemblies,
                Does.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
            Assert.That(plans.Single(plan => plan.Key == "ugui").Assemblies,
                Does.Not.Contain(FrameworkModuleAudit.ToolkitAssemblyName));
            Assert.That(plans.Single(plan => plan.Key == "toolkit").Assemblies,
                Does.Not.Contain(FrameworkModuleAudit.UGuiAssemblyName));
            Assert.That(plans.SelectMany(plan => plan.Sources).Select(source => source.AssetDirectory),
                Has.None.Matches<string>(path => path.Replace('\\', '/').Contains("/Editor/")));
            Assert.That(plans.SelectMany(plan => plan.Sources)
                    .All(source => System.IO.Directory.Exists(source.PhysicalDirectory)), Is.True,
                "隔离构建必须拿到 Assets 或 PackageCache 中真实存在的 Module 源码目录。");
        }

        [Test]
        public void LinkXml_PreservesSelectedAssembliesOnceAndEscapesNames()
        {
            string xml = FrameworkBuildSizeProbe.CreateLinkXml(new[]
            {
                "Game.Framework.UI", "Game.Framework", "Game.Framework.UI", "A&B",
            });

            Assert.That(xml, Does.Contain("fullname=\"Game.Framework\" preserve=\"all\""));
            Assert.That(xml, Does.Contain("fullname=\"A&amp;B\""));
            Assert.That(Count(xml, "fullname=\"Game.Framework.UI\""), Is.EqualTo(1));
        }

        [Test]
        public void MinimalManifest_OnlyAddsPackagesRequiredBySelectedModules()
        {
            string core = FrameworkBuildSizeProbe.CreateMinimalManifest(
                Manifest, new[] { FrameworkModuleAudit.CoreAssemblyName });
            Assert.That(core, Does.Contain("com.cysharp.r3"));
            Assert.That(core, Does.Contain("com.cysharp.unitask"));
            Assert.That(core, Does.Contain("com.unity.modules.audio"));
            Assert.That(core, Does.Not.Contain("com.unity.inputsystem"));
            Assert.That(core, Does.Not.Contain("com.unity.ugui"));
            Assert.That(core, Does.Not.Contain("com.tuyoogame.yooasset\": \"3.0.5"));
            Assert.That(core, Does.Not.Contain("com.unity.entities"));

            string ugui = FrameworkBuildSizeProbe.CreateMinimalManifest(Manifest, new[]
            {
                FrameworkModuleAudit.CoreAssemblyName,
                FrameworkModuleAudit.SharedUiAssemblyName,
                FrameworkModuleAudit.UGuiAssemblyName,
            });
            Assert.That(ugui, Does.Contain("com.unity.inputsystem"));
            Assert.That(ugui, Does.Contain("com.unity.ugui"));

            string yoo = FrameworkBuildSizeProbe.CreateMinimalManifest(
                Manifest, new[] { "Game.Framework.Asset.Yoo" });
            Assert.That(yoo, Does.Contain("com.tuyoogame.yooasset\": \"3.0.5"));
        }

        [Test]
        public void ModuleCopy_ExcludesEditorAndTestImplementations()
        {
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Editor/Foo.cs"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Tests/Editor/Foo.cs"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Editor.meta"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Runtime/Foo.cs"), Is.False);
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Contest/Foo.cs"), Is.False);
        }

        [Test]
        public void SourceDirectories_MustBeDisjointToKeepDeletionEvidenceHonest()
        {
            string root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "SSFrameworkProbeSourceOverlap");
            var exception = Assert.Throws<System.IO.InvalidDataException>(() =>
                FrameworkBuildSizeProbe.ValidateDisjointSourceDirectories(new[]
                {
                    new FrameworkBuildSizeProbe.ModuleSourcePlan
                    {
                        AssemblyName = "Game.Framework.Parent",
                        AssetDirectory = "Packages/example/Runtime",
                        PhysicalDirectory = root,
                    },
                    new FrameworkBuildSizeProbe.ModuleSourcePlan
                    {
                        AssemblyName = "Game.Framework.Child",
                        AssetDirectory = "Packages/example/Runtime/Child",
                        PhysicalDirectory = System.IO.Path.Combine(root, "Child"),
                    },
                }));

            Assert.That(exception?.Message, Does.Contain("夹带另一个"));
        }

        [Test]
        public void SourceIdentity_IgnoresCacheRelocationButRejectsPackageDrift()
        {
            var recorded = new FrameworkBuildSizeProbe.ModuleSourcePlan
            {
                AssemblyName = "Game.Framework",
                AssetDirectory = "Packages/com.example.framework/Runtime",
                PhysicalDirectory = "C:/cache/old",
                PackageName = "com.example.framework",
                PackageVersion = "1.0.0",
                PackageId = "com.example.framework@1.0.0",
                SourceFingerprint = "source-hash-1",
            };
            var relocated = new FrameworkBuildSizeProbe.ModuleSourcePlan
            {
                AssemblyName = recorded.AssemblyName,
                AssetDirectory = recorded.AssetDirectory,
                PhysicalDirectory = "D:/cache/new",
                PackageName = recorded.PackageName,
                PackageVersion = recorded.PackageVersion,
                PackageId = recorded.PackageId,
                SourceFingerprint = recorded.SourceFingerprint,
            };

            Assert.That(FrameworkBuildSizeProbe.FindSourceIdentityMismatch(
                new[] { recorded }, new[] { relocated }), Is.Empty);

            relocated.SourceFingerprint = "source-hash-2";
            Assert.That(FrameworkBuildSizeProbe.FindSourceIdentityMismatch(
                new[] { recorded }, new[] { relocated }), Does.Contain("源码身份已变化"));

            relocated.SourceFingerprint = recorded.SourceFingerprint;
            relocated.PackageId = "com.example.framework@2.0.0";
            Assert.That(FrameworkBuildSizeProbe.FindSourceIdentityMismatch(
                new[] { recorded }, new[] { relocated }), Does.Contain("源码身份已变化"));
        }

        [Test]
        public void SourceFingerprint_TracksCopiedContentButIgnoresSkippedEditorFiles()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "SSFrameworkProbeFingerprint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Editor"));
            try
            {
                string runtimeFile = Path.Combine(root, "Runtime.cs");
                string editorFile = Path.Combine(root, "Editor", "Inspector.cs");
                File.WriteAllText(runtimeFile, "runtime-v1");
                File.WriteAllText(editorFile, "editor-v1");
                string original = FrameworkBuildSizeProbe.ComputeModuleSourceFingerprint(root);

                File.WriteAllText(editorFile, "editor-v2");
                Assert.That(FrameworkBuildSizeProbe.ComputeModuleSourceFingerprint(root), Is.EqualTo(original),
                    "Editor/Test 内容不会复制进隔离工程，不应让恢复身份误报漂移。");

                File.WriteAllText(runtimeFile, "runtime-v2");
                Assert.That(FrameworkBuildSizeProbe.ComputeModuleSourceFingerprint(root), Is.Not.EqualTo(original),
                    "实际复制内容变化后，Domain Reload 恢复必须拒绝混用旧报告。 ");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void RecoveryDrift_RejectsProfileRemovedFromCurrentModuleTopology()
        {
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                FormatVersion = 4,
                Profiles = new[]
                {
                    new FrameworkBuildSizeProbe.ProfileRecord
                    {
                        Key = "removed-module",
                        Sources = Array.Empty<FrameworkBuildSizeProbe.ModuleSourcePlan>(),
                    },
                },
            };

            string drift = FrameworkBuildSizeProbe.FindRecoveryDrift(
                report,
                new Dictionary<string, FrameworkBuildSizeProbe.ProfilePlan>(StringComparer.Ordinal));

            Assert.That(drift, Does.Contain("已不在当前 Module 拓扑"));
        }

        [Test]
        public void ShippingOutput_ExcludesUnityDoNotShipEvidenceAndDebugSymbols()
        {
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("GameAssembly.dll"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("Player_Data/globalgamemanagers"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath(
                "Probe_BackUpThisFolder_ButDontShipItWithYourGame/GameAssembly.pdb"), Is.False);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("BurstDebugInformation_DoNotShip/file.txt"),
                Is.False);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("Symbols/GameAssembly.pdb"), Is.False);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("Probe.dSYM/Contents/file"), Is.False);
        }

        [Test]
        public void ChildEnvironment_DoesNotInheritHybridClrIl2CppOverride()
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false };
            startInfo.EnvironmentVariables[FrameworkBuildSizeProbe.UnityIl2CppPathEnvironmentVariable] =
                "D:/main-project/HybridCLRData/LocalIl2CppData";

            FrameworkBuildSizeProbe.SanitizeChildEnvironment(startInfo);

            Assert.That(startInfo.EnvironmentVariables.ContainsKey(
                FrameworkBuildSizeProbe.UnityIl2CppPathEnvironmentVariable), Is.False,
                "隔离工程未安装 HybridCLR，不能继承主 Unity 进程的本地 IL2CPP 路径。 ");
        }

        [Test]
        public void FailedChildLog_PrefersFirstActionableDiagnosticOverMisleadingTail()
        {
            string path = Path.Combine(Path.GetTempPath(), "SSFrameworkProbeLog-" + Guid.NewGuid() + ".log");
            try
            {
                File.WriteAllLines(path, new[]
                {
                    "startup",
                    "DirectoryNotFoundException: missing local il2cpp",
                    "noise after the real failure",
                    "executeMethod class 'SSFrameworkBuildProbeChild' could not be found.",
                });

                string excerpt = FrameworkBuildSizeProbe.ReadDiagnosticLogExcerpt(path, 12);

                Assert.That(excerpt, Does.StartWith("DirectoryNotFoundException:"));
                Assert.That(excerpt, Does.Contain("executeMethod class"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void MarkdownReport_ComputesDeltaAgainstSuccessfulCore()
        {
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                UnityVersion = "6000.test",
                Target = "WebGL",
                ScriptingBackend = "IL2CPP",
                StrippingLevel = "High",
                EvidenceScope = "test",
                Profiles = new[]
                {
                    new FrameworkBuildSizeProbe.ProfileRecord
                    {
                        Key = "core", Title = "只用核心", Status = "成功", OutputBytes = 1024,
                        RawOutputBytes = 4096,
                        Sources = new[]
                        {
                            new FrameworkBuildSizeProbe.ModuleSourcePlan
                            {
                                AssemblyName = FrameworkModuleAudit.CoreAssemblyName,
                                AssetDirectory = "Packages/com.example.framework/Runtime",
                                PhysicalDirectory = "Library/PackageCache/com.example.framework/Runtime",
                                PackageName = "com.example.framework",
                                PackageVersion = "1.2.3",
                                PackageId = "com.example.framework@1.2.3",
                                SourceFingerprint = "abcdef0123456789",
                            },
                        },
                    },
                    new FrameworkBuildSizeProbe.ProfileRecord
                    {
                        Key = "ugui", Title = "核心 + UGUI", Status = "成功", OutputBytes = 3072,
                        RawOutputBytes = 8192,
                    },
                },
            };

            string markdown = FrameworkBuildSizeProbe.CreateMarkdownReport(report);

            Assert.That(markdown, Does.Contain("+2.0 KiB"));
            Assert.That(markdown, Does.Contain("体积上界"));
            Assert.That(markdown, Does.Contain("未选 Module"));
            Assert.That(markdown, Does.Contain("非发布构建证据"));
            Assert.That(markdown, Does.Contain(
                "Game.Framework ← Packages/com.example.framework/Runtime (com.example.framework@1.2.3)"));
            Assert.That(markdown, Does.Contain("实际复制内容 SHA-256：`abcdef0123456789`"));
            Assert.That(JsonUtility.ToJson(report), Does.Not.Contain("PackageCache"),
                "可分享 JSON 只记录稳定 Asset/package 身份，不得泄漏机器专属物理缓存路径。");
        }

        [Test]
        public void Window_UsesProgressiveDisclosureAndResponsiveActions()
        {
            var window = ScriptableObject.CreateInstance<FrameworkBuildSizeProbeWindow>();
            try
            {
                window.position = new Rect(0f, 0f, 360f, 520f);
                window.CreateGUI();

                var content = window.rootVisualElement.Q<ScrollView>("build-size-probe-content");
                var actions = window.rootVisualElement.Q<VisualElement>("build-size-probe-actions");
                var core = window.rootVisualElement.Q<Toggle>("build-size-probe-toggle-core");
                var full = window.rootVisualElement.Q<Toggle>("build-size-probe-toggle-full");
                var advanced = window.rootVisualElement.Q<Foldout>("build-size-probe-advanced-profiles");
                var bridge = window.rootVisualElement.Q<Toggle>(
                    "build-size-probe-toggle-module-game-framework-ui-bridge");
                Assert.That(content, Is.Not.Null);
                Assert.That(content.horizontalScrollerVisibility, Is.EqualTo(ScrollerVisibility.Hidden));
                Assert.That(actions, Is.Not.Null);
                Assert.That(actions.childCount, Is.EqualTo(4));
                Assert.That(core?.value, Is.True);
                Assert.That(full?.value, Is.False);
                Assert.That(advanced, Is.Not.Null);
                Assert.That(advanced.value, Is.False);
                Assert.That(bridge, Is.Not.Null);
                Assert.That(bridge.value, Is.False, "任意 Module 慢构建必须由用户按需选择，不能默认全跑。 ");
                Assert.That(window.rootVisualElement.Q<TextField>(), Is.Null);

                window.ApplyResponsiveLayoutForTests(360f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));
                Assert.That(actions[0].style.flexBasis.keyword, Is.EqualTo(StyleKeyword.Auto));

                window.ApplyResponsiveLayoutForTests(900f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Row));
                Assert.That(actions[0].style.flexGrow.value, Is.EqualTo(1f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        private static int Count(string text, string value)
        {
            int count = 0;
            int offset = 0;
            while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += value.Length;
            }
            return count;
        }
    }
}
