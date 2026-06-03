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
        public override string Title => "Command · 三态";
        public override string Category => "核心";
        public override int Order => 20;
        public override string Summary =>
            "Command 三种形态：同步（ICommand，计数器已演）、异步（IAsyncCommand，带取消令牌）、查询（ICommand<T>，返回值）。本章聚焦异步与查询。";

        public override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterValue(new TaskModel(), typeof(TaskModel));

        public override void Build(DemoModuleHost host)
        {
            // ── 异步（带取消）──
            host.AddSectionTitle("异步（带取消）");
            var statusLabel = host.AddValueDisplay();
            statusLabel.text = "状态：空闲";
            var doneLabel = host.AddValueDisplay();
            Bag.Subscribe(this.ExecuteCommand(new GetDoneCommand()), v => doneLabel.text = $"已完成：{v} 次");

            CancellationTokenSource cts = null;
            host.AddActionRow("开始任务（1.5 秒）", async () =>
            {
                cts?.Cancel();
                cts?.Dispose();
                var myCts = new CancellationTokenSource();
                cts = myCts;
                statusLabel.text = "状态：进行中…";
                try
                {
                    await this.ExecuteCommandAsync(new RunTaskCommand(), myCts.Token);
                    if (cts == myCts) statusLabel.text = "状态：完成 ✓";
                }
                catch (OperationCanceledException)
                {
                    if (cts == myCts) statusLabel.text = "状态：已取消";
                }
            }, CodeRef.Here("class RunTaskCommand", "RunTaskCommand"));
            host.AddActionRow("取消", () => cts?.Cancel());
            host.AddNote("异步命令是 class + IAsyncCommand，签名带 CancellationToken；View 用 await this.ExecuteCommandAsync(...)。"
                + "无参重载会自动把 View 销毁 + Context 生命周期令牌链接（任一销毁即取消）；这里另传自定义令牌演示主动取消。");

            // ── 查询（返回值）──
            host.AddSectionTitle("查询（返回值）");
            var snapshotLabel = host.AddValueDisplay();
            snapshotLabel.text = "快照：点下面按钮查询";
            host.AddActionRow("查询一次完成数（快照）", () =>
            {
                var n = this.ExecuteCommand(new CountDoneCommand());
                snapshotLabel.text = $"快照：{n} 次";
            }, CodeRef.Here("struct CountDoneCommand", "CountDoneCommand"));
            host.AddNote("查询 Command（ICommand<T>）同步返回值：可返回只读状态流（ReadOnlyReactiveProperty，给 View 持续订阅——本 demo 到处在用），"
                + "也可返回一次性快照值（这里返回 int）。View 经查询读状态，不直接碰 Model。");

            // ── 小结 ──
            host.AddSectionTitle("三态小结");
            host.AddConcept("同步 ICommand", "Execute(ctx) 立即完成。简单写操作首选（struct 零分配）。计数器章已演。");
            host.AddConcept("异步 IAsyncCommand", "class + ExecuteAsync(ctx, ct)。加载 / 网络 / 动画等耗时操作，令牌可取消。");
            host.AddConcept("查询 ICommand<T>", "Execute(ctx) 返回值。读状态：返回只读流持续订阅，或返回一次性快照。");
        }
    }

    /// <summary>异步任务的成果：完成次数。</summary>
    public sealed class TaskModel : IModel
    {
        public readonly RP<int> Done = new(0);
    }

    /// <summary>异步命令（class + IAsyncCommand）：模拟 1.5 秒耗时操作，完成后 Done +1，支持取消。</summary>
    public sealed class RunTaskCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            await UniTask.Delay(1500, cancellationToken: cancellationToken);
            ctx.GetModel<TaskModel>().Done.Value++;
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
