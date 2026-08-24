using System.Linq;
using Game.Framework.Context;
using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定诊断面板的状态分类与响应式信息优先级，避免修 UI 时悄悄改变诊断语义。</summary>
    public sealed class FrameworkDiagnosticsWindowTests
    {
        [TestCase(0f, 0)]
        [TestCase(639.99f, 0)]
        [TestCase(640f, 1)]
        [TestCase(959.99f, 1)]
        [TestCase(960f, 2)]
        public void ResolveLayoutMode_UsesStableBoundaries(float width, int expected)
        {
            Assert.That((int)FrameworkDiagnosticsWindow.ResolveLayoutMode(width), Is.EqualTo(expected));
        }

        [Test]
        public void CompactCommandColumns_KeepPrimaryDiagnosis()
        {
            var mode = FrameworkDiagnosticsWindow.LayoutMode.Compact;

            Assert.That(FrameworkDiagnosticsWindow.IsCommandColumnVisible(
                mode, FrameworkDiagnosticsWindow.CommandColumnId.Command), Is.True);
            Assert.That(FrameworkDiagnosticsWindow.IsCommandColumnVisible(
                mode, FrameworkDiagnosticsWindow.CommandColumnId.Duration), Is.True);
            Assert.That(FrameworkDiagnosticsWindow.IsCommandColumnVisible(
                mode, FrameworkDiagnosticsWindow.CommandColumnId.Status), Is.True);
            Assert.That(FrameworkDiagnosticsWindow.IsCommandColumnVisible(
                mode, FrameworkDiagnosticsWindow.CommandColumnId.Time), Is.False);
            Assert.That(FrameworkDiagnosticsWindow.IsCommandColumnVisible(
                mode, FrameworkDiagnosticsWindow.CommandColumnId.Context), Is.False);
        }

        [Test]
        public void ResponsiveSplitDimensions_StayWithinUsableBounds()
        {
            Assert.That(FrameworkDiagnosticsWindow.ResolveCommandPaneDimension(
                FrameworkDiagnosticsWindow.LayoutMode.Compact, 420f), Is.InRange(90f, 180f));
            Assert.That(FrameworkDiagnosticsWindow.ResolveCommandPaneDimension(
                FrameworkDiagnosticsWindow.LayoutMode.Wide, 1200f), Is.InRange(90f, 220f));
            Assert.That(FrameworkDiagnosticsWindow.ResolveTreePaneDimension(
                FrameworkDiagnosticsWindow.LayoutMode.Compact, 280f, 420f), Is.InRange(100f, 220f));
            Assert.That(FrameworkDiagnosticsWindow.ResolveTreePaneDimension(
                FrameworkDiagnosticsWindow.LayoutMode.Medium, 640f, 700f), Is.InRange(220f, 340f));
        }

        [TestCase(420f, TwoPaneSplitViewOrientation.Vertical, 3, "diagnostics-toolbar-search-row")]
        [TestCase(720f, TwoPaneSplitViewOrientation.Horizontal, 5, "diagnostics-toolbar-search-row")]
        [TestCase(1100f, TwoPaneSplitViewOrientation.Horizontal, 7, "diagnostics-toolbar-actions")]
        public void CreateGUI_InitialWidthBuildsExpectedResponsiveStructure(
            float width,
            TwoPaneSplitViewOrientation expectedOrientation,
            int expectedVisibleColumns,
            string expectedSearchParent)
        {
            var window = ScriptableObject.CreateInstance<FrameworkDiagnosticsWindow>();
            try
            {
                window.position = new Rect(0f, 0f, width, 700f);
                window.CreateGUI();

                var root = window.rootVisualElement;
                var split = root.Q<TwoPaneSplitView>("diagnostics-context-split");
                var table = root.Q<MultiColumnListView>("diagnostics-command-table");
                var search = root.Q<ToolbarSearchField>("diagnostics-tree-search");

                Assert.That(split, Is.Not.Null);
                Assert.That(split.orientation, Is.EqualTo(expectedOrientation));
                Assert.That(table, Is.Not.Null);
                Assert.That(table.columns.Count(column => column.visible), Is.EqualTo(expectedVisibleColumns));
                Assert.That(search, Is.Not.Null);
                Assert.That(search.parent.name, Is.EqualTo(expectedSearchParent));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void UninitializedActiveContext_IsNormalInEditMode_AndSuspiciousInPlayMode()
        {
            var host = new GameObject("UninitializedContext");
            try
            {
                var context = host.AddComponent<TestMonoContext>();

                Assert.That(FrameworkDiagnosticsWindow.ShouldReportMonoIssue(context, editorIsPlaying: false),
                    Is.False);
                Assert.That(FrameworkDiagnosticsWindow.ShouldReportMonoIssue(context, editorIsPlaying: true),
                    Is.True);

                host.SetActive(false);
                Assert.That(FrameworkDiagnosticsWindow.ShouldReportMonoIssue(context, editorIsPlaying: true),
                    Is.False, "非激活场景分支尚未执行 Awake 是正常状态");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private sealed class TestMonoContext : MonoGameContextBase
        {
            protected override void InstallBindings(ContainerBuilder builder)
            {
            }
        }
    }
}
