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
    /// 游戏上下文能力 Interface。
    ///
    /// GameContext 直接实现此接口；MonoGameContextBase 作为场景中的 Mono 代理也实现此接口并转发到内部 GameContext。
    /// 因此业务对象可以统一持有 IGameContext，而不必关心它来自纯 C# 还是 Mono 场景节点。
    /// </summary>
    /// <remarks>
    /// <b>权限边界：</b>本接口故意<b>不暴露 <see cref="Container"/></b>。业务通过 <c>RegisterModel/System/Utility</c>
    /// 这几条受控通道注册依赖，避免直接 <c>ctx.Container.RegisterFor&lt;TLayer&gt;</c> 绕过层标记。
    /// 框架内部需要 Container 的位置通过 <see cref="ContextInternals.GetContainer"/> 访问（仅程序集内可见）。
    /// <br/><b>线程：</b>Context 能力统一在 Unity 主线程访问，Implementation 不加锁。已在主线程取得的
    /// <see cref="CancellationToken"/> 可以传给后台任务观察取消，但后台任务不应回调本 Interface。
    /// <br/><b>释放：</b>Context 结束后，解析、注入、动态注册、Command 与新订阅都会抛
    /// <see cref="ObjectDisposedException"/>；迟到的 <see cref="IEventBus.SendEvent{T}(T)"/> 是唯一刻意保留的
    /// 幂等 no-op。生命周期 token 的取消回调失败不会阻断事件流和 owned 实例继续释放。
    /// </remarks>
    public interface IGameContext : IEventBus
    {
        /// <summary>Context 是否已经结束；<c>true</c> 后不能再解析、注册、注入、执行 Command 或创建订阅。</summary>
        bool IsDisposed { get; }

        /// <summary>
        /// 由 Context 拥有的生命周期令牌；<c>Dispose</c> 时取消。释放后首次读取也会返回已取消令牌，
        /// 不会为已结束 Context 新建 <see cref="CancellationTokenSource"/>。
        /// </summary>
        CancellationToken CancellationToken { get; }

        /// <summary>
        /// 对现有对象执行 <c>[Inject]</c> 字段/方法注入。只写依赖，不注册、不绑定 Context，也不接管
        /// <paramref name="obj"/> 的生命周期；需要扩展方法能力的纯 C# 对象再显式调用
        /// <see cref="AttachTo"/>。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="obj"/> 为 <c>null</c>。</exception>
        /// <exception cref="ObjectDisposedException">Context 已释放。</exception>
        void Inject(object obj);

        /// <summary>
        /// 把底层 <see cref="GameContext"/> 写入实现了 <see cref="IHasGameContext"/> 且尚未绑定的纯 C# 对象，
        /// 使其可以使用 <c>this.GetXxx/SendEvent</c> 等扩展入口。只绑定 Context 引用，不执行 Inject、
        /// 不注册对象、也不转移所有权；已有非空 Context 时保持原绑定。
        /// </summary>
        /// <param name="target">声明了由 <see cref="IHasGameContext.Context"/> 读取的私有 <see cref="GameContext"/> 字段的对象。</param>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
        /// <exception cref="ObjectDisposedException">Context 已释放。</exception>
        void AttachTo(object target);

        /// <summary>
        /// 尝试按类型解析：当前 Container 的运行时覆盖 → 构建期绑定 → 父链 → 可选 Main 回退。
        /// 未命中返回 <c>false</c> 并把 <paramref name="instance"/> 置为 <c>null</c>；Context 已释放仍抛异常，
        /// 不能把生命周期错误伪装成“未注册”。
        /// </summary>
        bool TryResolve(Type type, out object instance);

        /// <summary>按 <paramref name="type"/> 解析；未注册时抛 <see cref="InvalidOperationException"/>。</summary>
        object Resolve(Type type);

        /// <summary>
        /// 创建跟随当前 Context 的新 <see cref="DisposableBag"/>。
        /// Command 等没有 MonoBase 自带 bag 的场景用 <c>using var bag = ctx.CreateBag()</c> 做临时生命周期管理。
        /// Bag 本身不会被注册到 Context，业务自己负责 Dispose（using 块或显式调用）。
        /// </summary>
        DisposableBag CreateBag();

        /// <summary>解析一个 Model 契约；未注册或 Context 已释放时抛异常。</summary>
        T GetModel<T>() where T : class, IModel;
        /// <summary>解析一个 System 契约；未注册或 Context 已释放时抛异常。</summary>
        T GetSystem<T>() where T : class, ISystem;
        /// <summary>解析一个 Utility 契约；未注册或 Context 已释放时抛异常。</summary>
        T GetUtility<T>() where T : class, IUtility;

        /// <summary>
        /// 在当前 Context 的运行时覆盖层登记 Model 的具体类型与其 Model Interface。
        /// 不执行 Inject / AttachTo，也不转移所有权；重复契约 fail-fast。
        /// </summary>
        void RegisterModel<T>(T instance) where T : class, IModel;
        /// <summary>运行时登记 System；注入、Context 绑定和释放仍由调用方负责，语义同 <see cref="RegisterModel{T}(T)"/>。</summary>
        void RegisterSystem<T>(T instance) where T : class, ISystem;
        /// <summary>运行时登记 Utility；注入、Context 绑定和释放仍由调用方负责，语义同 <see cref="RegisterModel{T}(T)"/>。</summary>
        void RegisterUtility<T>(T instance) where T : class, IUtility;

        /// <summary>
        /// 仅当当前运行时 Model 覆盖与 <paramref name="instance"/> 是同一实例时移除登记。
        /// 不调用 <see cref="IDisposable.Dispose"/>，也不重定向既有注入引用或订阅。
        /// </summary>
        void UnregisterModel<T>(T instance) where T : class, IModel;
        /// <summary>移除同一 System 实例的运行时登记，不释放实例；语义同 <see cref="UnregisterModel{T}(T)"/>。</summary>
        void UnregisterSystem<T>(T instance) where T : class, ISystem;
        /// <summary>移除同一 Utility 实例的运行时登记，不释放实例；语义同 <see cref="UnregisterModel{T}(T)"/>。</summary>
        void UnregisterUtility<T>(T instance) where T : class, IUtility;

        /// <summary>通过当前 Command dispatcher 同步执行命令；命令异常原样传播。</summary>
        void ExecuteCommand<T>(T command) where T : ICommand;
        /// <summary>同步执行带返回值命令；命令异常原样传播。</summary>
        TResult ExecuteCommand<TResult>(ICommand<TResult> command);
        /// <summary>以双泛型形式同步执行带返回值命令，避免 struct 命令装箱；异常原样传播。</summary>
        TResult ExecuteCommand<T, TResult>(T command) where T : ICommand<TResult>;

        /// <summary>用 Context 生命周期令牌执行异步命令；取消与异常由返回的 task 原样传播。</summary>
        UniTask ExecuteCommandAsync<T>(T command) where T : IAsyncCommand;
        /// <summary>
        /// 用调用方提供的令牌执行异步命令。该重载不会再自动与 Context 令牌合并；需要同时受两者约束时，
        /// 调用方应传入链接令牌或从 View 扩展入口执行。
        /// </summary>
        UniTask ExecuteCommandAsync<T>(T command, CancellationToken cancellationToken) where T : IAsyncCommand;
        /// <summary>用 Context 生命周期令牌执行带返回值异步命令。</summary>
        UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command);
        /// <summary>用调用方令牌执行带返回值异步命令；令牌不会自动与 Context 令牌合并。</summary>
        UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, CancellationToken cancellationToken);
        /// <summary>以双泛型形式用 Context 生命周期令牌执行带返回值异步命令。</summary>
        UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command) where T : IAsyncCommand<TResult>;
        /// <summary>以双泛型形式用调用方令牌执行带返回值异步命令；令牌不会自动与 Context 令牌合并。</summary>
        UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, CancellationToken cancellationToken) where T : IAsyncCommand<TResult>;
    }
}
