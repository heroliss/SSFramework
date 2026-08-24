using System.Linq;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Demo.Modules;
using Game.Framework.Internal;
using Game.Framework.Systems;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Tests
{
    /// <summary>
    /// 验证响应式列表章展示的实例号与生命周期计数来自真实 BindList 行，而不是与实现脱节的说明文字。
    /// </summary>
    public sealed class ReactiveListModuleEvidenceTests
    {
        [Test]
        public void MoveKeepsRowIdentity_ReplaceRecreatesAndDisposesExactlyOneRow()
        {
            var module = new ReactiveListModule();
            using var catalog = new DemoModuleCatalog(new[] { module });
            using var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            catalog.InstallBindings(builder);
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            catalog.Initialize(context);

            var content = new VisualElement();
            catalog.Activate(module, content);
            context.ExecuteCommand(new AddTodoCommand());
            context.ExecuteCommand(new AddTodoCommand());
            context.ExecuteCommand(new AddTodoCommand());

            var rows = Rows(content);
            var first = rows[0];
            var firstEvidence = (ReactiveListRowEvidence)first.userData;
            Assert.AreEqual(1, firstEvidence.InstanceId);
            Assert.IsFalse(firstEvidence.IsDisposed);

            context.ExecuteCommand(new MoveFirstToEndCommand());
            rows = Rows(content);
            Assert.AreSame(first, rows[2], "Move 必须移动同一个真实 VisualElement，不能重造一张看起来相同的表");
            Assert.IsFalse(firstEvidence.IsDisposed, "Move 不应释放该行 rowBag");
            StringAssert.Contains("创建 3　释放 0　存活 3", EvidenceText(content));

            var replaced = rows[0];
            var replacedEvidence = (ReactiveListRowEvidence)replaced.userData;
            context.ExecuteCommand(new ReplaceFirstTodoCommand());
            rows = Rows(content);

            Assert.AreNotSame(replaced, rows[0], "Replace 的契约是释放旧槽并创建新行");
            Assert.IsTrue(replacedEvidence.IsDisposed, "Replace 必须真实 Dispose 旧行 rowBag");
            Assert.IsNull(replaced.parent, "旧行必须从真实 UI 层级摘除");
            Assert.AreEqual(4, ((ReactiveListRowEvidence)rows[0].userData).InstanceId);
            StringAssert.Contains("创建 4　释放 1　存活 3", EvidenceText(content));

            catalog.Deactivate();
            Assert.IsTrue(rows.All(row => ((ReactiveListRowEvidence)row.userData).IsDisposed),
                "切章时宿主 Bag 应释放所有仍存活的行证据");
        }

        private static VisualElement[] Rows(VisualElement content)
            => content.Query<VisualElement>(className: "demo-list-row").ToList().ToArray();

        private static string EvidenceText(VisualElement content)
            => content.Q<Label>("reactive-list-evidence")?.text ?? string.Empty;
    }
}
