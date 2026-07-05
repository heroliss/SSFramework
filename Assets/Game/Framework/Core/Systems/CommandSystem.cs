using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Context;

namespace Game.Framework.Systems
{
    /// <summary>
    /// 默认命令处理系统。无状态 —— 不持有 GameContext 引用，
    /// 每次调用都使用入参 ctx，跨级共享/继承都安全。
    ///
    /// 执行流程：
    /// - class Command：ctx.Inject([Inject] 字段) → Execute(ctx)
    /// - struct Command：JIT 在 typeof(T).IsValueType 处消除 Inject 分支 → Execute(ctx)，零装箱零分配。
    /// 注意 struct Command 不能用 [Inject]（反射写字段只会修改装箱副本），改用 ctx.GetXxx&lt;T&gt;() 直接拿依赖。
    /// 同步（ExecuteCommand）与异步（ExecuteCommandAsync）共用这套泛型分发，struct 在两条路径都零装箱——故异步命令默认也用 readonly struct，class 只为 [Inject] 服务。
    /// </summary>
    public sealed class CommandSystem : ICommandSystem
    {
        public void ExecuteCommand<T>(T command, GameContext ctx) where T : ICommand
        {
            if (!typeof(T).IsValueType) ctx.Inject(command);
            command.Execute(ctx);
        }

        public TResult ExecuteCommand<TResult>(ICommand<TResult> command, GameContext ctx)
        {
            ctx.Inject(command);
            return command.Execute(ctx);
        }

        public TResult ExecuteCommand<T, TResult>(T command, GameContext ctx) where T : ICommand<TResult>
        {
            if (!typeof(T).IsValueType) ctx.Inject(command);
            return command.Execute(ctx);
        }

        public UniTask ExecuteCommandAsync<T>(T command, GameContext ctx, CancellationToken cancellationToken) where T : IAsyncCommand
        {
            if (!typeof(T).IsValueType) ctx.Inject(command);
            return command.ExecuteAsync(ctx, cancellationToken);
        }

        public UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, GameContext ctx, CancellationToken cancellationToken)
        {
            ctx.Inject(command);
            return command.ExecuteAsync(ctx, cancellationToken);
        }

        public UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, GameContext ctx, CancellationToken cancellationToken) where T : IAsyncCommand<TResult>
        {
            if (!typeof(T).IsValueType) ctx.Inject(command);
            return command.ExecuteAsync(ctx, cancellationToken);
        }
    }
}
