using System;
using System.Linq;
using Game.Framework.Context;
using Game.Framework.Flow;
using Game.Framework.Internal;
using Game.Framework.Systems;
using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定诊断面板的状态分类与响应式信息优先级，避免修 UI 时悄悄改变诊断语义。</summary>
    public sealed class FrameworkDiagnosticsWindowTests
    {
        [TestCase(0, "未初始化（Uninitialized）")]
        [TestCase(1, "初始化中（Initializing）")]
        [TestCase(2, "就绪（Ready）")]
        [TestCase(3, "失败（Failed）")]
        [TestCase(4, "已释放（Disposed）")]
        public void MonoContextStateLabel_IsChineseFirstAndKeepsCodeValue(
            int stateValue,
            string expected)
        {
            var state = (MonoContextDiagnosticState)stateValue;
            Assert.That(MonoContextIssueAnalysis.StateLabel(state), Is.EqualTo(expected));
        }

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

        [TestCase(0, 420f)]
        [TestCase(0, 730f)]
        [TestCase(1, 700f)]
        [TestCase(2, 1000f)]
        public void MonoIssuePaneHeight_NeverExceedsResponsiveMaximum(
            int modeValue,
            float windowHeight)
        {
            var mode = (FrameworkDiagnosticsWindow.LayoutMode)modeValue;
            float preferred = FrameworkDiagnosticsWindow.ResolveMonoIssuePaneHeight(mode, windowHeight);

            Assert.That(preferred, Is.GreaterThanOrEqualTo(120f));
            Assert.That(preferred, Is.LessThanOrEqualTo(
                FrameworkDiagnosticsWindow.ResolveMonoIssueMaxHeight(mode)));
        }

        [TestCase(0, 0, 0, "")]
        [TestCase(2, 0, 3, " · Mono 根因 2（影响 3）")]
        [TestCase(0, 1, 2, " · Mono 时序提醒 1（影响 2）")]
        [TestCase(1, 2, 4, " · Mono 根因 1 · 时序提醒 2（影响 4）")]
        public void MonoIssueSummary_DistinguishesFailuresFromTimingConcerns(
            int rootCauses,
            int timingGroups,
            int affected,
            string expected)
        {
            Assert.That(FrameworkDiagnosticsWindow.BuildMonoIssueSummary(
                rootCauses,
                timingGroups,
                affected), Is.EqualTo(expected));
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
                var commandHint = root.Q<HelpBox>("diagnostics-command-hint");
                var mode = FrameworkDiagnosticsWindow.ResolveLayoutMode(width);

                Assert.That(split, Is.Not.Null);
                Assert.That(split.orientation, Is.EqualTo(expectedOrientation));
                Assert.That(table, Is.Not.Null);
                Assert.That(table.columns.Count(column => column.visible), Is.EqualTo(expectedVisibleColumns));
                Assert.That(search, Is.Not.Null);
                Assert.That(search.parent.name, Is.EqualTo(expectedSearchParent));
                Assert.That(commandHint, Is.Not.Null);
                Assert.That(commandHint.text, Is.EqualTo(
                    FrameworkDiagnosticsWindow.ResolveCommandHint(mode)));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void CompactCommandHint_PreservesNextStepWithoutRenderingLongCodeBlock()
        {
            string compact = FrameworkDiagnosticsWindow.ResolveCommandHint(
                FrameworkDiagnosticsWindow.LayoutMode.Compact);
            string full = FrameworkDiagnosticsWindow.ResolveCommandHint(
                FrameworkDiagnosticsWindow.LayoutMode.Medium);

            Assert.That(compact, Does.Contain("显式接入"));
            Assert.That(compact, Does.Contain(nameof(LoggingCommandSystem)));
            Assert.That(compact, Does.Not.Contain("builder.RegisterValue"));
            Assert.That(full, Does.Contain("builder.RegisterValue"));
            Assert.That(full, Does.Contain(nameof(LoggingCommandSystem)));
        }

        [Test]
        public void LocalGameFlowLookup_ReadsConstructedBindingWithoutTriggeringFactory()
        {
            var flow = new GameFlow();
            using var directBuilder = new ContainerBuilder();
            directBuilder.RegisterOwnedSystem(flow);
            directBuilder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            using var directContext = new GameContext(directBuilder.Build(), inheritFromGlobal: false);

            Assert.That(FrameworkDiagnosticsWindow.ResolveLocalGameFlow(directContext), Is.SameAs(flow));

            int factoryCalls = 0;
            using var factoryBuilder = new ContainerBuilder();
            factoryBuilder.RegisterOwnedFactory(
                _ =>
                {
                    factoryCalls++;
                    return new GameFlow();
                },
                typeof(IGameFlow));
            factoryBuilder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            using var factoryContext = new GameContext(factoryBuilder.Build(), inheritFromGlobal: false);

            Assert.That(FrameworkDiagnosticsWindow.ResolveLocalGameFlow(factoryContext), Is.Null);
            Assert.That(factoryCalls, Is.Zero, "诊断读取不得让 Lazy Factory 产生运行时副作用");
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

        [Test]
        public void MonoIssues_ParentFailureGroupsThreeAffectedContextsUnderOneRootCause()
        {
            var rootObject = new GameObject("RootContext");
            var childObject = new GameObject("ChildContext");
            var grandchildObject = new GameObject("GrandchildContext");
            childObject.transform.SetParent(rootObject.transform);
            grandchildObject.transform.SetParent(childObject.transform);

            try
            {
                var root = rootObject.AddComponent<TestMonoContext>();
                var child = childObject.AddComponent<TestMonoContext>();
                var grandchild = grandchildObject.AddComponent<TestMonoContext>();
                var rootCause = new InvalidOperationException("install-boom");
                var childFailure = new InvalidOperationException("child failed because parent failed", rootCause);
                var grandchildFailure = new InvalidOperationException("grandchild failed because parent failed", childFailure);

                var candidates = new[]
                {
                    Candidate(root, resolvedParent: null, failure: rootCause),
                    Candidate(child, root, childFailure),
                    Candidate(grandchild, child, grandchildFailure),
                };

                var groups = MonoContextIssueAnalysis.GroupCandidates(candidates);

                Assert.That(groups, Has.Count.EqualTo(1));
                Assert.That(groups[0].Origin.Host, Is.SameAs(root));
                Assert.That(groups[0].RootCause, Is.SameAs(rootCause));
                Assert.That(groups[0].Affected, Has.Count.EqualTo(3));
                Assert.That(groups[0].Affected.Select(item => item.Host),
                    Is.EquivalentTo(new[] { root, child, grandchild }));

                string report = MonoContextIssueAnalysis.BuildCopyReport(groups[0], editorIsPlaying: false);
                Assert.That(report, Does.Contain("历史证据"));
                Assert.That(report, Does.Contain("影响：3 个 Context"));
                Assert.That(report, Does.Contain("install-boom"));
                Assert.That(report, Does.Contain("RootContext/ChildContext/GrandchildContext"));
            }
            finally
            {
                Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void MonoIssues_SharedExceptionFromUnrelatedContextsRemainsIndependentRoots()
        {
            var firstObject = new GameObject("FirstContext");
            var secondObject = new GameObject("SecondContext");
            try
            {
                var first = firstObject.AddComponent<TestMonoContext>();
                var second = secondObject.AddComponent<TestMonoContext>();
                var sharedFailure = new InvalidOperationException("same-instance");
                var candidates = new[]
                {
                    Candidate(first, resolvedParent: null, failure: sharedFailure),
                    Candidate(second, resolvedParent: null, failure: sharedFailure),
                };

                var groups = MonoContextIssueAnalysis.GroupCandidates(candidates);

                Assert.That(groups, Has.Count.EqualTo(2),
                    "即使异常对象被重用，没有实际父子链的两个 Context 也不能合并。 ");
            }
            finally
            {
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(secondObject);
            }
        }

        [Test]
        public void MonoIssues_ParentAndChildWithSameMessageButDifferentExceptionsRemainIndependentRoots()
        {
            var parentObject = new GameObject("ParentContext");
            var childObject = new GameObject("ChildContext");
            childObject.transform.SetParent(parentObject.transform);
            try
            {
                var parent = parentObject.AddComponent<TestMonoContext>();
                var child = childObject.AddComponent<TestMonoContext>();
                var candidates = new[]
                {
                    Candidate(parent, resolvedParent: null, failure: new InvalidOperationException("same-message")),
                    Candidate(child, parent, new InvalidOperationException("same-message")),
                };

                var groups = MonoContextIssueAnalysis.GroupCandidates(candidates);

                Assert.That(groups, Has.Count.EqualTo(2),
                    "父子关系本身不足以证明级联；必须保留同一个最深异常对象身份。 ");
            }
            finally
            {
                Object.DestroyImmediate(parentObject);
            }
        }

        [Test]
        public void MonoIssues_NoExceptionParentChainFormsTimingConcernInsteadOfFailureRoot()
        {
            var parentObject = new GameObject("WaitingParent");
            var childObject = new GameObject("WaitingChild");
            childObject.transform.SetParent(parentObject.transform);
            try
            {
                var parent = parentObject.AddComponent<TestMonoContext>();
                var child = childObject.AddComponent<TestMonoContext>();
                var candidates = new[]
                {
                    Candidate(parent, resolvedParent: null, failure: null,
                        state: MonoContextDiagnosticState.Initializing),
                    Candidate(child, parent, failure: null,
                        state: MonoContextDiagnosticState.Uninitialized),
                };

                var groups = MonoContextIssueAnalysis.GroupCandidates(candidates);

                Assert.That(groups, Has.Count.EqualTo(1));
                Assert.That(groups[0].IsTimingConcern, Is.True);
                Assert.That(groups[0].RootCause, Is.Null);
                Assert.That(MonoContextIssueAnalysis.BuildCopyReport(groups[0], editorIsPlaying: true),
                    Does.Contain("同一条父子未就绪链"));
            }
            finally
            {
                Object.DestroyImmediate(parentObject);
            }
        }

        [Test]
        public void MonoIssueSignature_ChangesWhenPathOrParentTopologyChanges()
        {
            var parentObject = new GameObject("SignatureParent");
            var childObject = new GameObject("SignatureChild");
            childObject.transform.SetParent(parentObject.transform);
            try
            {
                var parent = parentObject.AddComponent<TestMonoContext>();
                var child = childObject.AddComponent<TestMonoContext>();
                var failure = new InvalidOperationException("signature-boom");
                var linked = MonoContextIssueAnalysis.GroupCandidates(new[]
                {
                    Candidate(parent, resolvedParent: null, failure: failure, path: "Root/Parent"),
                    Candidate(child, parent, failure, path: "Root/Parent/Child"),
                });
                var moved = MonoContextIssueAnalysis.GroupCandidates(new[]
                {
                    Candidate(parent, resolvedParent: null, failure: failure, path: "Renamed/Parent"),
                    Candidate(child, resolvedParent: null, failure: failure, path: "Renamed/Child"),
                });

                string linkedSignature = MonoContextIssueAnalysis.BuildSignature(linked, editorIsPlaying: true);
                string movedSignature = MonoContextIssueAnalysis.BuildSignature(moved, editorIsPlaying: true);

                Assert.That(movedSignature, Is.Not.EqualTo(linkedSignature),
                    "重命名或改挂父级后必须重建卡片和定位闭包。 ");
            }
            finally
            {
                Object.DestroyImmediate(parentObject);
            }
        }

        [Test]
        public void DescribeParent_RecognizesDestroyedUnityContext()
        {
            var parentObject = new GameObject("DestroyedParent");
            var parent = parentObject.AddComponent<TestMonoContext>();
            IGameContext interfaceReference = parent;

            Object.DestroyImmediate(parentObject);

            Assert.That(MonoContextIssueAnalysis.DescribeParent(interfaceReference),
                Is.EqualTo("（对象已销毁）"));
        }

        [Test]
        public void MonoIssues_ParentCycleFormsOneDeterministicGroupAndReportsCycle()
        {
            var firstObject = new GameObject("CycleB");
            var secondObject = new GameObject("CycleA");
            try
            {
                var first = firstObject.AddComponent<TestMonoContext>();
                var second = secondObject.AddComponent<TestMonoContext>();
                var sharedFailure = new InvalidOperationException("circular-context");
                var candidates = new[]
                {
                    Candidate(first, resolvedParent: second, failure: sharedFailure, path: "CycleB"),
                    Candidate(second, resolvedParent: first, failure: sharedFailure, path: "CycleA"),
                };

                var groups = MonoContextIssueAnalysis.GroupCandidates(candidates);

                Assert.That(groups, Has.Count.EqualTo(1));
                Assert.That(groups[0].HasParentCycle, Is.True);
                Assert.That(groups[0].Origin.Path, Is.EqualTo("CycleA"),
                    "循环没有天然根节点，应选择稳定排序的定位起点。 ");
                Assert.That(groups[0].Affected, Has.Count.EqualTo(2));
                Assert.That(MonoContextIssueAnalysis.BuildCopyReport(groups[0], editorIsPlaying: true),
                    Does.Contain("父级链存在循环"));
            }
            finally
            {
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(secondObject);
            }
        }

        [TestCase(true, "当前 Play")]
        [TestCase(false, "历史证据")]
        public void MonoIssueEvidenceLabel_DistinguishesCurrentAndPreviousPlay(bool playing, string expected)
        {
            Assert.That(MonoContextIssueAnalysis.EvidenceLabel(playing), Does.StartWith(expected));
        }

        private static MonoContextIssueAnalysis.Candidate Candidate(
            TestMonoContext host,
            IGameContext resolvedParent,
            Exception failure,
            string path = null,
            MonoContextDiagnosticState state = MonoContextDiagnosticState.Failed)
        {
            var snapshot = new MonoContextDiagnosticSnapshot(
                state,
                resolvedParent,
                context: null,
                failure: failure);
            return new MonoContextIssueAnalysis.Candidate(host, snapshot, path);
        }

        private sealed class TestMonoContext : MonoGameContextBase
        {
            protected override void InstallBindings(ContainerBuilder builder)
            {
            }
        }
    }
}
