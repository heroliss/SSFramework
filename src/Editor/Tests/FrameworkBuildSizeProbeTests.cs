using System;
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

            Assert.That(plans.Select(plan => plan.Key),
                Is.EqualTo(new[] { "core", "ugui", "toolkit", "full" }));
            Assert.That(plans.Single(plan => plan.Key == "core").Assemblies,
                Is.EqualTo(new[] { FrameworkModuleAudit.CoreAssemblyName }));
            Assert.That(plans.Single(plan => plan.Key == "ugui").Assemblies,
                Does.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
            Assert.That(plans.Single(plan => plan.Key == "ugui").Assemblies,
                Does.Not.Contain(FrameworkModuleAudit.ToolkitAssemblyName));
            Assert.That(plans.Single(plan => plan.Key == "toolkit").Assemblies,
                Does.Not.Contain(FrameworkModuleAudit.UGuiAssemblyName));
            Assert.That(plans.SelectMany(plan => plan.SourceDirectories),
                Has.None.Matches<string>(path => path.Replace('\\', '/').Contains("/Editor/")));
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
                Assert.That(content, Is.Not.Null);
                Assert.That(content.horizontalScrollerVisibility, Is.EqualTo(ScrollerVisibility.Hidden));
                Assert.That(actions, Is.Not.Null);
                Assert.That(actions.childCount, Is.EqualTo(4));
                Assert.That(core?.value, Is.True);
                Assert.That(full?.value, Is.False);
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
