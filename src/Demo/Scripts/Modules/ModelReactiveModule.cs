using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Model;
using R3;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·Model：状态怎么持有 + 两条注册路径对比——纯 C#（代码注册，白盒原理）vs Mono（Hierarchy 节点，自动注册 + Inspector 可视/可配）。
    /// </summary>
    /// <remarks>
    /// RP 基础在「计数器」已演示过，这里不重复；本章焦点是“同一种状态，两种进容器的方式”，以及 Mono 路径独有的 Inspector 便利。
    /// </remarks>
    public sealed class ModelReactiveModule : DemoModuleBase
    {
        public override string Id => "model-reactive";
        public override string Title => "Model · 状态与 Inspector";
        public override string Category => "核心";
        public override int Order => 10;
        public override string Summary =>
            "Model 用 RP<T> 持有响应式状态。两条注册路径：纯 C#（代码 new + 注册，原理透明）vs Mono（挂成 Hierarchy 节点，自动注册 + Inspector 实时可见、可设初值）。";

        public override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterValue(new CodeScoreModel(), typeof(CodeScoreModel));
        // 注：MonoScoreModel 不在这里注册——它作为场景节点走 Mono 路径，在自己的 Awake 自动注册进同一个 Context。

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddSectionTitle("定位：同一种状态，两条进容器的路");
            host.AddNote("Model 用 `RP<T>` 持有响应式状态（RP 基础见「最小闭环」）。本章焦点是**两条注册路径**：纯 C#（代码 new + 注册，原理透明、可热更可单测）vs Mono（挂成 Hierarchy 节点，自动注册 + Inspector 实时可见可配）。同一份状态，两种进容器方式。");

            // ── 纯 C# 路径：原理最透明 ──
            host.AddSectionTitle("纯 C# Model（白盒原理）");
            var codeLabel = host.AddValueDisplay("", CodeRef.Here("struct GetCodeScoreCommand", "GetCodeScoreCommand"));
            Bag.Subscribe(this.ExecuteCommand(new GetCodeScoreCommand()), v => codeLabel.text = $"分数：{v}");
            host.AddActionRow("分数 +1", () => this.ExecuteCommand(new RaiseCodeScoreCommand()),
                CodeRef.Here("class CodeScoreModel", "CodeScoreModel"));
            host.AddNote("代码里 new 出来、在 `InstallBindings` 注册进容器。原理一目了然，但它只是个普通 C# 对象——Inspector 里看不到、也没法配。",
                CodeRef.Here("InstallBindings", "注册代码"));

            // ── Mono 路径：Hierarchy 节点 + Inspector ──
            host.AddSectionTitle("Mono Model（Hierarchy + Inspector）");
            var monoLabel = host.AddValueDisplay("", CodeRef.Here("struct GetMonoScoreCommand", "GetMonoScoreCommand"));
            Bag.Subscribe(this.ExecuteCommand(new GetMonoScoreCommand()), v => monoLabel.text = $"分数：{v}");
            host.AddActionRow("分数 +1", () => this.ExecuteCommand(new RaiseMonoScoreCommand()),
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/MonoScoreModel.cs", "class MonoScoreModel", "MonoScoreModel"));
#if UNITY_EDITOR
            host.AddActionRow("选中到 Inspector", SelectMonoModelInInspector,
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/MonoScoreModel.cs", "class MonoScoreModel", "Mono Model 定义"));
#endif
            host.AddTip("MonoScoreModel 挂成 demo 根 Context 子树里的场景节点（ChapterAssets/ScoreModel），Awake 自动注册进同一个 Context——不用写一行注册代码。"
                + "点「选中到 Inspector」：运行时不仅能看 RP 分数随按钮实时跳动，还能反过来——在 Inspector 里直接改这个值，上方 UI 分数会同步刷新："
                + "RP 的 Drawer 改的就是 ReactiveProperty.Value，所有订阅者即时收到，等于没写一行代码就发了次状态更新（双向实时，框架小亮点）。"
                + "停止运行后改的则是序列化初值，下次运行生效。");

            // ── 怎么选 ──
            host.AddSectionTitle("两条路怎么选");
            host.AddConcept("纯 C#", "逻辑型 / 要热更 / 要单测 / 不需要在 Inspector 配的状态。零 Unity 依赖、原理透明。");
            host.AddConcept("Mono", "需要 Inspector 可视化、策划要填初值或拖引用的状态。自动注册、所见即所得。");
            host.AddNote("两者都用同一个 `RP<T>` 持有状态、都经 Command 读写、都进同一个 `Context`——区别只在“怎么进容器”。");
        }

#if UNITY_EDITOR
        // 编辑器便利：把场景里的 Mono Model 选中并高亮，方便你立刻在 Inspector 看它的 RP。这是 demo 导航，不是框架用法。
        private static void SelectMonoModelInInspector()
        {
            var model = Object.FindFirstObjectByType<MonoScoreModel>();
            if (model != null) DemoEditorNav.PingSceneObject(model.gameObject);
        }
#endif
    }

    /// <summary>纯 C# Model：一个分数。由本章 InstallBindings 注册（纯 C# 路径）。</summary>
    public sealed class CodeScoreModel : IModel
    {
        public readonly RP<int> Score = new(0);
    }

    /// <summary>分数 +1（纯 C# Model）。</summary>
    public readonly struct RaiseCodeScoreCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<CodeScoreModel>().Score.Value++;
    }

    /// <summary>只读查询：纯 C# Model 的分数流。</summary>
    public readonly struct GetCodeScoreCommand : ICommand<ReadOnlyReactiveProperty<int>>
    {
        public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx) => ctx.GetModel<CodeScoreModel>().Score;
    }

    /// <summary>分数 +1（Mono Model）。</summary>
    public readonly struct RaiseMonoScoreCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<MonoScoreModel>().Score.Value++;
    }

    /// <summary>只读查询：Mono Model 的分数流。</summary>
    public readonly struct GetMonoScoreCommand : ICommand<ReadOnlyReactiveProperty<int>>
    {
        public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx) => ctx.GetModel<MonoScoreModel>().Score;
    }

    /// <summary>分数归零（Mono Model）。</summary>
    public readonly struct ResetMonoScoreCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<MonoScoreModel>().Score.Value = 0;
    }
}
