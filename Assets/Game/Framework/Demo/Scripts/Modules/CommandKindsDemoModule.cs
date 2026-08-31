using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Model;
using R3;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·Command 三态：同步 / 异步(带取消) / 查询(返回值)。同步在计数器已演，这里聚焦异步与查询。
    /// </summary>
    public sealed class CommandKindsDemoModule : DemoModuleBase
    {
        public override string Id => "command-kinds";
        public override string Title => "命令（Command）· 三种形态";
        public override string Category => "核心";
        public override int Order => 20;
        public override string Summary =>
            "Command 三种形态：同步（ICommand，计数器已演）、异步（IAsyncCommand，带取消令牌）、查询（ICommand<T>，返回值）。本章聚焦异步与查询，含查询的进阶形态「只读投影」（一面板一查询）。";

        public override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterModel(new TaskModel());

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("Command 的三种形态");
            host.AddNote("上一章确定了状态由 Model 持有；Command 则是外部读写这些状态的统一入口。同一接缝有三态：**同步** `ICommand`（计数器已演）、**异步** `IAsyncCommand`（带取消令牌）、**查询** `ICommand<T>`（返回值）。本章聚焦异步与查询。");

            host.AddSectionTitle("为什么 View 不直接调用 System");
            host.AddNote("Command 把所有外部意图收口到一个可观察接缝，日志、测试替身、回放、取消和权限检查都能在这里获得高杠杆，而 View 不必知道逻辑实现。",
                new CodeRef("Assets/Game/Framework/Core/Systems/ICommandSystem.cs", "interface ICommandSystem", "ICommandSystem · 可替换命令分发器接缝"));
            host.AddSubNote("代价是多一个小类型。简单操作让 `readonly struct Command` 直接改 Model 即可；规则复用或多步协调时再委托给 System，不需要为了一行赋值强造厚 System。");

            // ── 异步（带取消）──
            host.AddSectionTitle("异步（带取消）");
            var statusLabel = host.AddValueDisplay();
            statusLabel.text = "状态：空闲";
            var doneLabel = host.AddValueDisplay("", CodeRef.Here("struct GetDoneCommand", "GetDoneCommand"));
            Bag.Subscribe(this.ExecuteCommand(new GetDoneCommand()), v => doneLabel.text = $"已完成：{v} 次");

            CancellationTokenSource cts = null;
            // 切走本章时取消并释放进行中的任务令牌：异步操作的生命周期也跟着 bag 走，
            // 否则切章后任务仍在后台跑完、往已拆除的 UI 标签写文字（无害但脏）。
            Bag.Add(Disposable.Create(() => { cts?.Cancel(); cts?.Dispose(); cts = null; }));
            host.AddAsyncActionRow("开始任务（1.5 秒）", async chapterCt =>
            {
                cts?.Cancel();
                cts?.Dispose();
                var myCts = CancellationTokenSource.CreateLinkedTokenSource(chapterCt);
                cts = myCts;
                statusLabel.text = "状态：进行中…";
                try
                {
                    await this.ExecuteCommandAsync(new RunTaskCommand(), myCts.Token);
                    if (cts == myCts) statusLabel.text = "状态：完成 ✓";
                }
                catch (OperationCanceledException) when (!chapterCt.IsCancellationRequested)
                {
                    if (cts == myCts) statusLabel.text = "状态：已取消";
                }
            }, CodeRef.Here("struct RunTaskCommand", "RunTaskCommand"));
            host.AddActionRow("取消", () => cts?.Cancel(),
                CodeRef.Here("statusLabel.text = \"状态：已取消\"", "取消→接住"));
            host.AddNote("异步命令实现 `IAsyncCommand`，签名带 `CancellationToken`；View 用 `await this.ExecuteCommandAsync(...)`。"
                + "Mono View 无参调用会自动链接 GameObject 销毁 + Context；本 DemoModuleBase 是纯 C# View，没有 GameObject 销毁令牌，"
                + "所以显式传入章节令牌，并在它之上再加主动取消。可取消的显式令牌会选择调用方生命周期、替代 Mono 销毁默认值，但 Context 始终保留。",
                CodeRef.Here("myCts.Token", "自定义令牌"));
            host.AddSubNote("留意 RunTaskCommand 是 `readonly struct`——异步命令默认也用 struct，不是 class："
                + "`readonly struct` 一样能写 `async` 方法，框架对同步/异步走同一套泛型分发、struct 两边都零装箱。"
                + "用 struct 还是 class 只看「要不要 `[Inject]` 字段注入」（struct 注入只会写进装箱副本、不生效），与同步/异步无关——需要注入再用 class。");

            // ── 命令组合（子命令）──
            host.AddSectionTitle("命令组合（子命令）");
            var comboLabel = host.AddValueDisplay();
            comboLabel.text = "组合状态：空闲";
            host.AddAsyncActionRow("连跑两次（异步子命令）", async ct =>
            {
                comboLabel.text = "组合状态：连跑中…";
                await this.ExecuteCommandAsync(new RunTaskTwiceCommand(), ct);
                comboLabel.text = "组合状态：完成 ✓（Done +2）";
            }, CodeRef.Here("struct RunTaskTwiceCommand", "RunTaskTwiceCommand"));
            host.AddNote("命令内经 `ctx` 组合子命令：同步 `ctx.ExecuteCommand(...)`、异步 `await ctx.ExecuteCommandAsync(cmd, cancellationToken)`"
                + "（把本命令的取消令牌透传给子命令，取消随父命令级联）。`RunTaskTwiceCommand` 把 `RunTaskCommand` 作为异步子命令 await 两次，"
                + "上方「已完成」会累加 2。子命令的价值是「能被命令分发器装饰器统一拦截」（日志/回放/事务）；不需要拦截时直接调 System 更直接。",
                CodeRef.Here("struct RunTaskTwiceCommand", "异步子命令组合"));

            // ── 查询（返回值）──
            host.AddSectionTitle("查询（返回值）");
            var snapshotLabel = host.AddValueDisplay();
            snapshotLabel.text = "已完成次数（快照）：点按钮读一次";
            host.AddActionRow("查询一次完成数（快照）", () =>
            {
                var n = this.ExecuteCommand(new CountDoneCommand());
                snapshotLabel.text = $"已完成次数（快照）：{n}";
            }, CodeRef.Here("struct CountDoneCommand", "CountDoneCommand"));
            host.AddNote("查询 Command（`ICommand<T>`）同步返回值：可返回只读状态流（`ReadOnlyReactiveProperty`，给 View 持续订阅——本 demo 到处在用），"
                + "也可返回一次性快照值——这里返回 `int`，读的就是上方异步任务的累计完成数在“查询那一刻”的值（之后再完成任务它不会自己变，要重新点才更新）。"
                + "View 经查询读状态，不直接碰 Model。");
            host.AddSubNote("性能细节：带返回值的 struct 查询走可推断调用 `this.ExecuteCommand(new CountDoneCommand())` 会**装箱一次**——"
                + "`TResult` 只出现在泛型约束里、无法被推断，编译器选中的是接口参数的重载。取一次订阅源这类场景完全无所谓；"
                + "真在热路径高频查询，显式写双泛型 `this.ExecuteCommand<CountDoneCommand, int>(...)` 即零装箱。"
                + "无返回值的 struct Command 走泛型重载、永远零装箱，与此无关。见框架手册 §9。");

            // ── 查询进阶：只读投影（一面板一查询）——模式简单，文字说明即可，完整代码在框架手册 §8 ──
            host.AddSectionTitle("查询进阶：只读投影（一面板一查询）");
            host.AddNote("复杂面板要观察很多状态时，别写 N 个「一字段一查询」——用一个查询 Command 返回打包多个只读源的「投影」对象"
                + "（如 `StatsProjection` 暴露 `Hp` / `Mp` / `Gold` 三个 `ReadOnlyReactiveProperty`），**一面板一查询**。"
                + "投影只暴露只读源，View 看得到、改不了，写仍走 Command，单向数据流约束不变。");
            host.AddSubNote("投影是「读视图」，不是 Model：在查询 Command 里现组装、只引用 Model 已有的只读源，不持有状态、不进容器；"
                + "需要派生 / 过滤 / 组合时投影里直接放 R3 操作符链（如 `model.Hp.Select(...)`）。"
                + "权衡：字段少（一两个）时直接「一字段一查询」更直白，复杂面板才用投影收口。完整代码与权衡见框架手册 §8。");

            // ── 小结 ──
            host.AddSectionTitle("三态小结");
            host.AddConcept("同步 ICommand", "`Execute(ctx)` 立即完成。简单写操作首选（`struct` 零分配）。计数器章已演。");
            host.AddConcept("异步 IAsyncCommand", "`readonly struct` + `ExecuteAsync(ctx, ct)`（struct 也能 async，默认首选）。加载 / 网络 / 动画等耗时操作，令牌可取消。");
            host.AddConcept("查询 ICommand<T>", "`Execute(ctx)` 返回值。读状态：返回只读流持续订阅，或返回一次性快照；读密集面板用只读投影打包多个源（一面板一查询）。");
            host.AddTip("Command 负责统一入口，不代表所有规则都要塞进 Command。下一章会比较一步自增与“校验 + 多步提交”，说明什么时候让 Command 直接改 Model、什么时候把规则交给 System。");
        }
    }

    /// <summary>异步任务的成果：完成次数。</summary>
    public sealed class TaskModel : IModel
    {
        public readonly RP<int> Done = new(0);
    }

    /// <summary>异步命令（readonly struct + IAsyncCommand）：模拟 1.5 秒耗时操作，完成后 Done +1，支持取消。struct 一样能写 async。</summary>
    public readonly struct RunTaskCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            await UniTask.Delay(1500, cancellationToken: cancellationToken);
            ctx.GetModel<TaskModel>().Done.Value++;
        }
    }

    /// <summary>命令组合示例：把 RunTaskCommand 作为异步子命令 await 两次。经 ctx.ExecuteCommandAsync 发起，透传父命令令牌。</summary>
    public readonly struct RunTaskTwiceCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            await ctx.ExecuteCommandAsync(new RunTaskCommand(), cancellationToken);
            await ctx.ExecuteCommandAsync(new RunTaskCommand(), cancellationToken);
        }
    }

    /// <summary>只读查询：完成次数流（给 View 持续订阅）。</summary>
    public readonly struct GetDoneCommand : ICommand<ReadOnlyReactiveProperty<int>>
    {
        public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx) => ctx.GetModel<TaskModel>().Done;
    }

    /// <summary>查询返回一次性快照值（普通 int，非流）。</summary>
    public readonly struct CountDoneCommand : ICommand<int>
    {
        public int Execute(ICommandContext ctx) => ctx.GetModel<TaskModel>().Done.CurrentValue;
    }
}
