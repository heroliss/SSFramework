using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.Utility;
using UnityEngine;

namespace Game.Framework.Context
{
#if UNITY_EDITOR
    /// <summary>
    /// Mono Context 初始化事务的 Editor 诊断状态。它不是业务生命周期 API；只供框架 Editor/Test
    /// 白盒读取，避免诊断工具通过 <see cref="IGameContext.IsDisposed"/> 猜测失败与销毁的区别。
    /// </summary>
    internal enum MonoContextDiagnosticState
    {
        Uninitialized,
        Initializing,
        Ready,
        Failed,
        Disposed,
    }

    /// <summary>
    /// <see cref="MonoGameContextBase"/> 初始化事务的只读快照。失败宿主没有可登记的
    /// <see cref="GameContext"/>，因此由 Editor Adapter 直接读取宿主真实状态，而不污染
    /// <c>FrameworkDiagnostics.LiveContexts</c> 的“已构造且未释放”语义。
    /// </summary>
    internal readonly struct MonoContextDiagnosticSnapshot
    {
        internal readonly MonoContextDiagnosticState State;
        internal readonly IGameContext ResolvedParent;
        internal readonly GameContext Context;
        internal readonly Exception Failure;

        internal MonoContextDiagnosticSnapshot(
            MonoContextDiagnosticState state,
            IGameContext resolvedParent,
            GameContext context,
            Exception failure)
        {
            State = state;
            ResolvedParent = resolvedParent;
            Context = context;
            Failure = failure;
        }
    }
#endif

    /// <summary>
    /// 层级式游戏上下文基类。挂在 GameObject 上，作为场景里的 <see cref="IGameContext"/> 代理；
    /// 子节点上的 <c>MonoModelBase / MonoSystemBase / MonoUtilityBase</c> Awake 时会自动注册到本 Context。
    /// </summary>
    /// <remarks>
    /// <b>谁该用：</b>所有"作用域 = 一段游戏过程"的 Context 节点——大厅、关卡、Boss 战、UI 子模块等。
    /// 项目级唯一根 Context 用 <see cref="MonoGlobalContext"/>（自动管理 <see cref="GameContext.Main"/>）。<br/>
    /// <b>层级关系：</b>Inspector 留空<c>父级上下文（Parent Context）</c>并开启<c>自动查找父级上下文</c>时，
    /// 沿 Transform 父链找最近的 <see cref="MonoGameContextBase"/> 作为父级；
    /// 显式赋值则跳过自动查找。子级解析未命中时自动回退父级。<br/>
    /// <b>执行顺序：</b><c>DefaultExecutionOrder(-1000)</c>。子 Context 与父 Context 同序，
    /// 框架在 <see cref="Awake"/> 内递归确保父级先初始化，业务无需关心顺序。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item>派生类只重写 <see cref="InstallBindings"/> 注册纯 C# 服务，<b>不要</b>重写 Awake/OnDestroy（如必须，先调 <c>base</c>）。</item>
    ///   <item>同一 GameObject 上只能挂一个（<see cref="DisallowMultipleComponentAttribute"/>）。</item>
    ///   <item>事件总线按 Context 独立——子 Context 发出的事件不会泄漏到父级。</item>
    /// </list>
    /// </remarks>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public class MonoGameContextBase : MonoBehaviour, IGameContext, IHasGameContext
    {
        [SerializeField]
        [LockInPlayMode]
        [InspectorName("父级上下文（Parent Context）")]
        [Tooltip("显式指定父级场景 Context。留空且开启“自动查找父级上下文”时，会沿 Transform 父级查找。")]
        private MonoGameContextBase _parentContextHost;

        /// <summary>
        /// 纯 C# 父 Context 的代码装配入口。派生类只可在初始化前赋值；Inspector 装配使用
        /// <see cref="_parentContextHost"/>，避免让 Core 为接口序列化依赖第三方插件。
        /// </summary>
        [NonSerialized] protected IGameContext _parentContext;

        [SerializeField]
        [LockInPlayMode]
        [InspectorName("自动查找父级上下文")]
        [Tooltip("是否自动向上查找 Transform 层级中的父级上下文。关闭时不会自动查找，但显式设置的 Parent Context 仍然生效。")]
        protected bool _inheritFromParent = true;

        [SerializeField]
        [LockInPlayMode]
        [InspectorName("回退到全局主上下文（GameContext.Main）")]
        [Tooltip("是否在本地和父级解析不到时回退到全局静态主上下文（GameContext.Main）。")]
        protected bool _inheritFromGlobal = true;

        private IGameContext _resolvedParent;
        private Container _container;
        private GameContext _context;
        private InitializationState _state;
        private Exception _initializationException;

        private enum InitializationState
        {
            Uninitialized,
            Initializing,
            Ready,
            Failed,
            Disposed,
        }

        /// <summary>
        /// 底层 DI 容器。<b>仅框架程序集内可访问</b>，原因见 <see cref="GameContext.Container"/>。
        /// </summary>
        internal Container Container => RequireContext().Container;
        public bool IsDisposed => _state is InitializationState.Failed or InitializationState.Disposed ||
                                  _context == null || _context.IsDisposed;
        public CancellationToken CancellationToken => _context != null ? _context.CancellationToken : new CancellationToken(canceled: true);

        /// <summary>
        /// 内部真实 <see cref="GameContext"/> 实例。<b>仅框架程序集内部可访问</b>（如 <see cref="MonoGlobalContext"/>
        /// 写 <see cref="GameContext.Main"/>），业务代码应通过 <see cref="IGameContext"/> 接口操作。
        /// </summary>
        internal GameContext RawContext => _context;

#if UNITY_EDITOR
        /// <summary>
        /// Editor 诊断快照；读取不会触发 Initialize/Resolve，也不会让失败 Context 重试副作用绑定。
        /// </summary>
        internal MonoContextDiagnosticSnapshot DiagnosticSnapshot => new(
            _state switch
            {
                InitializationState.Uninitialized => MonoContextDiagnosticState.Uninitialized,
                InitializationState.Initializing => MonoContextDiagnosticState.Initializing,
                InitializationState.Ready => MonoContextDiagnosticState.Ready,
                InitializationState.Failed => MonoContextDiagnosticState.Failed,
                InitializationState.Disposed => MonoContextDiagnosticState.Disposed,
                _ => throw new ArgumentOutOfRangeException(),
            },
            _resolvedParent,
            _context,
            _initializationException);
#endif

        /// <summary>IHasGameContext 实现：MonoGameContextBase 本身也是上下文持有者。</summary>
        public IGameContext Context => this;

        protected virtual void Awake() => Initialize();

        /// <summary>
        /// 容器构建完成后的回调钩子，供派生类追加初始化逻辑（如 MonoGlobalContext 注册全局引用）。
        /// 由 Initialize() 在容器构建完毕后调用，无论是 Unity 正常 Awake 还是子级强制触发均只执行一次。
        /// </summary>
        protected virtual void OnInitialized() { }

        private void Initialize()
        {
            if (_state == InitializationState.Ready) return;
            if (_state == InitializationState.Initializing)
                throw new InvalidOperationException(
                    $"[MonoGameContext] 在 '{name}' 检测到 Context 循环初始化；" +
                    "请检查显式 Parent Context 是否形成循环引用。");
            if (_state == InitializationState.Failed)
                throw new InvalidOperationException(
                    $"[MonoGameContext] '{name}' 此前初始化失败，不会重试可能产生副作用的绑定。",
                    _initializationException);
            if (_state == InitializationState.Disposed)
                throw new ObjectDisposedException(nameof(MonoGameContextBase),
                    $"[MonoGameContext] '{name}' 已被销毁。");

            _state = InitializationState.Initializing;
            try
            {
                FindParentContext();

                // 父级与子级可能同处同一 DefaultExecutionOrder，Unity 不保证 Awake 顺序；
                // 若父级尚未初始化则在此递归强制初始化。Initializing 再入会明确报告父级循环，而不是 NRE。
                if (_resolvedParent is MonoGameContextBase monoParent)
                    monoParent.Initialize();

                using var builder = new ContainerBuilder();
                // 自动父链只会在 _inheritFromParent 开启时被 FindParentContext 选中；显式代码/Inspector
                // Parent 则不受该开关影响。此处只看最终解析结果，保持字段 Tooltip 的契约。
                if (_resolvedParent != null)
                    builder.SetParent(ContextInternals.GetContainer(_resolvedParent));

                InstallBindings(builder);
                _container = builder.Build();
                _context = new GameContext(_container, _inheritFromGlobal) { DebugName = name };

                OnInitialized();
                _state = InitializationState.Ready;
            }
            catch (Exception e)
            {
                // Initialize 是一个提交点：只有全部阶段完成才发布 Ready。失败时从最深的 owner 向外回滚，
                // GameContext 构造未返回的路径已由其自身释放 Container；这里的重复 Dispose 仍是幂等的。
                _initializationException = e;
                var context = _context;
                if (GameContext.Main == context)
                    GameContext.Main = null;
                try
                {
                    if (context != null) context.Dispose();
                    else _container?.Dispose();
                }
                catch (Exception cleanupException)
                {
                    Log.Error(
                        $"'{name}'：Context 初始化失败后的清理也抛出了异常；将保留原始失败信息。",
                        cleanupException,
                        "Context",
                        this);
                }
                _context = null;
                _container = null;
                _state = InitializationState.Failed;

                throw new InvalidOperationException(
                    $"[MonoGameContext] '{name}' 初始化失败，已回滚全部已获取资源；" +
                    "请先修复内部异常，再使用此 Context。",
                    e);
            }
        }

        protected virtual void OnDestroy()
        {
            var context = _context;
            _state = InitializationState.Disposed;
            _context = null;
            _container = null;
            if (GameContext.Main == context)
                GameContext.Main = null;
            context?.Dispose();
        }

        /// <summary>子类重写此方法注册基础服务（非 Mono 的纯 C# 服务）。</summary>
        protected virtual void InstallBindings(ContainerBuilder builder) { }

        /// <summary>Transform 父链查找的最大深度。正常项目不会触达，触达时多半是脚本误配（如把 Context 挂在循环引用的 prefab 链上）。</summary>
        private const int MaxParentSearchDepth = 32;

        private void FindParentContext()
        {
            IGameContext explicitParent = _parentContext ?? _parentContextHost;
            if (explicitParent != null)
            {
                if (ReferenceEquals(explicitParent, this))
                {
                    Log.Error(
                        $"'{name}'：Parent Context 指向自身，已忽略该设置。",
                        category: "Context",
                        context: this);
                    return;
                }
                _resolvedParent = explicitParent;
                return;
            }

            if (!_inheritFromParent) return;

            var t = transform.parent;
            var depth = 0;
            while (t != null)
            {
                if (++depth > MaxParentSearchDepth)
                {
                    Log.Error(
                        $"'{name}'：Parent Context 查找深度超过 {MaxParentSearchDepth}。" +
                        "层级可能过深或存在意外循环；请显式设置 Parent Context。",
                        category: "Context",
                        context: this);
                    return;
                }
                var context = t.GetComponent<MonoGameContextBase>();
                if (context != null)
                {
                    _resolvedParent = context;
                    return;
                }
                t = t.parent;
            }
        }

        private GameContext RequireContext()
        {
            // OnInitialized 是事务的最后阶段，允许派生类在钩子里解析已完整构建的容器。
            if (_context != null && !_context.IsDisposed &&
                _state is InitializationState.Initializing or InitializationState.Ready)
                return _context;

            if (_state == InitializationState.Failed)
                throw new InvalidOperationException(
                    $"[MonoGameContext] '{name}' 因初始化失败而不可用；" +
                    "请查看内部异常以定位根因。",
                    _initializationException);
            if (_state == InitializationState.Disposed)
                throw new ObjectDisposedException(nameof(MonoGameContextBase),
                    $"[MonoGameContext] '{name}' 已被销毁。");
            throw new InvalidOperationException(
                $"[MonoGameContext] '{name}' 尚未完成初始化，暂时不能使用。");
        }

        public void Inject(object obj) => RequireContext().Inject(obj);
        /// <inheritdoc />
        public void AttachTo(object target) => RequireContext().AttachTo(target);
        public bool TryResolve(Type type, out object instance) => RequireContext().TryResolve(type, out instance);
        public object Resolve(Type type) => RequireContext().Resolve(type);

        public DisposableBag CreateBag() => RequireContext().CreateBag();

        public T GetModel<T>() where T : class, IModel => RequireContext().GetModel<T>();
        public T GetSystem<T>() where T : class, ISystem => RequireContext().GetSystem<T>();
        public T GetUtility<T>() where T : class, IUtility => RequireContext().GetUtility<T>();

        public void RegisterModel<T>(T instance) where T : class, IModel => RequireContext().RegisterModel(instance);
        public void RegisterSystem<T>(T instance) where T : class, ISystem => RequireContext().RegisterSystem(instance);
        public void RegisterUtility<T>(T instance) where T : class, IUtility => RequireContext().RegisterUtility(instance);

        public void UnregisterModel<T>(T instance) where T : class, IModel => RequireContext().UnregisterModel(instance);
        public void UnregisterSystem<T>(T instance) where T : class, ISystem => RequireContext().UnregisterSystem(instance);
        public void UnregisterUtility<T>(T instance) where T : class, IUtility => RequireContext().UnregisterUtility(instance);

        public void ExecuteCommand<T>(T command) where T : ICommand => RequireContext().ExecuteCommand(command);
        public TResult ExecuteCommand<TResult>(ICommand<TResult> command) => RequireContext().ExecuteCommand(command);
        public TResult ExecuteCommand<T, TResult>(T command) where T : ICommand<TResult> => RequireContext().ExecuteCommand<T, TResult>(command);

        public UniTask ExecuteCommandAsync<T>(T command) where T : IAsyncCommand => RequireContext().ExecuteCommandAsync(command);
        public UniTask ExecuteCommandAsync<T>(T command, CancellationToken cancellationToken) where T : IAsyncCommand => RequireContext().ExecuteCommandAsync(command, cancellationToken);
        public UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command) => RequireContext().ExecuteCommandAsync(command);
        public UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, CancellationToken cancellationToken) => RequireContext().ExecuteCommandAsync(command, cancellationToken);
        public UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command) where T : IAsyncCommand<TResult> => RequireContext().ExecuteCommandAsync<T, TResult>(command);
        public UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, CancellationToken cancellationToken) where T : IAsyncCommand<TResult> => RequireContext().ExecuteCommandAsync<T, TResult>(command, cancellationToken);

        public void SendEvent<T>(T evt = default) where T : IEvent => RequireContext().SendEvent(evt);
        public IDisposable RegisterEvent<T>(Action<T> handler) where T : IEvent => RequireContext().RegisterEvent(handler);
    }
}
