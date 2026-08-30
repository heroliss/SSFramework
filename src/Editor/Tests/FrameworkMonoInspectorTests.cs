using System.IO;
using Game.Framework.Context;
using Game.Framework.View;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定 Framework Mono Inspector 的低噪音诊断展示与异常可发现性。</summary>
    public sealed class FrameworkMonoInspectorTests
    {
        [SetUp]
        public void SetUp() => FrameworkMonoDiagnosticsGUI.ResetExpandedStateForTests();

        [TearDown]
        public void TearDown() => FrameworkMonoDiagnosticsGUI.ResetExpandedStateForTests();

        [Test]
        public void DiagnosticsFoldout_DefaultsCollapsedAndKeepsStatePerTarget()
        {
            var firstObject = new GameObject("FirstInspectorTarget");
            var secondObject = new GameObject("SecondInspectorTarget");
            try
            {
                var first = firstObject.AddComponent<MonoGameContextBase>();
                var second = secondObject.AddComponent<MonoGameContextBase>();

                Assert.That(FrameworkMonoDiagnosticsGUI.IsExpanded(first), Is.False);
                Assert.That(FrameworkMonoDiagnosticsGUI.IsExpanded(second), Is.False);

                FrameworkMonoDiagnosticsGUI.SetExpanded(first, true);
                Assert.That(FrameworkMonoDiagnosticsGUI.IsExpanded(first), Is.True);
                Assert.That(FrameworkMonoDiagnosticsGUI.IsExpanded(second), Is.False,
                    "展开一个组件不能把所有 Framework Mono Inspector 一起展开。 ");

                FrameworkMonoDiagnosticsGUI.SetExpanded(first, false);
                Assert.That(FrameworkMonoDiagnosticsGUI.IsExpanded(first), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(secondObject);
            }
        }

        [Test]
        public void CollapsedDiagnostics_UninitializedContextOnlyWarnsForActivePlayObject()
        {
            var host = new GameObject("UninitializedInspectorContext");
            try
            {
                var context = host.AddComponent<MonoGameContextBase>();

                Assert.That(FrameworkMonoDiagnosticsGUI.TryGetCollapsedIssue(
                    context, editorIsPlaying: false, out _, out _), Is.False,
                    "Edit Mode 中尚未执行 Awake 是普通资产状态。 ");
                Assert.That(FrameworkMonoDiagnosticsGUI.TryGetCollapsedIssue(
                    context, editorIsPlaying: true, out string summary, out MessageType messageType), Is.True);
                Assert.That(messageType, Is.EqualTo(MessageType.Warning));
                Assert.That(summary, Does.Contain("Uninitialized").And.Contain("展开"));

                host.SetActive(false);
                Assert.That(FrameworkMonoDiagnosticsGUI.TryGetCollapsedIssue(
                    context, editorIsPlaying: true, out _, out _), Is.False,
                    "非激活分支尚未执行 Awake 不应制造警告。 ");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void CollapsedDiagnostics_MissingContextDoesNotWarnForDisabledLayerComponent()
        {
            var host = new GameObject("DisabledInspectorLayer");
            try
            {
                var view = host.AddComponent<InspectorViewProbe>();
                Assert.That(FrameworkMonoDiagnosticsGUI.TryGetCollapsedIssue(
                    view, editorIsPlaying: true, out _, out _), Is.True,
                    "激活且尚未解析 Context 的层组件仍应保留折叠警告。 ");

                view.enabled = false;
                Assert.That(FrameworkMonoDiagnosticsGUI.TryGetCollapsedIssue(
                    view, editorIsPlaying: true, out _, out _), Is.False,
                    "禁用组件没有执行或维持运行时解析是正常状态，不应制造诊断噪音。 ");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void SharedInspectorSource_HasNoPerComponentFullDiagnosticsButton()
        {
            string source = File.ReadAllText(FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                "FrameworkMonoInspectors.cs", "Game.Framework.Editor").PhysicalPath);

            Assert.That(source, Does.Not.Contain("打开完整框架诊断"),
                "完整诊断已有菜单、工具中心与 Demo 入口，不应在每个 Mono Inspector 重复。 ");
            Assert.That(source, Does.Contain("EditorGUILayout.Foldout"));
        }

        private sealed class InspectorViewProbe : MonoViewBase { }
    }
}
