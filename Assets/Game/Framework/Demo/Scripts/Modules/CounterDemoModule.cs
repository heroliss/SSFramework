using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Model;
using R3;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 入门模块：用最小的"计数器"演示 <b>View → Command → Model</b> 单向数据流 + R3 只读订阅。
    /// </summary>
    public sealed class CounterDemoModule : DemoModuleBase
    {
        public override string Id => "counter";
        public override string Title => "最小闭环";
        public override string Category => "入门";
        public override int Order => 10;
        public override string Summary =>
            "以最简单的计数器为例走通一圈：View 通过 Command 表达意图，Command 改 Model，View 只读订阅 Model 的状态流刷新 UI——" +
            "View 不直接碰 Model，所有写操作都走 Command 接缝。";

        // 模块自带的框架层：把 CounterModel 注册进 demo Context。
        public override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterValue(new CounterModel(), typeof(CounterModel));

        public override void Build(DemoModuleHost host)
        {
            host.AddSectionTitle("演示");

            var valueLabel = host.AddValueDisplay("", CodeRef.Here("struct GetCountCommand", "GetCountCommand"));
            // 只读查询 Command 返回 Model 的状态流；订阅即拿当前值（R3 内置），之后每次变化自动推送。
            var count = this.ExecuteCommand(new GetCountCommand());
            Bag.Subscribe(count, v => valueLabel.text = $"当前计数：{v}");

            // CodeRef.Here(...)：指向"本文件"内的声明，路径由编译期自动注入（见 CodeRef.Here 文档）。
            host.AddActionRow("+1", () => this.ExecuteCommand(new IncrementCommand()),
                CodeRef.Here("struct IncrementCommand", "IncrementCommand"));

            host.AddActionRow("重置", () => this.ExecuteCommand(new ResetCountCommand()),
                CodeRef.Here("struct ResetCountCommand", "ResetCountCommand"));

            host.AddSectionTitle("说明");
            host.AddNote("• 点 +1 → 执行 `IncrementCommand` → 在 `ICommandContext` 里 `GetModel<CounterModel>()` 自增 `Count`。",
                CodeRef.Here("class CounterModel", "CounterModel"));
            host.AddNote("• 标签订阅的是 `GetCountCommand` 返回的 `ReadOnlyReactiveProperty<int>`，Model 一变就自动刷新。");
            host.AddTip("注意：View 角色没有 GetModel 权限，只能经 Command 读写 Model——这正是框架强制的单向数据流。");
            host.AddNote("• 这里 Command 直接改 Model 是最简单的形态，逻辑简单时够用；逻辑一多就把它抽到 System，"
                + "Command 退化成只表达意图、调用 System。别把 Command→Model 直连当成终态——见后面「System · 逻辑归位」。");

            // 第一次出现 RP / ReadOnlyReactiveProperty，顺手交代清楚，后续章节不再赘述。
            host.AddSectionTitle("概念说明：响应式属性");
            host.AddConcept("R3",
                "一个响应式编程库（Reactive Extensions 的 Unity 实现）：把「值 / 事件」做成可订阅的流，"
                + "值一变就自动推给所有订阅者，省掉手动轮询和回调管理。框架的响应式状态、事件订阅都建在它之上。");
            host.AddConcept("RP<T>",
                "框架对 R3 响应式属性（`SerializableReactiveProperty<T>`）的简称：一个「可被订阅的值」。"
                + "`.Value` 读写、值一变就自动通知所有订阅者。Model 用它持有可观察状态（这里就是计数）。");
            host.AddConcept("ReadOnlyReactiveProperty<T>",
                "`RP<T>` 的只读视图：只能订阅、读当前值，不能写。查询 Command 把它返回给 View——"
                + "View 看得到、改不了，写只能走 Command；订阅时 R3 会立即推一次当前值（所以一订阅就显示）。");
        }
    }

    /// <summary>计数器 Model：持有一个响应式计数。纯 C# Model（不挂 MonoBehaviour），由 demo Context 注册。</summary>
    public sealed class CounterModel : IModel
    {
        /// <summary>当前计数。<c>RP&lt;int&gt;</c> 是框架对 R3 SerializableReactiveProperty 的简称包装。</summary>
        public readonly RP<int> Count = new(0);
    }

    /// <summary>计数 +1。struct Command：零分配，只能经 <c>ctx.GetModel</c> 访问层。</summary>
    public readonly struct IncrementCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<CounterModel>().Count.Value++;
    }

    /// <summary>计数重置为 0。</summary>
    public readonly struct ResetCountCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<CounterModel>().Count.Value = 0;
    }

    /// <summary>只读查询：返回计数的只读状态流，供 View 订阅（订阅即得当前值）。</summary>
    public readonly struct GetCountCommand : ICommand<ReadOnlyReactiveProperty<int>>
    {
        public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx) => ctx.GetModel<CounterModel>().Count;
    }
}
