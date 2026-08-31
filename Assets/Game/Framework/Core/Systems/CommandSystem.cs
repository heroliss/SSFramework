using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Context;

namespace Game.Framework.Systems
{
    /// <summary>
    /// 默认命令分发器。类型名为兼容既有公共 API 保留；它不是五层业务 ISystem。
    /// 无状态 —— 不持有 GameContext 引用，
    /// 每次调用都使用入参 ctx，跨级共享/继承都安全。
    ///
    /// 执行流程：
    /// - class Command：ctx.Inject([Inject] 字段) → Execute(ctx)
    /// - struct Command：JIT 在 typeof(T).IsValueType 处消除 Inject 分支 → Execute(ctx)，零装箱零分配。
    /// 注意 struct Command 不能用 [Inject]（反射写字段只会修改装箱副本），改用 ctx.GetXxx&lt;T&gt;() 直接拿依赖。
    /// 同步（ExecuteCommand）与异步（ExecuteCommandAsync）共用这套泛型分发，struct 在两条路径都零装箱——故异步命令默认也用 readonly struct，class 只为 [Inject] 服务。
    /// 异步 Command 可自行下工作线程做纯计算；无论成功、失败还是取消，dispatcher 都在返回的 UniTask
    /// 完成前切回 Unity 主线程，使调用方能安全继续访问主线程独占的 Context / Event / Model。
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
            return CompleteOnMainThread(command.ExecuteAsync(ctx, cancellationToken));
        }

        public UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, GameContext ctx, CancellationToken cancellationToken)
        {
            ctx.Inject(command);
            return CompleteOnMainThread(command.ExecuteAsync(ctx, cancellationToken));
        }

        public UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, GameContext ctx, CancellationToken cancellationToken) where T : IAsyncCommand<TResult>
        {
            if (!typeof(T).IsValueType) ctx.Inject(command);
            return CompleteOnMainThread(command.ExecuteAsync(ctx, cancellationToken));
        }

        // finally 保留 Command 的原始异常 / 取消身份与堆栈，同时确保所有终态都先回主线程再交付调用方。
        private static async UniTask CompleteOnMainThread(UniTask task)
        {
            try { await task; }
            finally { await UniTask.SwitchToMainThread(); }
        }

        private static async UniTask<TResult> CompleteOnMainThread<TResult>(UniTask<TResult> task)
        {
            try { return await task; }
            finally { await UniTask.SwitchToMainThread(); }
        }
    }
}
