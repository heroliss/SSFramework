using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Context;
using Game.Framework.Event;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.Utility;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 游戏上下文能力接口。
    ///
    /// GameContext 直接实现此接口；MonoGameContextBase 作为场景中的 Mono 代理也实现此接口并转发到内部 GameContext。
    /// 因此业务对象可以统一持有 IGameContext，而不必关心它来自纯 C# 还是 Mono 场景节点。
    /// </summary>
    /// <remarks>
    /// <b>权限边界：</b>本接口故意<b>不暴露 <see cref="Container"/></b>。业务通过 <c>RegisterModel/System/Utility</c>
    /// 这几条受控通道注册依赖，避免直接 <c>ctx.Container.RegisterFor&lt;TLayer&gt;</c> 绕过层标记。
    /// 框架内部需要 Container 的位置通过 <see cref="ContextInternals.GetContainer"/> 访问（仅程序集内可见）。
    /// </remarks>
    public interface IGameContext : IEventBus
    {
        bool IsDisposed { get; }
        CancellationToken CancellationToken { get; }

        void Inject(object obj);
        bool TryResolve(Type type, out object instance);
        object Resolve(Type type);

        /// <summary>
        /// 创建跟随当前 Context 的新 <see cref="DisposableBag"/>。
        /// Command 等没有 MonoBase 自带 bag 的场景用 <c>using var bag = ctx.CreateBag()</c> 做临时生命周期管理。
        /// Bag 本身不会被注册到 Context，业务自己负责 Dispose（using 块或显式调用）。
        /// </summary>
        DisposableBag CreateBag();

        T GetModel<T>() where T : class, IModel;
        T GetSystem<T>() where T : class, ISystem;
        T GetUtility<T>() where T : class, IUtility;

        void RegisterModel<T>(T instance) where T : class, IModel;
        void RegisterSystem<T>(T instance) where T : class, ISystem;
        void RegisterUtility<T>(T instance) where T : class, IUtility;

        void UnregisterModel<T>(T instance) where T : class, IModel;
        void UnregisterSystem<T>(T instance) where T : class, ISystem;
        void UnregisterUtility<T>(T instance) where T : class, IUtility;

        void ExecuteCommand<T>(T command) where T : ICommand;
        TResult ExecuteCommand<TResult>(ICommand<TResult> command);
        TResult ExecuteCommand<T, TResult>(T command) where T : ICommand<TResult>;

        UniTask ExecuteCommandAsync<T>(T command) where T : IAsyncCommand;
        UniTask ExecuteCommandAsync<T>(T command, CancellationToken cancellationToken) where T : IAsyncCommand;
        UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command);
        UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, CancellationToken cancellationToken);
        UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command) where T : IAsyncCommand<TResult>;
        UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, CancellationToken cancellationToken) where T : IAsyncCommand<TResult>;
    }
}
