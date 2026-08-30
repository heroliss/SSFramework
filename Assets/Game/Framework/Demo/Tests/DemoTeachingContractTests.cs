using System;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Internal;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Tests
{
    /// <summary>验证 Demo 教学契约读取真实 Host 调用语义，而不是依赖源码字面量或最终 USS 结构。</summary>
    public sealed class DemoTeachingContractTests
    {
        [Test]
        public void ConceptChapter_AllowsNoActionsWhenItHasStructuredExplanationAndSource()
        {
            var module = new ContractModule(DemoTeachingKind.Concept, host =>
            {
                host.AddPositioning("先解释心智模型");
                host.AddNote("概念章可以没有按钮。");
                host.AddSectionTitle("核心概念");
                host.AddConcept("Interface", "消费方依赖的稳定能力面。");
                host.AddConcept("Adapter", "可替换的具体实现。");
                host.AddSectionTitle("适用边界");
                host.AddNote("只有存在替换或隔离价值时才引入 Seam。",
                    new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/IDemoModule.cs", "interface IDemoModule"));
            });

            Assert.DoesNotThrow(() => ActivateOnce(module));
        }

        [Test]
        public void NormalChapter_RejectsPositioningThatIsOnlyAStringOrAppearsTooLate()
        {
            var module = new ContractModule(DemoTeachingKind.Capability, host =>
            {
                host.AddSectionTitle("定位：这只是普通标题，不能冒充语义");
                host.AddNote("源码里出现定位字样不代表真实契约成立。");
                host.AddPositioning("真正定位出现得太晚");
                host.AddSectionTitle("边界");
                host.AddNote("用于锁定顺序检查。");
                host.AddActionRow("执行", () => { });
            });

            var error = Assert.Throws<InvalidOperationException>(() => ActivateOnce(module));
            StringAssert.Contains("第一个教学元素", error.Message);
        }

        [Test]
        public void UnavailableChapter_UsesReasonRecoveryContinuationAndSetupSourceInsteadOfNormalRules()
        {
            var module = new ContractModule(DemoTeachingKind.Capability, host =>
                host.AddUnavailable(
                    "缺少场景 Adapter。",
                    "把 Adapter 挂进根 Context 子树。",
                    "先阅读概念章，恢复后再回来操作。",
                    new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "class MonoDemoContext")));

            Assert.DoesNotThrow(() => ActivateOnce(module));
        }

        [Test]
        public void UnavailableChapter_RejectsAnIncompleteRecoveryLoop()
        {
            var module = new ContractModule(DemoTeachingKind.Capability, host =>
                host.AddUnavailable(
                    "缺少场景 Adapter。",
                    " ",
                    "恢复后再回来。",
                    new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "class MonoDemoContext")));

            var error = Assert.Throws<InvalidOperationException>(() => ActivateOnce(module));
            StringAssert.Contains("恢复方式", error.Message);
        }

        [Test]
        public void UnavailableChapter_RejectsNormalContentMixedInAfterFallback()
        {
            var module = new ContractModule(DemoTeachingKind.Capability, host =>
            {
                host.AddUnavailable(
                    "缺少场景 Adapter。",
                    "把 Adapter 挂进根 Context 子树。",
                    "恢复后再回来。",
                    new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "class MonoDemoContext"));
                host.AddActionRow("这个按钮不应出现在降级页", () => { });
            });

            var error = Assert.Throws<InvalidOperationException>(() => ActivateOnce(module));
            StringAssert.Contains("不要再混入", error.Message);
        }

        [Test]
        public void TeachingValidationFailure_UsesTheSameHostThenModuleCleanupPath()
        {
            bool teardownCalled = false;
            var module = new ContractModule(
                DemoTeachingKind.Capability,
                host => host.AddNote("没有定位、动作与完整结构。"),
                () => teardownCalled = true);

            Assert.Throws<InvalidOperationException>(() => ActivateOnce(module));
            Assert.IsTrue(teardownCalled, "教学契约失败也属于 Build 失败，必须释放半构建章节资源。");
        }

        [Test]
        public void ExperimentNotice_RecordsSemanticTraceAndBuildsAStableInformationCard()
        {
            var root = new VisualElement();
            using var host = new DemoModuleHost(root);

            var card = host.AddExperimentNotice(
                "只改本章隔离数据。",
                "Console 出现两条 Warning。",
                "点击恢复按钮即可幂等恢复。");

            Assert.AreEqual(1, host.TeachingTrace.ExperimentNoticeCount);
            Assert.AreEqual(DemoTeachingElement.ExperimentNotice,
                host.TeachingTrace.Elements[0]);
            Assert.IsTrue(card.ClassListContains("demo-experiment"));
            Assert.AreEqual(3,
                card.Query<Label>(className: "demo-experiment-term").ToList().Count);
            Assert.AreEqual(3,
                card.Query<Label>(className: "demo-experiment-desc").ToList().Count);
        }

        [TestCase(null, "证据", "恢复", "impact")]
        [TestCase("影响", " ", "恢复", "expectedEvidence")]
        [TestCase("影响", "证据", "", "recovery")]
        public void ExperimentNotice_RejectsAnIncompleteRecoveryContract(
            string impact,
            string evidence,
            string recovery,
            string parameter)
        {
            using var host = new DemoModuleHost(new VisualElement());
            var error = Assert.Throws<ArgumentException>(() =>
                host.AddExperimentNotice(impact, evidence, recovery));
            Assert.AreEqual(parameter, error.ParamName);
        }

        [Test]
        public void ExperimentAction_RequiresANoticeInTheSameSectionAndRecordsMachineReadableSemantics()
        {
            using var host = new DemoModuleHost(new VisualElement());
            host.AddSectionTitle("实验");

            var error = Assert.Throws<InvalidOperationException>(() =>
                host.AddExperimentActionRow("破坏数据", () => { }));
            StringAssert.Contains("同一小节", error.Message);

            host.AddExperimentNotice("仅测试数据", "一条 Warning", "点击恢复");
            var button = host.AddExperimentActionRow("破坏数据", () => { });

            Assert.AreEqual("教学实验 · 破坏数据", button.text);
            Assert.IsTrue(button.ClassListContains("demo-btn--experiment"));
            Assert.AreEqual(1, host.TeachingTrace.ExperimentActionCount);
            Assert.AreEqual(1, host.TeachingTrace.ActionCount,
                "实验动作仍属于 Capability/Workflow 所需的可执行入口。");

            host.AddSectionTitle("下一节");
            Assert.Throws<InvalidOperationException>(() =>
                host.AddExperimentActionRow("不能借用上一节提示卡", () => { }));
        }

        [Test]
        public void ConceptAndCallouts_ExposeMeaningWithoutDependingOnColor()
        {
            var root = new VisualElement();
            using var host = new DemoModuleHost(root);

            var concept = host.AddConcept("Context", "带生命周期的作用域容器。");
            var tipBody = host.AddTip("这是帮助记忆的口诀。");
            var cautionBody = host.AddCaution("忽略后会产生错误结论。");

            Assert.AreEqual("概念", concept.Q<Label>(className: "demo-concept-kind").text);
            Assert.AreEqual("带生命周期的作用域容器。",
                concept.Q<Label>(className: "demo-concept-term").tooltip,
                "悬停可以补充解释，但同一解释仍必须直接显示在正文中。");
            Assert.AreEqual("重点速记",
                tipBody.parent.Q<Label>(className: "demo-callout-title").text);
            Assert.IsTrue(tipBody.parent.ClassListContains("demo-tip"));
            Assert.AreEqual("注意边界",
                cautionBody.parent.Q<Label>(className: "demo-callout-title").text);
            Assert.IsTrue(cautionBody.parent.ClassListContains("demo-caution"));
            CollectionAssert.Contains(host.TeachingTrace.Elements, DemoTeachingElement.Caution);
        }

        [Test]
        public void Caution_DoesNotAuthorizeAnExperimentAction()
        {
            using var host = new DemoModuleHost(new VisualElement());
            host.AddSectionTitle("错误边界");
            host.AddCaution("这里只是在解释风险，并未提供影响—证据—恢复闭环。");

            Assert.Throws<InvalidOperationException>(() =>
                host.AddExperimentActionRow("仍不能执行", () => { }));
        }

        private static void ActivateOnce(ContractModule module)
        {
            using var catalog = new DemoModuleCatalog(new[] { module });
            using var builder = new ContainerBuilder();
            catalog.InstallBindings(builder);
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            catalog.Initialize(context);
            catalog.Activate(module, new VisualElement());
            catalog.Deactivate();
        }

        private sealed class ContractModule : IDemoModule
        {
            private readonly Action<DemoModuleHost> _build;
            private readonly Action _teardown;

            internal ContractModule(
                DemoTeachingKind teachingKind,
                Action<DemoModuleHost> build,
                Action teardown = null)
            {
                TeachingKind = teachingKind;
                _build = build;
                _teardown = teardown;
            }

            public string Id => "contract-test";
            public string Title => "教学契约测试";
            public string Category => "入门";
            public int Order => 0;
            public string Summary => "验证真实 Build 的教学语义。";
            public bool IsComingSoon => false;
            public DemoTeachingKind TeachingKind { get; }
            public void InstallBindings(ContainerBuilder builder) { }
            public void Initialize(IGameContext context) { }
            public void Build(DemoModuleHost host) => _build(host);
            public void Teardown() => _teardown?.Invoke();
        }
    }
}
