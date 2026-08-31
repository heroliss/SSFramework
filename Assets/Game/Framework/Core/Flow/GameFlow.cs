using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Logging;

namespace Game.Framework.Flow
{
#if UNITY_EDITOR
    /// <summary>
    /// 默认流程 Implementation 的 Editor 只读诊断快照。保持在内部 Seam，避免业务把进入中 / 退出中等
    /// 事务细节当成控制 Interface；编辑器窗口只观察，不持有或修改状态实例。
    /// </summary>
    internal readonly struct GameFlowDiagnosticSnapshot
    {
        internal readonly FlowState Current;
        internal readonly FlowState Exiting;
        internal readonly FlowState Entering;
        internal readonly FlowState Pending;
        internal readonly bool IsRunning;
        internal readonly bool IsDisposed;

        internal GameFlowDiagnosticSnapshot(
            FlowState current,
            FlowState exiting,
            FlowState entering,
            FlowState pending,
            bool isRunning,
            bool isDisposed)
        {
            Current = current;
            Exiting = exiting;
            Entering = entering;
            Pending = pending;
            IsRunning = isRunning;
            IsDisposed = isDisposed;
        }
    }
#endif

    /// <summary>
    /// <see cref="IGameFlow"/> System 的默认实现：串行转换循环 + 最新意图胜排队（一格）+ 每状态一个子 Context。
    /// 纯 C#、零 Unity 对象依赖（异步仅用 UniTask），不发明新作用域机制——只是把 <c>SetParent</c> /
    /// <c>InstallBindings</c> / <c>Dispose</c> / CancellationToken 这些既有原语按正确顺序编排起来。
    /// </summary>
    /// <remarks>
    /// <b>注册：</b><c>builder.RegisterOwnedSystem(new GameFlow())</c>——层感知入口自动登记具体类型与
    /// <see cref="IGameFlow"/>，注册即注入（ADR-0019）回填宿主 Context（<see cref="IHasGameContext"/> 字段），
    /// 脱离容器直接 new 后调 GoTo 会抛
    /// <see cref="InvalidOperationException"/>。<br/>
    /// <b>Dispose</b>（宿主 Context Dispose 时由容器逆序调用）：取消在途进入、当前 / 半进入状态的
    /// 子 Context 整棵撤；若已在等待 <c>OnExit</c>，则立即取消逻辑转换并撤掉正在退出的子 Context，
    /// 物理 <c>OnExit</c> 允许迟到结束且仍由框架观察异常。尚未开始退出时，此路径不主动调用 <c>OnExit</c>
    /// （见 <see cref="FlowState.OnExit"/> 契约）。
    /// Dispose 后 GoTo 抛 <see cref="ObjectDisposedException"/>（对齐 GameContext.ExecuteCommand——
    /// 流程走错比音效丢一声严重，fail-fast）。<br/>
    /// <b>转换中的宿主释放</b>：转换循环每次 await 恢复后都检查 Dispose 标记，半路收尾不半途而废。
    /// </remarks>
    public sealed class GameFlow : IGameFlow, IHasGameContext, IDisposable
    {
        private GameContext _context; // RegisterOwnedSystem 注册即注入时由 AttachTo 回填
        private FlowState _current;
        private FlowState _exiting;   // OnExit 已开始但尚未物理结束；Dispose 必须能立即撤其 scope
        private FlowState _entering;  // 在途进入的状态：Dispose 需要能直接撤它的子 Context
        private FlowState _pendingState;
        private UniTaskCompletionSource _pendingTcs;
        private UniTaskCompletionSource _activeTcs; // 当前已被循环接受的 GoTo；退出阶段同样必须有终态 owner
        private CancellationTokenSource _enterCts;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private bool _running;
        private bool _disposed;

        IGameContext IHasGameContext.Context => _context;

        public FlowState Current => _current;

        public bool IsTransitioning => !_disposed && _running;

#if UNITY_EDITOR
        /// <summary>
        /// 供框架诊断窗口读取转换事务的真实阶段；不进入玩家包，也不扩张 <see cref="IGameFlow"/> Interface。
        /// </summary>
        internal GameFlowDiagnosticSnapshot DiagnosticSnapshot => new(
            _current,
            _exiting,
            _entering,
            _pendingState,
            _running,
            _disposed);
#endif

        public bool IsIn<TState>() where TState : FlowState => _current is TState;

        public UniTask GoTo(FlowState next)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GameFlow), "[GameFlow] 宿主 Context 已释放，不能再转换流程。");
            if (next == null)
                throw new ArgumentNullException(nameof(next));
            if (next.Consumed)
                throw new ArgumentException(
                    $"[GameFlow] FlowState 实例是一次性的：'{next.GetType().Name}' 已被 GoTo 消费过，重进同类状态请 new 一个新实例。",
                    nameof(next));
            if (_context == null)
                throw new InvalidOperationException(
                    "[GameFlow] 尚未挂到宿主 Context——用 builder.RegisterOwnedSystem(new GameFlow()) 注册（层感知注册即注入自动回填），不要脱离容器直接使用。");

            next.Consumed = true;

            // 先提交新意图、再取消旧排队：取消会同步运行旧 task 的 continuation；若 continuation
            // 重入 GoTo，它必须能看见并正常顶替本次请求，不能随后被外层调用覆盖成 orphan。
            UniTaskCompletionSource supersededTcs = _pendingTcs;
            var tcs = new UniTaskCompletionSource();
            _pendingState = next;
            _pendingTcs = tcs;
            supersededTcs?.TrySetCanceled();

            // 旧 task 的同步 continuation 不仅可以顶替本请求，还可能放行当前 hook，让 runner 当场
            // 消费更新请求并换出新的 _enterCts。只有本请求此刻仍占 pending 槽，外层调用栈才有权
            // 取消在途进入或启动 runner；否则会误杀已经取代自己的新 owner。
            if (!ReferenceEquals(_pendingTcs, tcs)) return tcs.Task;

            if (_running)
                CancelEnterSafely(); // 在途 OnEnter 协作取消让路；未在 Enter 阶段（如 OnExit 中）则由循环的排队检查接手
            else
                RunTransitions().Forget();

            return tcs.Task;
        }

        /// <summary>
        /// 串行转换循环：退旧（OnExit + 整棵撤）→ 建新子 Context → 进新（OnEnter）。
        /// 同一时刻至多一个循环在跑（_running 守卫）；循环内所有 await 恢复点都重查 _disposed / 排队。
        /// </summary>
        private async UniTaskVoid RunTransitions()
        {
            _running = true;
            bool runnerReleased = false;

            // UniTaskCompletionSource 会同步交付 continuation。任何 task 终态都必须先摘掉 active owner；
            // 没有后续请求时还要先释放 runner，使 await GoTo 的 continuation 观察到稳定状态，并允许它
            // 立即启动下一轮而不被本轮 finally 把 _running 再写回 false。
            bool PrepareCompletion(UniTaskCompletionSource owner)
            {
                if (ReferenceEquals(_activeTcs, owner)) _activeTcs = null;
                bool continueRunning = !_disposed && _pendingState != null;
                if (!continueRunning)
                {
                    _running = false;
                    runnerReleased = true;
                }
                return continueRunning;
            }

            // 一轮连续转换可能跨过多个从未完整进入的候选状态。事件的 From 要保留最后一个已发布状态，
            // 直到某个 To 真正进入成功；否则 A → (B 被顶替) → C 会被误报成 null → C。
            FlowState transitionFrom = null;
            try
            {
                while (!_disposed && _pendingState != null)
                {
                    var next = _pendingState;
                    var tcs = _pendingTcs;
                    _pendingState = null;
                    _pendingTcs = null;
                    _activeTcs = tcs;

                    // 1) 退出当前状态。OnExit 失败只记录（离开失败不卡死在旧阶段），子 Context 照撤。
                    var old = _current;
                    _current = null;
                    if (old != null)
                    {
                        transitionFrom ??= old;
                        _exiting = old;
                        bool exitCanceledByDispose = false;
                        try
                        {
                            // OnExit 没有 token（它是尽力而为的优雅告别），不能强迫业务物理停止；但宿主释放时
                            // flow 自己的 waiter 必须立即脱离，不能让已接受的 GoTo 与整个 flow 永久悬挂。
                            // ObserveExitToTerminal 继续持有物理任务并观察迟到异常，不制造无人观察的 UniTask。
                            // token 必须先取：OnExit 的同步部分允许重入宿主 Dispose，而 Dispose 会释放 CTS。
                            var lifetimeToken = _lifetimeCts.Token;
                            await ObserveExitToTerminal(old).AttachExternalCancellation(lifetimeToken);
                        }
                        catch (OperationCanceledException) when (_disposed)
                        {
                            exitCanceledByDispose = true;
                        }
                        finally
                        {
                            old.DisposeScope();
                            if (ReferenceEquals(_exiting, old)) _exiting = null;
                        }
                        if (exitCanceledByDispose || _disposed)
                        {
                            PrepareCompletion(tcs);
                            tcs.TrySetCanceled();
                            return;
                        }
                    }

                    // OnExit await 期间来了更新的 GoTo：本次进入被顶替（next 从未获得子 Context，无需清理）。
                    if (_pendingState != null)
                    {
                        bool continueRunning = PrepareCompletion(tcs);
                        tcs.TrySetCanceled();
                        if (!continueRunning) return;
                        continue;
                    }

                    // 2) 构建新状态的子 Context：宿主容器为父级 + 状态私有绑定。
                    //    RegisterOwnedSystem 的值绑定在 GameContext 构造时注入+回填（ADR-0019），状态内子 flow 等由此成活。
                    GameContext scope = null;
                    try
                    {
                        using var builder = new ContainerBuilder();
                        builder.SetParent(_context.Container);
                        next.InstallBindings(builder);
                        scope = new GameContext(builder.Build(), inheritFromGlobal: true)
                        {
                            DebugName = $"Flow:{next.GetType().Name}",
                        };
                        next.AttachScope(scope);
                    }
                    catch (Exception e)
                    {
                        // Install / Build / Context 构造都是进入事务的一部分。失败状态从未成为 Current，
                        // Builder 或 Context 负责回滚 owned 资源，本次 GoTo 以原异常完成而不是永久 Pending。
                        scope?.Dispose();
                        if (_disposed)
                        {
                            PrepareCompletion(tcs);
                            tcs.TrySetCanceled();
                            return;
                        }
                        bool continueRunning = PrepareCompletion(tcs);
                        tcs.TrySetException(e);
                        if (!continueRunning) return;
                        continue;
                    }

                    // InstallBindings、构造期 Inject/Attach 都是可重入的用户边界。只有整个 scope 建成后
                    // 仍是最新意图且宿主存活，才允许调用 OnEnter；否则撤掉未发布 scope。
                    if (_disposed || _pendingState != null)
                    {
                        next.DisposeScope();
                        bool continueRunning = PrepareCompletion(tcs);
                        tcs.TrySetCanceled();
                        if (!continueRunning) return;
                        continue;
                    }

                    // 3) 进入。取消（被顶替 / 宿主释放）或失败 = 半进入：整棵撤、不调 OnExit（清理靠 Bag）。
                    _entering = next;
                    var enterCts = new CancellationTokenSource();
                    _enterCts = enterCts;
                    Exception enterError = await ObserveEnterToTerminal(next, enterCts.Token);
                    bool flowRequestedCancellation = enterCts.IsCancellationRequested;
                    if (ReferenceEquals(_entering, next)) _entering = null;
                    if (ReferenceEquals(_enterCts, enterCts)) _enterCts = null;
                    enterCts.Dispose();

                    // 清理进入 owner 后才发布 scope/task 终态：FlowChanged handler 或 GoTo awaiter 可同步重入，
                    // 但此时已经成功的 token 不会再被下一次 GoTo 当成“半进入”取消。
                    if (_disposed)
                    {
                        next.DisposeScope();
                        PrepareCompletion(tcs);
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (enterError is OperationCanceledException && flowRequestedCancellation)
                    {
                        next.DisposeScope();
                        bool continueRunning = PrepareCompletion(tcs);
                        tcs.TrySetCanceled();
                        if (!continueRunning) return;
                        continue;
                    }

                    if (enterError is OperationCanceledException unexpectedCancellation)
                    {
                        next.DisposeScope();
                        bool continueRunning = PrepareCompletion(tcs);
                        tcs.TrySetException(new InvalidOperationException(
                            $"FlowState '{next.GetType().Name}' 的 OnEnter 在 GameFlow 未请求取消时抛出了 OperationCanceledException。" +
                            "这通常表示下游操作自行取消；请在状态内决定重试、降级或改抛能说明原因的异常。",
                            unexpectedCancellation));
                        if (!continueRunning) return;
                        continue;
                    }

                    if (enterError != null)
                    {
                        next.DisposeScope();
                        bool continueRunning = PrepareCompletion(tcs);
                        tcs.TrySetException(enterError);
                        if (!continueRunning) return;
                        continue;
                    }

                    _current = next;
                    _context.SendEvent(new FlowChangedEvent(transitionFrom, next));
                    transitionFrom = null;
                    if (_disposed)
                    {
                        PrepareCompletion(tcs);
                        tcs.TrySetCanceled();
                        return;
                    }

                    bool hasNext = PrepareCompletion(tcs);
                    tcs.TrySetResult();
                    if (!hasNext) return;
                }
            }
            finally
            {
                if (!runnerReleased)
                {
                    _activeTcs = null;
                    _running = false;
                }
            }
        }

        /// <summary>
        /// 用户 OnEnter 可以在任意 await 后结束到 worker；只在这里捕获物理终态，切回 Unity 主线程后
        /// 才允许状态机分类异常、撤 scope 或发布 Current/Event/task。
        /// </summary>
        private static async UniTask<Exception> ObserveEnterToTerminal(
            FlowState state,
            CancellationToken cancellationToken)
        {
            Exception error = null;
            try { await state.OnEnter(cancellationToken); }
            catch (Exception e) { error = e; }
            await UniTask.SwitchToMainThread();
            return error;
        }

        /// <summary>
        /// 持有一次已经开始的物理 <see cref="FlowState.OnExit"/> 直到终态。逻辑转换可在宿主 Dispose 时
        /// 通过外部 cancellation 脱离等待，但本 owner 仍会观察迟到失败并写入统一日志。
        /// </summary>
        private static async UniTask ObserveExitToTerminal(FlowState state)
        {
            Exception error = null;
            try { await state.OnExit(); }
            catch (Exception e) { error = e; }
            await UniTask.SwitchToMainThread();
            if (error != null)
            {
                Log.Error(
                    $"FlowState '{state.GetType().Name}' 的 OnExit 执行失败；流程清理将继续，并仍会释放该状态作用域。",
                    error,
                    "GameFlow");
            }
        }

        // CancellationTokenSource.Cancel 会聚合并抛出业务注册的取消回调异常。取消意图此时已经成立，
        // 但不能让一个坏回调截断 GoTo 返回值或宿主 Dispose 的 scope sweep。
        private void CancelEnterSafely()
        {
            if (_enterCts == null) return;
            try { _enterCts.Cancel(); }
            catch (Exception e)
            {
                Log.Error(
                    "OnEnter 的取消回调执行失败；流程取消与作用域清理将继续。",
                    e,
                    "GameFlow");
            }
        }

        /// <summary>
        /// 释放流程：取消排队与在途进入，当前 / 半进入状态的子 Context 整棵撤。幂等。
        /// 由宿主 <c>GameContext.Dispose</c>（RegisterOwnedSystem 逆序释放）调用；转换循环若在途，
        /// 恢复后自查 Dispose 标记收尾（DisposeScope 幂等，先撤不冲突）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pendingTcs?.TrySetCanceled();
            _pendingTcs = null;
            _pendingState = null;
            _activeTcs?.TrySetCanceled();

            // 先结束 flow 自己对无 token OnExit 的等待；物理任务由 ObserveExitToTerminal 继续观察到终态。
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();

            CancelEnterSafely();
            _exiting?.DisposeScope();
            _entering?.DisposeScope();
            _current?.DisposeScope();
            _exiting = null;
            _current = null;
        }
    }
}
