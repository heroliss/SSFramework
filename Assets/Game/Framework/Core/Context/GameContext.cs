using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Game.Framework.Command;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Model;
using Game.Framework.System;
using Game.Framework.Utility;
using Cysharp.Threading.Tasks;
using R3;
using UnityEngine;

namespace Game.Framework.Context
{
    /// <summary>
    /// 事件总线接口：按类型独立 Subject 分发，消除广播过滤开销。
    /// </summary>
    public interface IEventBus
    {
        void SendEvent<T>(T evt = default) where T : IEvent;
        IDisposable RegisterEvent<T>(Action<T> handler) where T : IEvent;
    }

    /// <summary>
    /// 游戏上下文：封装 DI 容器、事件总线与命令系统，所有层访问的统一入口。
    /// 事件总线使用按类型独立的 R3 Subject，发送时直接投递到对应类型，
    /// 消除了单 Subject 广播 + 订阅端类型过滤的 O(N) 开销。
    /// 每个 GameContext 独立、可嵌套、可释放。
    /// </summary>
    public sealed class GameContext : IDisposable, IEventBus, IGameContext, ICommandContext
    {
        private readonly Container _container;
        private readonly bool _inheritFromGlobal;
        private readonly Dictionary<Type, object> _typedEvents = new();
        private ICommandSystem _commandSystem;
        private bool _disposed;
        private CancellationTokenSource _cts;

        private static GameContext _main;

        /// <summary>
        /// 全局静态主上下文引用。由 <see cref="MonoGlobalContext"/> 自动设置，业务代码不应手工赋值。
        /// 适用于无法获得上下文引用的深层代码（如纯 C# 工具类），但应优先使用实例上下文。
        /// </summary>
        /// <remarks>
        /// setter 是 <c>internal</c>，仅框架程序集内可调用（<see cref="MonoGlobalContext"/> 在 Awake 写入）。
        /// 业务不再能 <c>GameContext.Main = ...</c>。
        /// </remarks>
        public static GameContext Main
        {
            get => _main;
            internal set => _main = value;
        }

        /// <summary>创建 GameContext。inheritFromGlobal 控制本容器未命中时是否回退到 GameContext.Main。</summary>
        public GameContext(Container container, bool inheritFromGlobal = true)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _inheritFromGlobal = inheritFromGlobal;
        }

        /// <summary>此 Context 是否已被 Dispose。</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// 与此 Context 生命周期绑定的取消令牌。Context.Dispose() 时自动取消。
        /// 异步命令可通过 ctx.CancellationToken 感知 Context 销毁，安全提前退出。
        /// 懒初始化：不使用 CT 的 Context 不分配 CancellationTokenSource。
        /// </summary>
        public CancellationToken CancellationToken
        {
            get
            {
                if (_disposed) return new CancellationToken(canceled: true);
                _cts ??= new CancellationTokenSource();
                return _cts.Token;
            }
        }

        // ---- 容器 / 注入 ----

        /// <summary>
        /// 底层 DI 容器。<b>仅框架程序集内可访问</b>（业务侧 <see cref="IGameContext"/> 接口不暴露 Container，
        /// 避免绕过层标记直接 RegisterFor）。业务用 <see cref="RegisterModel{T}"/> 等受控通道。
        /// </summary>
        internal Container Container => _container;

        public void Inject(object obj) => InjectionPlan.For(obj.GetType()).Apply(obj, this);

        /// <summary>
        /// 创建跟随当前 Context 的临时 bag。
        /// Bag 持有 ctx 引用以支持资源加载和 Framework Event 订阅；不会被注册到容器，调用方负责 Dispose。
        /// </summary>
        public DisposableBag CreateBag() => new DisposableBag(this);

        /// <summary>尝试解析类型。查找顺序：本容器（含父级链）→ GameContext.Main（可选）。</summary>
        public bool TryResolve(Type type, out object instance)
        {
            if (_container.TryResolve(type, out instance)) return true;
            if (_inheritFromGlobal && Main != null && Main != this)
                return Main.TryResolve(type, out instance);
            instance = null;
            return false;
        }

        /// <summary>解析类型。未找到时抛 InvalidOperationException。</summary>
        public object Resolve(Type type)
        {
            if (TryResolve(type, out var instance)) return instance;
            throw new InvalidOperationException($"[GameContext] {type.Name} not registered");
        }

        // ---- 层访问 ----

        public T GetModel<T>() where T : class, IModel => (T)Resolve(typeof(T));
        public T GetSystem<T>() where T : class, ISystem => (T)Resolve(typeof(T));
        public T GetUtility<T>() where T : class, IUtility => (T)Resolve(typeof(T));

        // ---- 动态注册（公开 API） ----

        public void RegisterModel<T>(T instance) where T : class, IModel
            => _container.RegisterFor<IModel>(instance, $"dynamic:{instance.GetType().Name}");

        public void RegisterSystem<T>(T instance) where T : class, ISystem
            => _container.RegisterFor<ISystem>(instance, $"dynamic:{instance.GetType().Name}");

        public void RegisterUtility<T>(T instance) where T : class, IUtility
            => _container.RegisterFor<IUtility>(instance, $"dynamic:{instance.GetType().Name}");

        public void UnregisterModel<T>(T instance) where T : class, IModel
            => _container.UnregisterFor<IModel>(instance);

        public void UnregisterSystem<T>(T instance) where T : class, ISystem
            => _container.UnregisterFor<ISystem>(instance);

        public void UnregisterUtility<T>(T instance) where T : class, IUtility
            => _container.UnregisterFor<IUtility>(instance);

        // ---- Command 执行 ----

        public void ExecuteCommand<T>(T command) where T : ICommand
        {
            ThrowIfDisposed();
            ResolveCommandSystem().ExecuteCommand(command, this);
        }

        public TResult ExecuteCommand<TResult>(ICommand<TResult> command)
        {
            ThrowIfDisposed();
            return ResolveCommandSystem().ExecuteCommand(command, this);
        }

        public TResult ExecuteCommand<T, TResult>(T command) where T : ICommand<TResult>
        {
            ThrowIfDisposed();
            return ResolveCommandSystem().ExecuteCommand<T, TResult>(command, this);
        }

        public UniTask ExecuteCommandAsync<T>(T command) where T : IAsyncCommand
        {
            ThrowIfDisposed();
            return ResolveCommandSystem().ExecuteCommandAsync(command, this, CancellationToken);
        }

        public UniTask ExecuteCommandAsync<T>(T command, CancellationToken cancellationToken) where T : IAsyncCommand
        {
            ThrowIfDisposed();
            return ResolveCommandSystem().ExecuteCommandAsync(command, this, cancellationToken);
        }

        public UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command)
        {
            ThrowIfDisposed();
            return ResolveCommandSystem().ExecuteCommandAsync(command, this, CancellationToken);
        }

        public UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return ResolveCommandSystem().ExecuteCommandAsync(command, this, cancellationToken);
        }

        public UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command) where T : IAsyncCommand<TResult>
        {
            ThrowIfDisposed();
            return ResolveCommandSystem().ExecuteCommandAsync<T, TResult>(command, this, CancellationToken);
        }

        public UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, CancellationToken cancellationToken) where T : IAsyncCommand<TResult>
        {
            ThrowIfDisposed();
            return ResolveCommandSystem().ExecuteCommandAsync<T, TResult>(command, this, cancellationToken);
        }

        // ---- 事件（按类型独立 Subject，无广播过滤） ----

        public void SendEvent<T>(T evt = default) where T : IEvent
        {
            if (_disposed)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning($"[GameContext] SendEvent<{typeof(T).Name}> called on disposed context. Ignoring.");
#endif
                return;
            }
            if (_typedEvents.TryGetValue(typeof(T), out var subject))
                ((Subject<T>)subject).OnNext(evt);
        }

        public IDisposable RegisterEvent<T>(Action<T> handler) where T : IEvent
            => GetOrCreateSubject<T>().Subscribe(handler);

        // ---- 上下文绑定 ----

        public void AttachTo(object target)
        {
            if (target is IHasGameContext hasCtx && hasCtx.Context == null)
                SetContextField(target);
        }

        // ---- IDisposable ----

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _commandSystem = null;
            foreach (var subject in _typedEvents.Values)
                ((IDisposable)subject).Dispose();
            _typedEvents.Clear();
            _container.Dispose(); // 释放本 Context 拥有的实例（RegisterOwned 登记的，如 PoolUtility）
        }

        // ---- 内部 ----

        internal static IGameContext ResolveFrom(object self)
        {
            if (self is IHasGameContext hasCtx && hasCtx.Context != null)
                return hasCtx.Context;
            throw new InvalidOperationException(
                $"[GameContext] No context on '{self.GetType().Name}'. " +
                "Inherit a *Base class, implement IHasGameContext, or use the GameContext parameter passed to Execute().");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GameContext), "[GameContext] Cannot execute commands on a disposed context.");
        }

        private Subject<T> GetOrCreateSubject<T>() where T : IEvent
        {
            if (_typedEvents.TryGetValue(typeof(T), out var subject))
                return (Subject<T>)subject;
            var s = new Subject<T>();
            _typedEvents[typeof(T)] = s;
            return s;
        }

        private ICommandSystem ResolveCommandSystem()
        {
            if (_commandSystem != null) return _commandSystem;
            _commandSystem = (ICommandSystem)Resolve(typeof(ICommandSystem));
            return _commandSystem;
        }

        // ---- AttachTo 反射字段缓存 ----

        // 静态缓存不加锁：与 Container 同一「主线程独占」契约，Editor / Dev 下由 MainThreadGuard 兜底。
        private static readonly Dictionary<Type, FieldInfo> _contextFieldCache = new();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void ClearContextFieldCacheOnDomainReload() => _contextFieldCache.Clear();
#endif

        private void SetContextField(object target)
        {
            var type = target.GetType();
            if (!_contextFieldCache.TryGetValue(type, out var field))
            {
                MainThreadGuard.AssertMainThread(nameof(GameContext));
                field = FindContextField(type);
                _contextFieldCache[type] = field;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (field == null)
                {
                    Debug.LogWarning(
                        $"[GameContext] AttachTo: no 'GameContext' field found on '{type.Name}'. " +
                        "Ensure the class declares a private GameContext field (IHasGameContext.Context reads it).");
                }
#endif
            }
            field?.SetValue(target, this);
        }

        private static FieldInfo FindContextField(Type type)
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly;

            var t = type;
            while (t != null && t != typeof(object))
            {
                foreach (var field in t.GetFields(flags))
                    if (field.FieldType == typeof(GameContext))
                        return field;
                t = t.BaseType;
            }
            return null;
        }
    }
}
