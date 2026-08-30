using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Game.Framework.Command;
using Game.Framework.Diagnostics;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.Model;
using Game.Framework.Systems;
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

        /// <summary>
        /// 创建 GameContext。inheritFromGlobal 控制本容器未命中时是否回退到 GameContext.Main。
        /// </summary>
        /// <remarks>
        /// 构造时对容器里<b>构建期值绑定</b>（RegisterValue / RegisterOwned）的实例统一 <see cref="Inject"/> +
        /// <see cref="AttachTo"/>，与 Mono 路径「注册即注入」语义对称（ADR-0019）——纯 C# 服务在 InstallBindings
        /// 注册后不再需要手动补注入。此刻全部绑定已入容器、父链可解析；<c>[Inject]</c> 解析失败 / 越权在启动期
        /// 即以 LogWarning / LogError 暴露（与 Mono 路径同一套 InjectionPlan 语义）。
        /// 工厂产物不自动注入——工厂经 <c>Func&lt;Container, object&gt;</c> 显式接线。
        /// </remarks>
        public GameContext(Container container, bool inheritFromGlobal = true)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _inheritFromGlobal = inheritFromGlobal;

            try
            {
                var boundValues = container.BoundValues;
                if (boundValues != null)
                    for (int i = 0; i < boundValues.Count; i++)
                    {
                        Inject(boundValues[i]);
                        AttachTo(boundValues[i]);
                    }

#if UNITY_EDITOR
                CreatedRealtime = Time.realtimeSinceStartupAsDouble;
#endif
                FrameworkDiagnostics.OnContextCreated(this); // Editor 外编译消除
            }
            catch
            {
                // 构造函数没有成功返回，调用方拿不到 GameContext 来 Dispose；从接收 Container 起这里就是
                // 所有权事务的最后一道边界，注入 / Attach / 诊断初始化失败都必须主动回滚。
                _disposed = true;
                _container.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 诊断显示名（诊断面板 / 日志用，业务逻辑不得依赖）。框架创建点自动命名：
        /// <see cref="MonoGameContextBase"/> 用 GameObject 名、GameFlow 用状态类型名；未命名显示为匿名 Context。
        /// </summary>
        public string DebugName { get; set; }

#if UNITY_EDITOR
        // ---- 诊断数据面（Editor 专用，诊断面板经 InternalsVisibleTo 读取；ADR-0026） ----

        /// <summary>构造时刻（realtimeSinceStartup），诊断面板显示存活时长。</summary>
        internal double CreatedRealtime { get; }

        /// <summary>本 Context 解析未命中时是否回退 <see cref="Main"/>（构造参数快照），诊断面板标记用。</summary>
        internal bool InheritsFromGlobal => _inheritFromGlobal;

        // 本 Context 各事件类型的存活订阅数（订阅 +1、退订 -1）。惰性分配：不订阅事件的 Context 零成本。
        private Dictionary<Type, int> _eventSubscriptionCounts;

        /// <summary>各事件类型的存活订阅计数；从未有过订阅时为 null。订阅数只增不减 = 泄漏嫌疑。</summary>
        internal IReadOnlyDictionary<Type, int> EventSubscriptionCounts => _eventSubscriptionCounts;
#endif

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

        /// <inheritdoc />
        public void Inject(object obj)
        {
            ThrowIfDisposed();
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            InjectionPlan.For(obj.GetType()).Apply(obj, this);
        }

        /// <summary>
        /// 创建跟随当前 Context 的临时 bag。
        /// Bag 持有 ctx 引用以支持资源加载和 Framework Event 订阅；不会被注册到容器，调用方负责 Dispose。
        /// </summary>
        public DisposableBag CreateBag()
        {
            ThrowIfDisposed();
            return new DisposableBag(this);
        }

        /// <summary>尝试解析类型。查找顺序：本容器（含父级链）→ GameContext.Main（可选）。</summary>
        /// <remarks>
        /// Editor 诊断只记录成功离开本 Container 的实际回退；允许 Main、失败探测与只读诊断不会记账。
        /// 计数表达 Resolve 次数，不等于业务使用次数。
        /// </remarks>
        public bool TryResolve(Type type, out object instance)
        {
            ThrowIfDisposed();
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (_container.TryResolve(type, out instance)) return true;
            if (_inheritFromGlobal && Main != null && Main != this)
#if UNITY_EDITOR
            {
                Main.ThrowIfDisposed();
                if (Main._container.TryResolveWithSource(type, out instance, out var source))
                {
                    _container.RecordFallback(type, source, ContainerFallbackKind.Main);
                    return true;
                }
                return false;
            }
#else
                return Main.TryResolve(type, out instance);
#endif
            instance = null;
            return false;
        }

        /// <summary>解析类型。未找到时抛 InvalidOperationException。</summary>
        public object Resolve(Type type)
        {
            if (TryResolve(type, out var instance)) return instance;
            throw new InvalidOperationException($"[GameContext] 未注册类型 '{type.Name}'。");
        }

        // ---- 层访问 ----

        public T GetModel<T>() where T : class, IModel => (T)Resolve(typeof(T));
        public T GetSystem<T>() where T : class, ISystem => (T)Resolve(typeof(T));
        public T GetUtility<T>() where T : class, IUtility => (T)Resolve(typeof(T));

        // ---- 动态注册（公开 API） ----

        public void RegisterModel<T>(T instance) where T : class, IModel
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _container.RegisterFor<IModel>(instance, $"dynamic:{instance.GetType().Name}");
        }

        public void RegisterSystem<T>(T instance) where T : class, ISystem
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _container.RegisterFor<ISystem>(instance, $"dynamic:{instance.GetType().Name}");
        }

        public void RegisterUtility<T>(T instance) where T : class, IUtility
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _container.RegisterFor<IUtility>(instance, $"dynamic:{instance.GetType().Name}");
        }

        public void UnregisterModel<T>(T instance) where T : class, IModel
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _container.UnregisterFor<IModel>(instance);
        }

        public void UnregisterSystem<T>(T instance) where T : class, ISystem
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _container.UnregisterFor<ISystem>(instance);
        }

        public void UnregisterUtility<T>(T instance) where T : class, IUtility
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _container.UnregisterFor<IUtility>(instance);
        }

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
                Log.Warning($"已释放的 Context 收到 SendEvent<{typeof(T).Name}> 调用，已忽略。", "GameContext");
#endif
                return;
            }
            if (_typedEvents.TryGetValue(typeof(T), out var subject))
                ((Subject<T>)subject).OnNext(evt);
        }

        public IDisposable RegisterEvent<T>(Action<T> handler) where T : IEvent
        {
            ThrowIfDisposed();
            if (handler == null) throw new ArgumentNullException(nameof(handler));
#if UNITY_EDITOR
            // 诊断计数包装（仅 Editor）：R3 Subject 不暴露 observer 数，框架在唯一订阅通道上自己数。
            // Bag.Subscribe<TEvent> 也走本方法，覆盖全部 Framework Event 订阅。
            return new CountedEventSubscription(this, typeof(T), GetOrCreateSubject<T>().Subscribe(handler));
#else
            return GetOrCreateSubject<T>().Subscribe(handler);
#endif
        }

#if UNITY_EDITOR
        // 订阅计数的退订侧：Dispose 幂等（只减一次），持有 Context 引用在 Context Dispose 后不减（计数字典已随之作废）。
        private sealed class CountedEventSubscription : IDisposable
        {
            private readonly GameContext _owner;
            private readonly Type _eventType;
            private IDisposable _inner;

            public CountedEventSubscription(GameContext owner, Type eventType, IDisposable inner)
            {
                _owner = owner;
                _eventType = eventType;
                _inner = inner;
                var counts = _owner._eventSubscriptionCounts ??= new Dictionary<Type, int>();
                counts.TryGetValue(eventType, out int n);
                counts[eventType] = n + 1;
            }

            public void Dispose()
            {
                var inner = _inner;
                if (inner == null) return;
                _inner = null;
                inner.Dispose();
                var counts = _owner._eventSubscriptionCounts;
                if (counts != null && counts.TryGetValue(_eventType, out int n) && n > 0)
                    counts[_eventType] = n - 1;
            }
        }
#endif

        // ---- 上下文绑定 ----

        public void AttachTo(object target)
        {
            ThrowIfDisposed();
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target is IHasGameContext hasCtx && hasCtx.Context == null)
                SetContextField(target);
        }

        // ---- IDisposable ----

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            FrameworkDiagnostics.OnContextDisposed(this); // Editor 外编译消除

            var cts = _cts;
            _cts = null;
            if (cts != null)
            {
                try
                {
                    // CancellationTokenSource 会聚合并重新抛出用户回调异常；不能让一个坏回调阻断整棵 Context 清理。
                    cts.Cancel();
                }
                catch (Exception e)
                {
                    Log.Error(
                        "Context 释放期间有取消回调抛出异常；事件与托管服务仍会继续释放。",
                        e,
                        "GameContext");
                }
                finally
                {
                    cts.Dispose();
                }
            }
            _commandSystem = null;
            foreach (var subject in _typedEvents.Values)
                ((IDisposable)subject).Dispose();
            _typedEvents.Clear();
#if UNITY_EDITOR
            _eventSubscriptionCounts = null; // 订阅计数随事件总线一起作废（迟到的退订不再减）
#endif
            _container.Dispose(); // 释放 RegisterOwned / RegisterOwnedFactory 接管的实例（如 PoolUtility）
        }

        // ---- 内部 ----

        internal static IGameContext ResolveFrom(object self)
        {
            if (self is IHasGameContext hasCtx && hasCtx.Context != null)
                return hasCtx.Context;
            throw new InvalidOperationException(
                $"[GameContext] 类型 '{self.GetType().Name}' 没有关联 Context。" +
                "请继承框架 *Base 基类、实现 IHasGameContext，或使用 Execute() 传入的 GameContext 参数。");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GameContext),
                    "[GameContext] Context 已释放，不能再解析、注入、订阅或修改注册项。");
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
                    Log.Warning(
                        $"AttachTo 在类型 '{type.Name}' 上找不到 'GameContext' 字段。" +
                        "请声明由 IHasGameContext.Context 读取的私有 GameContext 字段。",
                        "GameContext");
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
