using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Model;
using R3;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·只读投影：读密集 UI 的轻量范式。与其「一字段一查询 Command」，不如用一个查询 Command
    /// 返回打包多个 <c>ReadOnlyReactiveProperty</c> 的「只读投影」对象——一面板一查询，写路径约束不变。
    /// </summary>
    public sealed class ReadProjectionModule : DemoModuleBase
    {
        public override string Id => "read-projection";
        public override string Title => "只读投影";
        public override string Category => "进阶";
        public override int Order => 60;
        public override string Summary =>
            "读密集 UI 的轻量范式：复杂面板要观察很多状态时，别写一堆「一字段一查询」的查询 Command，"
            + "用一个查询 Command 返回打包多个只读源（ReadOnlyReactiveProperty）的「只读投影」对象——一面板一查询。"
            + "投影只暴露只读源，View 看得到、改不了，写仍只能走 Command，单向数据流约束不变。";

        // 本模块自带的框架层：把 StatsModel 注册进 demo 共享 Context。
        public override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterValue(new StatsModel(), typeof(StatsModel));

        public override void Build(DemoModuleHost host)
        {
            host.AddSectionTitle("演示：一次查询拿到整包只读源");

            // 关键点：一个查询 Command 返回打包好的只读投影，而不是每个字段一个查询。
            var stats = this.ExecuteCommand(new GetStatsProjectionCommand());

            var hpLabel = host.AddValueDisplay("", CodeRef.Here("struct GetStatsProjectionCommand", "GetStatsProjectionCommand"));
            var mpLabel = host.AddValueDisplay();
            var goldLabel = host.AddValueDisplay();
            // 投影里的每个源都是 ReadOnlyReactiveProperty：订阅即得当前值（R3 内置），之后自动刷新。
            Bag.Subscribe(stats.Hp, v => hpLabel.text = $"HP：{v}");
            Bag.Subscribe(stats.Mp, v => mpLabel.text = $"MP：{v}");
            Bag.Subscribe(stats.Gold, v => goldLabel.text = $"金币：{v}");

            host.AddActionRow("HP +10", () => this.ExecuteCommand(new AddHpCommand()),
                CodeRef.Here("struct AddHpCommand", "AddHpCommand"));
            host.AddActionRow("MP +5", () => this.ExecuteCommand(new AddMpCommand()));
            host.AddActionRow("金币 +50", () => this.ExecuteCommand(new AddGoldCommand()));

            host.AddSectionTitle("说明");
            host.AddNote("• 一个 `GetStatsProjectionCommand` 返回一个 `StatsProjection`——把 HP / MP / 金币三个只读源打包成一个对象。"
                + "复杂面板要绑 N 个字段时，这比写 N 个「一字段一查询」的查询 Command 省得多：**一面板一查询**。",
                CodeRef.Here("class StatsProjection", "StatsProjection 只读投影"));
            host.AddNote("• 投影对象只暴露 `ReadOnlyReactiveProperty`（不是可写的 `RP`），所以 View 拿到后只能订阅 / 读当前值、改不了；"
                + "写仍只能发 Command（上面三个按钮）。单向数据流约束**没有松动**，只是把读路径的样板收成了一处。");
            host.AddNote("• 投影是「读视图」，不是 Model：它在查询 Command 里现组装、只引用 Model 已有的只读源，不持有状态、不进容器。"
                + "需要派生 / 过滤 / 组合时，投影里直接放 R3 操作符链（如 `model.Hp.Select(...)`）。");
            host.AddTip("权衡：字段少（一两个）时直接「一字段一查询」更直白；字段多的复杂面板才用只读投影收口。深入见框架手册 §8。");
        }
    }

    /// <summary>演示用 Model：持有三个响应式状态。纯 C# Model，由本模块注册进 demo Context。</summary>
    public sealed class StatsModel : IModel
    {
        public readonly RP<int> Hp = new(100);
        public readonly RP<int> Mp = new(50);
        public readonly RP<int> Gold = new(0);
    }

    /// <summary>
    /// 只读投影（CQRS 的 read projection）：把一个面板要观察的多个状态源打包成一个只读对象。
    /// 命名用 <c>Projection</c> 而非 <c>View</c> / <c>Model</c>——后两者是框架的层名，会引起误解。
    /// 只暴露 <see cref="ReadOnlyReactiveProperty{T}"/>——View 能订阅、能读当前值，但改不了（写仍只能走 Command）。
    /// 在查询 Command 里现组装，只引用 Model 已有的只读源，不持有状态、不注册进容器。
    /// </summary>
    public sealed class StatsProjection
    {
        public ReadOnlyReactiveProperty<int> Hp { get; }
        public ReadOnlyReactiveProperty<int> Mp { get; }
        public ReadOnlyReactiveProperty<int> Gold { get; }

        public StatsProjection(StatsModel model)
        {
            // RP<T> IS-A ReadOnlyReactiveProperty<T>，直接赋值，零分配无转换。
            Hp = model.Hp;
            Mp = model.Mp;
            Gold = model.Gold;
        }
    }

    /// <summary>只读查询：返回打包多个只读源的投影，供 View 一次拿全。</summary>
    public readonly struct GetStatsProjectionCommand : ICommand<StatsProjection>
    {
        public StatsProjection Execute(ICommandContext ctx) => new(ctx.GetModel<StatsModel>());
    }

    /// <summary>HP +10。</summary>
    public readonly struct AddHpCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<StatsModel>().Hp.Value += 10;
    }

    /// <summary>MP +5。</summary>
    public readonly struct AddMpCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<StatsModel>().Mp.Value += 5;
    }

    /// <summary>金币 +50。</summary>
    public readonly struct AddGoldCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<StatsModel>().Gold.Value += 50;
    }
}
