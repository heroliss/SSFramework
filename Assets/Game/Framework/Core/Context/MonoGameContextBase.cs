using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.Utility;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
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
    /// <b>层级关系：</b>Inspector 留空 <c>Parent Context</c> + 开启 <c>Inherit From Parent</c> 时，
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
    public class MonoGameContextBase : SerializedMonoBehaviour, IGameContext, IHasGameContext
    {
        [OdinSerialize, ShowInInspector, LabelText("Parent Context"), DisableInPlayMode]
        [Tooltip("显式指定父级上下文。支持 MonoGameContextBase 或运行时赋值的纯 C# IGameContext。留空时根据 Inherit From Parent 自动向 Transform 父级查找。")]
        protected IGameContext _parentContext;

        [SerializeField, LabelText("Inherit From Parent"), DisableInPlayMode]
        [Tooltip("是否自动向上查找 Transform 层级中的父级上下文。关闭时不会自动查找，但显式设置的 Parent Context 仍然生效。")]
        protected bool _inheritFromParent = true;

        [SerializeField, LabelText("Inherit From Global"), DisableInPlayMode]
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

        // 运行时只读诊断统一收进「运行时诊断」可折叠框（与三层 Mono 的诊断同名同风格），仅 Play 显示。
        [FoldoutGroup("运行时诊断"), ShowInInspector, ReadOnly, HideInEditorMode, LabelText("解析到的父级"), PropertyOrder(-100)]
        [PropertyTooltip("运行时实际生效的父级上下文（Parent Context 为空且 Inherit From Parent 开启时，自动向上查找的结果）。")]
        private IGameContext ResolvedParent => _resolvedParent;

        // 本 Context 本地注册的契约键（不含父级回退）。看「这个 Context 里有什么」最直接。
        [FoldoutGroup("运行时诊断"), ShowInInspector, ReadOnly, HideInEditorMode, LabelText("本地注册"), PropertyOrder(-99)]
        [PropertyTooltip("本 Context 本地注册的契约键（不含父级回退）：运行时覆盖（MonoXxxBase / RegisterXxx）+ 构建时绑定（InstallBindings）。\nGetModel/GetSystem/GetUtility<T>() 先在这里找，未命中再回退父级。")]
        private List<string> RegisteredServices
        {
            get
            {
                var names = new List<string>();
                if (_container != null)
                    foreach (var t in _container.LocalRegistrations)
                        names.Add(t.Name);
                names.Sort();
                return names;
            }
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
                    $"[MonoGameContext] Circular Context initialization detected at '{name}'. " +
                    "Check explicit Parent Context assignments for a cycle.");
            if (_state == InitializationState.Failed)
                throw new InvalidOperationException(
                    $"[MonoGameContext] '{name}' previously failed to initialize and will not retry side-effecting bindings.",
                    _initializationException);
            if (_state == InitializationState.Disposed)
                throw new ObjectDisposedException(nameof(MonoGameContextBase),
                    $"[MonoGameContext] '{name}' has already been destroyed.");

            _state = InitializationState.Initializing;
            try
            {
                FindParentContext();

                // 父级与子级可能同处同一 DefaultExecutionOrder，Unity 不保证 Awake 顺序；
                // 若父级尚未初始化则在此递归强制初始化。Initializing 再入会明确报告父级循环，而不是 NRE。
                if (_resolvedParent is MonoGameContextBase monoParent)
                    monoParent.Initialize();

                using var builder = new ContainerBuilder();
                if (_inheritFromParent && _resolvedParent != null)
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
                        $"'{name}': cleanup after Context initialization failure also threw; the original failure is preserved.",
                        cleanupException,
                        "Context",
                        this);
                }
                _context = null;
                _container = null;
                _state = InitializationState.Failed;

                throw new InvalidOperationException(
                    $"[MonoGameContext] '{name}' failed to initialize. All acquired resources were rolled back; " +
                    "fix the inner exception before using this Context.",
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
            if (_parentContext != null)
            {
                if (ReferenceEquals(_parentContext, this))
                {
                    Log.Error(
                        $"'{name}': Parent Context is set to self; ignoring.",
                        category: "Context",
                        context: this);
                    return;
                }
                _resolvedParent = _parentContext;
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
                        $"'{name}': Parent Context search exceeded depth {MaxParentSearchDepth}. " +
                        "Hierarchy too deep or unexpected cycle; explicitly set Parent Context to fix.",
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
                    $"[MonoGameContext] '{name}' is unavailable because initialization failed. " +
                    "Inspect the inner exception for the root cause.",
                    _initializationException);
            if (_state == InitializationState.Disposed)
                throw new ObjectDisposedException(nameof(MonoGameContextBase),
                    $"[MonoGameContext] '{name}' has been destroyed.");
            throw new InvalidOperationException(
                $"[MonoGameContext] '{name}' has not completed initialization and cannot be used yet.");
        }

        public void Inject(object obj) => RequireContext().Inject(obj);
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
