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
        private GameContext _context; // RegisterOwned 注册即注入时由 AttachTo 回填
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

            // 最新意图胜：顶替旧排队（其 GoTo task 以取消结束），排队槽只有一格。
            _pendingTcs?.TrySetCanceled();
            var tcs = new UniTaskCompletionSource();
            _pendingState = next;
            _pendingTcs = tcs;

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
                            tcs.TrySetCanceled();
                            return;
                        }
                        finally
                        {
                            old.DisposeScope();
                            if (ReferenceEquals(_exiting, old)) _exiting = null;
                        }
                        if (_disposed) { tcs.TrySetCanceled(); return; }
                    }

                    // OnExit await 期间来了更新的 GoTo：本次进入被顶替（next 从未获得子 Context，无需清理）。
                    if (_pendingState != null)
                    {
                        tcs.TrySetCanceled();
                        continue;
                    }

                    // 2) 构建新状态的子 Context：宿主容器为父级 + 状态私有绑定。
                    //    RegisterOwned 的绑定在 GameContext 构造时注入+回填（ADR-0019），状态内子 flow 等由此成活。
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
                        tcs.TrySetException(e);
                        continue;
                    }

                    // 3) 进入。取消（被顶替 / 宿主释放）或失败 = 半进入：整棵撤、不调 OnExit（清理靠 Bag）。
                    _entering = next;
                    _enterCts = new CancellationTokenSource();
                    try
                    {
                        await next.OnEnter(_enterCts.Token);
                        if (_disposed)
                        {
                            next.DisposeScope();
                            tcs.TrySetCanceled();
                            return;
                        }
                        _current = next;
                        _context.SendEvent(new FlowChangedEvent(transitionFrom, next));
                        transitionFrom = null;
                        tcs.TrySetResult();
                    }
                    catch (OperationCanceledException) when (_enterCts.IsCancellationRequested)
                    {
                        next.DisposeScope();
                        tcs.TrySetCanceled();
                    }
                    catch (OperationCanceledException e)
                    {
                        next.DisposeScope();
                        tcs.TrySetException(new InvalidOperationException(
                            $"FlowState '{next.GetType().Name}' 的 OnEnter 在 GameFlow 未请求取消时抛出了 OperationCanceledException。" +
                            "这通常表示下游操作自行取消；请在状态内决定重试、降级或改抛能说明原因的异常。",
                            e));
                    }
                    catch (Exception e)
                    {
                        next.DisposeScope();
                        tcs.TrySetException(e);
                    }
                    finally
                    {
                        _entering = null;
                        _enterCts.Dispose();
                        _enterCts = null;
                    }
                }
            }
            finally
            {
                _activeTcs = null;
                _running = false;
            }
        }

        /// <summary>
        /// 持有一次已经开始的物理 <see cref="FlowState.OnExit"/> 直到终态。逻辑转换可在宿主 Dispose 时
        /// 通过外部 cancellation 脱离等待，但本 owner 仍会观察迟到失败并写入统一日志。
        /// </summary>
        private static async UniTask ObserveExitToTerminal(FlowState state)
        {
            try { await state.OnExit(); }
            catch (Exception e)
            {
                Log.Error(
                    $"FlowState '{state.GetType().Name}' 的 OnExit 执行失败；流程清理将继续，并仍会释放该状态作用域。",
                    e,
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
        /// 由宿主 <c>GameContext.Dispose</c>（RegisterOwned 逆序释放）调用；转换循环若在途，
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
