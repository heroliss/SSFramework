using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Event;
using Game.Framework.Model;
using R3;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·Event：事件总线 + “状态 vs 事件”对比。状态（Model/RP）有当前值、可随时读；事件只在发生时通知、没有当前值。
    /// </summary>
    public sealed class EventDemoModule : DemoModuleBase
    {
        public override string Id => "event-bus";
        public override string Title => "Event · 事件总线";
        public override string Category => "核心";
        public override int Order => 30;
        public override string Summary =>
            "按类型订阅 / 发送事件（View 能订阅、不能发；发要在 Command/System）。核心对比：状态(Model/RP) 有当前值、随时可读；事件只在发生时通知 N 个监听者、没有当前值。";

        public override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterValue(new TickModel(), typeof(TickModel));

        public override void Build(DemoModuleHost host)
        {
            // ── 状态：Model / RP（有当前值、持久） ──
            host.AddSectionTitle("状态（Model / RP）");
            var stateLabel = host.AddValueDisplay();
            Bag.Subscribe(this.ExecuteCommand(new GetTickCountCommand()), v => stateLabel.text = $"状态计数：{v}");
            host.AddActionRow("状态 +1", () => this.ExecuteCommand(new IncTickCommand()),
                CodeRef.Here("struct IncTickCommand", "IncTickCommand"));
            host.AddNote("订阅即得当前值；切到别章再回来，这个值还在——因为它是 Context 里的持久状态。");

            // ── 事件：Event Bus（无当前值、瞬时） ──
            host.AddSectionTitle("事件（Event Bus）");
            int seen = 0;
            var eventLabel = host.AddValueDisplay();
            eventLabel.text = "本次进入收到广播：0 次";
            Bag.Subscribe<TickEvent>(() =>
            {
                seen++;
                eventLabel.text = $"本次进入收到广播：{seen} 次";
            });
            host.AddActionRow("广播一次事件", () => this.ExecuteCommand(new BroadcastTickCommand()),
                CodeRef.Here("struct BroadcastTickCommand", "BroadcastTickCommand"));
            host.AddNote("事件没有“当前值”——只在发生时通知所有监听者。切到别章再回来这个计数从 0 重来（重新订阅，过去的事件不补发）。",
                CodeRef.Here("record struct TickEvent", "TickEvent"));

            // ── 对比 ──
            host.AddSectionTitle("状态 vs 事件：怎么选");
            host.AddConcept("状态 (Model/RP)", "有当前值、随时可读、订阅即得最新。适合 血量 / 金币 / 等级 这类持续存在的数据。");
            host.AddConcept("事件 (Event)", "没有当前值，只在发生时广播给 N 个监听者。适合 受伤了 / 升级了 / 死亡 这类瞬时信号。");
            host.AddTip("一句判断：“我需要随时读到最新值吗?”→要，用 Model；“只是通知一下发生了某事吗?”→是，用 Event。"
                + "上面切走再回来：状态计数还在、事件计数从 0 重来——这就是两者的本质区别。");
            host.AddNote("权限：View 角色能订阅事件（ICanRegisterEvent）、但不能发——发事件在 Command/System（ICanSendEvent），所以“广播”也经 Command 发出。");
            host.AddNote("带数据用 new MyEvent { Damage = 10 }，订阅端 Bag.Subscribe<MyEvent>(e => ...)。"
                + "GC 提示：struct 事件（readonly record struct）永远零堆分配；只有 class 事件 new 时才有 GC。"
                + "无参 SendEvent<T>() 两者都能编译——struct 发零值、class 发 null（所以 class 事件通常得 new 传数据）。");
        }
    }

    /// <summary>状态侧：一个持久计数（注册进 Context，章节切换不丢）。</summary>
    public sealed class TickModel : IModel
    {
        public readonly RP<int> Count = new(0);
    }

    /// <summary>事件侧：无数据的瞬时广播。事件须实现 IEvent，推荐 readonly record struct（零堆分配）。</summary>
    public readonly record struct TickEvent : IEvent;

    /// <summary>状态 +1（直连 Model，简单操作）。</summary>
    public readonly struct IncTickCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<TickModel>().Count.Value++;
    }

    /// <summary>只读查询：状态计数流。</summary>
    public readonly struct GetTickCountCommand : ICommand<ReadOnlyReactiveProperty<int>>
    {
        public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx) => ctx.GetModel<TickModel>().Count;
    }

    /// <summary>广播一次事件。View 不能发事件，所以经 Command 发（ICanSendEvent）。</summary>
    public readonly struct BroadcastTickCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.SendEvent<TickEvent>();
    }
}
