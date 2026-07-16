using System;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using Game.Framework.Event;
using R3;
using UnityEngine.Events;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·R3：一切皆流——Model 状态 / 框架 Event / UnityEvent 都能变成同一种 Observable，
    /// 组合用 R3 操作符在订阅处完成，框架刻意不为"组合"加任何专门 API。
    /// </summary>
    /// <remarks>
    /// 前面各章已经零散用过 RP 订阅、事件订阅、CombineLatest；本章把「入口不同、出口同型」这层心智
    /// 收拢起来讲，并补齐两个前面没露过面的姿势：时间轴操作符（Debounce）与异源合流（Merge）。
    /// 为聚焦操作符本身，流源直接在模块里造（局部 RP / UnityEvent）；真实业务的源头通常是 Model 的 RP 或框架 Event。
    /// </remarks>
    public sealed class R3StreamModule : DemoModuleBase
    {
        public override string Id => "r3-streams";
        public override string Title => "R3 · 一切皆流";
        public override string Category => "进阶";
        public override int Order => 5; // 排在资源三章（10/15/20）前：它是核心章响应式主题的收束，不依赖构建管线知识
        public override string Summary =>
            "Model 状态（RP）/ 框架 Event / UnityEvent 全都能变成 Observable——入口不同、出口同型，" +
            "Where / Debounce / CombineLatest / Merge 等操作符对谁都适用。复杂订阅在订阅处组合，框架零专门 API。";

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddSectionTitle("定位：一切皆流——四种来源，一种出口");
            host.AddConcept("RP<T>（Model 状态）", "本身就是 Observable<T>（IS-A），订阅即得当前值——不需要任何转换。");
            host.AddConcept("框架 Event", "`this.OnEvent<T>()` 桥成流。只是简单收通知时用 `Bag.Subscribe<T>(handler)` 就够，要接操作符才转。");
            host.AddConcept("UnityEvent", "`unityEvent.AsObservable()`（R3 Unity 集成）。UGUI 的 `Button.onClick` 就是 UnityEvent，同一个入口。");
            host.AddConcept("时间 / 帧", "`Observable.Interval` / `Observable.EveryUpdate` 等 R3 自带源，Unity 下默认挂在 PlayerLoop 上。");
            host.AddNote("出口都是 `Observable<T>`——所以下面每个操作符对**任何来源**都适用；" +
                "订阅收尾统一 `Bag.Subscribe(流, handler)`，宿主销毁整条链退订。");

            // ── Where：过滤 ──
            host.AddSectionTitle("Where：一条源流，两路订阅");
            var counter = new RP<int>(0);
            Bag.Add(counter);
            var rawLabel = host.AddValueDisplay();
            Bag.Subscribe(counter, v => rawLabel.text = $"原始流：{v}");
            var evenLabel = host.AddValueDisplay();
            Bag.Subscribe(counter.Where(v => v % 2 == 0), v => evenLabel.text = $"Where 偶数流：{v}");
            host.AddActionRow("计数 +1", () => counter.Value++,
                CodeRef.Here("counter.Where(v => v % 2 == 0)", "Where 过滤"));
            host.AddNote("同一个 `RP<int>` 两路订阅：一路全收，一路 `Where` 只放行偶数。" +
                "操作符**不改源头**——它生成一条新流，各订阅者各看各的。");

            // ── Debounce：时间轴 ──
            host.AddSectionTitle("Debounce：停手才算数");
            var clicks = new RP<int>(0);
            Bag.Add(clicks);
            var clickLabel = host.AddValueDisplay();
            Bag.Subscribe(clicks, v => clickLabel.text = $"点击：{v} 次");
            var calmLabel = host.AddValueDisplay("防抖后收到：—");
            Bag.Subscribe(clicks.Skip(1).Debounce(TimeSpan.FromMilliseconds(500)),
                v => calmLabel.text = $"防抖后收到：第 {v} 次（你停手 0.5 秒了）");
            host.AddActionRow("点我（试试连点）", () => clicks.Value++,
                CodeRef.Here("Debounce(TimeSpan.FromMilliseconds(500))", "Debounce 防抖"));
            host.AddNote("`Debounce(0.5s)`：连点时上面一路每次都动，下面一路只在**停手 0.5 秒后**收到最后一个值——" +
                "搜索框边输边查、拖滑条落盘存档都是它。`Skip(1)` 跳过 RP 的订阅初值，让这条链只看「点击」不看「当前值」。");
            host.AddSubNote("命名提醒：经典 Rx 的 Throttle 在 R3 里改名叫 Debounce；另有 ThrottleFirst（先响应再冷却，" +
                "适合技能按钮防连发）/ ThrottleLast（按节拍取最新）。时间类操作符在 Unity 下默认走 PlayerLoop 时间轴，不用传 TimeProvider。");

            // ── CombineLatest：多源组合 ──
            host.AddSectionTitle("CombineLatest：多源组合出派生值");
            var hp = new RP<int>(100);
            var shield = new RP<int>(50);
            Bag.Add(hp);
            Bag.Add(shield);
            var combinedLabel = host.AddValueDisplay();
            Bag.Subscribe(hp.CombineLatest(shield, (h, s) => (h, s)),
                v => combinedLabel.text = $"有效生命 = {v.h} 血 + {v.s} 盾 = {v.h + v.s}");
            host.AddActionRow("血量 +10", () => hp.Value += 10,
                CodeRef.Here("hp.CombineLatest(shield", "CombineLatest 组合"));
            host.AddActionRow("护盾 +5", () => shield.Value += 5);
            host.AddNote("任一源变化都重算——「派生值」不用手写同步代码。「本地化」章的动态参数 × 语言双源刷新就是同款姿势。");

            // ── Merge：异源合流（点题） ──
            host.AddSectionTitle("Merge：异源汇入同一管道");
            var unityEvent = new UnityEvent();
            int mergedCount = 0;
            var mergedLabel = host.AddValueDisplay("合流：还没收到消息");
            var fromFramework = this.OnEvent<StreamPingEvent>().Select(_ => "框架 Event");
            var fromUnity = unityEvent.AsObservable().Select(_ => "UnityEvent");
            Bag.Subscribe(Observable.Merge(fromFramework, fromUnity),
                src => mergedLabel.text = $"合流：第 {++mergedCount} 条 · 来源 {src}");
            host.AddActionRow("广播框架 Event", () => this.ExecuteCommand(new BroadcastStreamPingCommand()),
                CodeRef.Here("this.OnEvent<StreamPingEvent>()", "OnEvent 桥接"));
            host.AddActionRow("触发 UnityEvent", () => unityEvent.Invoke(),
                CodeRef.Here("unityEvent.AsObservable()", "AsObservable 桥接"));
            host.AddNote("两条来路完全不同的流——事件总线经 `OnEvent<T>()`、UnityEvent 经 `AsObservable()`——" +
                "`Select` 归一成同型后 `Merge` 进**同一条管道**，订阅端不关心消息从哪来。" +
                "这就是「一切皆流」的实际收益：异构事件源在操作符层面互通。");
            host.AddSubNote("View 不能发事件，「广播」经 Command 发（同 Event 章）。这里 new 的裸 UnityEvent 只为演示桥接；" +
                "实战里它通常是 Inspector 序列化字段或 UGUI `Button.onClick`。",
                CodeRef.Here("struct BroadcastStreamPingCommand", "广播 Command"));

            // ── 心智收束 ──
            host.AddSectionTitle("收束：三条心智");
            host.AddConcept("组合在订阅处", "操作符链写在用它的地方（View 绑定处 / System 内）。框架只给入口（RP / OnEvent / AsObservable），组合自由度全留给 R3——不为组合结果加框架重载。");
            host.AddConcept("生命周期统一", "链再长，最后一步都是 Bag.Subscribe 收进 Bag。切到别章再回来，本页所有流水线已随 Teardown 整体退订、重建。");
            host.AddConcept("初值心智", "RP 订阅即得当前值（要纯事件语义就 Skip(1)）；事件流没有当前值（订阅时要个初始通知用 Prepend(...)）。");
            host.AddTip("本章只露最常用的四个操作符，姿势都一样：入口 → 链操作符 → Bag.Subscribe。"
                + "全家福（Zip / Scan / Chunk / DistinctUntilChanged / TakeUntil…）见 R3 官方 README。");
        }
    }

    /// <summary>Merge 演示用的瞬时广播（无数据）。事件须实现 IEvent，推荐 readonly record struct（零堆分配）。</summary>
    public readonly record struct StreamPingEvent : IEvent;

    /// <summary>广播一次 <see cref="StreamPingEvent"/>。View 不能发事件，经 Command 发（ICanSendEvent）。</summary>
    public readonly struct BroadcastStreamPingCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.SendEvent<StreamPingEvent>();
    }
}
