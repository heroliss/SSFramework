using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
using YooAsset;

namespace Game.Framework
{
    /// <summary>
    /// 按 YooAsset 进程级 <see cref="ResourcePackage"/> 协调资源读取与缓存维护。
    /// 多个 <see cref="YooAssetProvider"/> 复用同一个 package 时也会命中同一实例，避免各自的上层
    /// Utility lane 只能看见局部操作、却同时修改同一份 YooAsset 文件记录。
    /// </summary>
    /// <remarks>
    /// Reader 可并行；Writer 独占且严格按到达顺序排队。队首出现 Writer 后，后续 Reader 不会插队，
    /// 因而持续加载不会饿死缓存维护。最后一个等待者在获 lease 前取消时会撤销排队，不产生物理副作用；
    /// lease 一旦授予，调用者 token 只取消等待，owner 必须运行到物理终态。终态失败若已无人等待会由本类型记录。
    /// </remarks>
    internal sealed class YooPackageOperationCoordinator
    {
        // YooAssets 的注册表负责 package 的实际生命周期；协调器不能反向强持有已被移除/销毁的 package。
        private static readonly ConditionalWeakTable<ResourcePackage, YooPackageOperationCoordinator> Registry = new();

        private readonly object _gate = new();
        private readonly Queue<LeaseRequest> _pending = new();
        private int _activeReaders;
        private bool _writerActive;
        private long _cacheEpoch;

        // internal 只为 Adapter 自身的隔离测试构造；生产路径必须经 Get(package) 共享实例。
        internal YooPackageOperationCoordinator()
        {
        }

        /// <summary>取得该进程级 package 唯一的协调器。</summary>
        public static YooPackageOperationCoordinator Get(ResourcePackage package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            return Registry.GetValue(package, static _ => new YooPackageOperationCoordinator());
        }

        /// <summary>
        /// 当前下载缓存世代。每个 Clear Writer 到达物理终态后都会前进一步；失败也可能留下部分物理变化，
        /// 因此同样推进，强制基于旧缓存快照创建的 downloader 重建。只卸载内存资源不会推进此值。
        /// </summary>
        public long CacheEpoch
        {
            get
            {
                lock (_gate)
                    return _cacheEpoch;
            }
        }

        /// <summary>
        /// 在不会越过已排队 Writer 的前提下执行一次同步 Reader。
        /// 下载器工厂等同步 YooAsset API 不能在主线程阻塞等待；维护已活跃或排队时直接失败，
        /// 由调用方在维护完成后重试，避免从正在变化的缓存记录创建中间态快照。
        /// </summary>
        public T RunSynchronousReader<T>(string operationName, Func<T> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            lock (_gate)
            {
                DiscardCanceledHeadNoLock();
                if (_writerActive || _pending.Count > 0)
                {
                    string name = string.IsNullOrWhiteSpace(operationName)
                        ? "Yoo 资源包同步读取"
                        : operationName;
                    throw new InvalidOperationException(
                        $"Yoo 资源包操作“{name}”在独占操作正在执行或排队时不能读取缓存状态；" +
                        "请在资源包维护完成后重试。");
                }

                _activeReaders++;
            }

            try
            {
                return operation();
            }
            finally
            {
                Release(LeaseKind.Reader, advanceCacheEpoch: false);
            }
        }

        /// <summary>
        /// 以 Reader 身份运行一次物理操作。排队阶段无人等待会跳过；取得 lease 后调用者取消只脱离结果等待。
        /// 若成功结果携带所有权，可用 <paramref name="releaseAbandonedResult"/> 在唯一调用者脱离后回收。
        /// </summary>
        public UniTask<T> RunReader<T>(
            string operationName,
            Func<UniTask<T>> operation,
            CancellationToken waiterToken,
            Action<T> releaseAbandonedResult = null)
        {
            waiterToken.ThrowIfCancellationRequested();
            return CreateReaderOwner(operationName, operation, releaseAbandonedResult).Wait(waiterToken);
        }

        /// <summary>
        /// 创建可被多个调用者共同等待的 Reader owner。首次等待会启动物理 owner，同一轮等待共享其终态；
        /// downloader 用此语义保证底层 <c>ResourceDownloaderOperation</c> 永远只启动一次。若启用终态重入，
        /// 后续调用会先重新取得 Reader 租约并执行校验，再复用已经保存的物理终态，避免绕过期间发生的缓存维护。
        /// </summary>
        public OperationOwner<T> CreateReaderOwner<T>(
            string operationName,
            Func<UniTask<T>> operation,
            Action<T> releaseAbandonedResult = null,
            Action validateAfterLease = null,
            bool reacquireLeaseAfterTerminal = false)
            => new(
                this,
                LeaseKind.Reader,
                advanceCacheEpoch: false,
                operationName,
                operation,
                releaseAbandonedResult,
                validateAfterLease,
                reacquireLeaseAfterTerminal);

        /// <summary>
        /// 以 Writer 身份运行一次物理维护。它与所有 Reader/Writer 互斥；调用者取消只脱离等待。
        /// </summary>
        public UniTask RunWriter(
            string operationName,
            Func<UniTask> operation,
            CancellationToken waiterToken,
            bool advanceCacheEpoch)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            waiterToken.ThrowIfCancellationRequested();

            var owner = new OperationOwner<bool>(
                this,
                LeaseKind.Writer,
                advanceCacheEpoch,
                operationName,
                async () =>
                {
                    await operation();
                    return true;
                });
            return WaitForWriter(owner, waiterToken);
        }

        private static async UniTask WaitForWriter(OperationOwner<bool> owner, CancellationToken waiterToken)
        {
            _ = await owner.Wait(waiterToken);
        }

        private LeaseRequest Enqueue(LeaseKind kind, bool advanceCacheEpoch)
        {
            var request = new LeaseRequest(this, kind, advanceCacheEpoch);
            List<LeaseRequest> granted;
            lock (_gate)
            {
                _pending.Enqueue(request);
                granted = CollectReadyRequestsNoLock();
            }

            CompleteGrants(granted);
            return request;
        }

        private bool TryCancel(LeaseRequest request)
        {
            List<LeaseRequest> granted;
            lock (_gate)
            {
                if (!request.TryMarkCanceledNoLock())
                    return false;
                granted = CollectReadyRequestsNoLock();
            }

            request.CompleteCanceled();
            CompleteGrants(granted);
            return true;
        }

        private void Release(LeaseKind kind, bool advanceCacheEpoch)
        {
            List<LeaseRequest> granted;
            lock (_gate)
            {
                if (kind == LeaseKind.Reader)
                {
                    if (_activeReaders <= 0)
                        throw new InvalidOperationException(
                            "Yoo 资源包读取租约（Reader lease）在没有活跃读取者时被释放。");
                    _activeReaders--;
                }
                else
                {
                    if (!_writerActive)
                        throw new InvalidOperationException(
                            "Yoo 资源包写入租约（Writer lease）在没有活跃写入者时被释放。");
                    _writerActive = false;
                    if (advanceCacheEpoch)
                    {
                        unchecked
                        {
                            _cacheEpoch++;
                        }
                    }
                }

                granted = CollectReadyRequestsNoLock();
            }

            CompleteGrants(granted);
        }

        // FIFO 队列本身提供公平性：活跃 Reader 只能吸收队首的连续 Reader；一旦 Writer 到达队首，
        // 后续 Reader 全部停在它后面。Writer 释放后再批量放行下一段连续 Reader。
        private List<LeaseRequest> CollectReadyRequestsNoLock()
        {
            if (_writerActive)
                return null;

            DiscardCanceledHeadNoLock();
            if (_pending.Count == 0) return null;

            var granted = new List<LeaseRequest>();
            if (_activeReaders == 0 && _pending.Peek().Kind == LeaseKind.Writer)
            {
                _writerActive = true;
                var writer = _pending.Dequeue();
                writer.MarkGrantedNoLock();
                granted.Add(writer);
                return granted;
            }

            while (true)
            {
                DiscardCanceledHeadNoLock();
                if (_pending.Count == 0 || _pending.Peek().Kind == LeaseKind.Writer)
                    break;

                _activeReaders++;
                var reader = _pending.Dequeue();
                reader.MarkGrantedNoLock();
                granted.Add(reader);
            }

            return granted.Count == 0 ? null : granted;
        }

        private void DiscardCanceledHeadNoLock()
        {
            while (_pending.Count > 0 && _pending.Peek().IsCanceledNoLock)
                _pending.Dequeue();
        }

        private void CompleteGrants(List<LeaseRequest> granted)
        {
            if (granted == null) return;
            for (int i = 0; i < granted.Count; i++)
                granted[i].Grant(new OperationLease(this, granted[i].Kind, granted[i].AdvanceCacheEpoch));
        }

        internal enum LeaseKind
        {
            Reader,
            Writer
        }

        private sealed class LeaseRequest
        {
            private readonly UniTaskCompletionSource<OperationLease> _completion = new();
            private readonly YooPackageOperationCoordinator _coordinator;
            private RequestState _state;

            public LeaseRequest(
                YooPackageOperationCoordinator coordinator,
                LeaseKind kind,
                bool advanceCacheEpoch)
            {
                _coordinator = coordinator;
                Kind = kind;
                AdvanceCacheEpoch = advanceCacheEpoch;
            }

            public LeaseKind Kind { get; }
            public bool AdvanceCacheEpoch { get; }
            public UniTask<OperationLease> Task => _completion.Task;
            public bool IsCanceledNoLock => _state == RequestState.Canceled;

            public bool TryCancelBeforeGrant() => _coordinator.TryCancel(this);

            public bool TryMarkCanceledNoLock()
            {
                if (_state != RequestState.Pending) return false;
                _state = RequestState.Canceled;
                return true;
            }

            public void MarkGrantedNoLock()
            {
                if (_state != RequestState.Pending)
                    throw new InvalidOperationException("只有等待中的 Yoo 资源包租约请求才能被授予。");
                _state = RequestState.Granted;
            }

            public void Grant(OperationLease lease) => _completion.TrySetResult(lease);

            public void CompleteCanceled() => _completion.TrySetResult(null);

            private enum RequestState
            {
                Pending,
                Granted,
                Canceled
            }
        }

        private sealed class OperationLease : IDisposable
        {
            private YooPackageOperationCoordinator _owner;
            private readonly LeaseKind _kind;
            private readonly bool _advanceCacheEpoch;

            public OperationLease(
                YooPackageOperationCoordinator owner,
                LeaseKind kind,
                bool advanceCacheEpoch)
            {
                _owner = owner;
                _kind = kind;
                _advanceCacheEpoch = advanceCacheEpoch;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Release(_kind, _advanceCacheEpoch);
            }
        }

        /// <summary>
        /// 一个可共享等待的物理 owner。调用者 token 永不传入 operation；它只从等待者计数中 detach。
        /// </summary>
        internal sealed class OperationOwner<T>
        {
            private readonly object _gate = new();
            private readonly YooPackageOperationCoordinator _coordinator;
            private readonly LeaseKind _kind;
            private readonly bool _advanceCacheEpoch;
            private readonly string _operationName;
            private readonly Func<UniTask<T>> _operation;
            private readonly Action<T> _releaseAbandonedResult;
            private readonly Action _validateAfterLease;
            private readonly bool _reacquireLeaseAfterTerminal;
            private Attempt _currentAttempt;
            private bool _hasTerminal;
            private T _terminalResult;
            private ExceptionDispatchInfo _terminalError;

            internal OperationOwner(
                YooPackageOperationCoordinator coordinator,
                LeaseKind kind,
                bool advanceCacheEpoch,
                string operationName,
                Func<UniTask<T>> operation,
                Action<T> releaseAbandonedResult = null,
                Action validateAfterLease = null,
                bool reacquireLeaseAfterTerminal = false)
            {
                _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
                _kind = kind;
                _advanceCacheEpoch = advanceCacheEpoch;
                _operationName = string.IsNullOrWhiteSpace(operationName) ? "Yoo 资源包操作" : operationName;
                _operation = operation ?? throw new ArgumentNullException(nameof(operation));
                _releaseAbandonedResult = releaseAbandonedResult;
                _validateAfterLease = validateAfterLease;
                _reacquireLeaseAfterTerminal = reacquireLeaseAfterTerminal;
            }

            /// <summary>加入该 owner 的终态等待；首次调用负责启动 owner。</summary>
            public UniTask<T> Wait(CancellationToken waiterToken)
            {
                waiterToken.ThrowIfCancellationRequested();

                Attempt attempt;
                bool shouldStart = false;
                lock (_gate)
                {
                    attempt = _currentAttempt;
                    if (attempt == null || (_reacquireLeaseAfterTerminal && attempt.Completed))
                    {
                        attempt = new Attempt(
                            _coordinator.Enqueue(_kind, _advanceCacheEpoch),
                            reuseTerminal: _reacquireLeaseAfterTerminal && _hasTerminal);
                        _currentAttempt = attempt;
                        shouldStart = true;
                    }

                    attempt.Waiters++;
                }

                if (shouldStart)
                {
                    Run(attempt).Forget(ex => Log.Error(
                        $"Yoo 资源包操作“{_operationName}”的所有者（owner）异常停止。",
                        ex,
                        nameof(YooPackageOperationCoordinator)));
                }

                return WaitCore(attempt, waiterToken);
            }

            private async UniTask<T> WaitCore(Attempt attempt, CancellationToken waiterToken)
            {
                bool observed = false;
                try
                {
                    await attempt.Completion.Task.AttachExternalCancellation(waiterToken);

                    T result;
                    ExceptionDispatchInfo error;
                    lock (_gate)
                    {
                        result = attempt.Result;
                        error = attempt.Error;
                        attempt.TerminalObserved = true;
                    }

                    observed = true;
                    error?.Throw();
                    return result;
                }
                finally
                {
                    FinishWaiter(attempt, observed);
                }
            }

            private async UniTask Run(Attempt attempt)
            {
                T result = default;
                ExceptionDispatchInfo error = null;
                OperationLease lease = null;
                try
                {
                    lease = await attempt.LeaseRequest.Task;
                    if (lease == null)
                        return; // 最后一个 waiter 在获 lease 前离开；本次 attempt 没有任何物理副作用。

                    lock (_gate)
                        attempt.PhysicalStarted = true;

                    try
                    {
                        _validateAfterLease?.Invoke();
                        if (attempt.ReuseTerminal)
                        {
                            lock (_gate)
                            {
                                result = _terminalResult;
                                error = _terminalError;
                            }
                        }
                        else
                        {
                            result = await _operation();
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ExceptionDispatchInfo.Capture(ex);
                    }
                }
                catch (Exception ex)
                {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    // 会改下载缓存快照的 Writer 先推进 CacheEpoch，再发布终态；刚 await 完 Clear 的
                    // 调用者立刻重建 downloader 时就不会看见维护完成前的旧世代。
                    lease?.Dispose();
                }

                Complete(attempt, result, error);
            }

            private void Complete(Attempt attempt, T result, ExceptionDispatchInfo error)
            {
                bool handleBackgroundTerminal;
                lock (_gate)
                {
                    if (!attempt.ReuseTerminal)
                    {
                        _hasTerminal = true;
                        _terminalResult = result;
                        _terminalError = error;
                    }

                    attempt.Result = result;
                    attempt.Error = error;
                    attempt.Completed = true;
                    handleBackgroundTerminal = ShouldHandleBackgroundTerminalNoLock(attempt);
                }

                attempt.Completion.TrySetResult();
                if (handleBackgroundTerminal)
                    HandleBackgroundTerminal(attempt);
            }

            private void FinishWaiter(Attempt attempt, bool observed)
            {
                bool handleBackgroundTerminal;
                lock (_gate)
                {
                    if (attempt.Waiters <= 0)
                        throw new InvalidOperationException("Yoo 资源包操作的等待者（waiter）计数失衡。");

                    attempt.Waiters--;
                    if (observed)
                        attempt.TerminalObserved = true;

                    // 排队阶段没有任何物理副作用。最后一个等待者离开时撤销请求；downloader 将保留
                    // 同一个 OperationOwner，但下次调用会建立一个全新的 attempt 再排队。
                    if (!observed &&
                        attempt.Waiters == 0 &&
                        !attempt.PhysicalStarted &&
                        !attempt.Completed &&
                        ReferenceEquals(_currentAttempt, attempt) &&
                        attempt.LeaseRequest.TryCancelBeforeGrant())
                    {
                        _currentAttempt = null;
                    }

                    handleBackgroundTerminal = ShouldHandleBackgroundTerminalNoLock(attempt);
                }

                if (handleBackgroundTerminal)
                    HandleBackgroundTerminal(attempt);
            }

            private static bool ShouldHandleBackgroundTerminalNoLock(Attempt attempt)
            {
                if (!attempt.Completed ||
                    attempt.Waiters != 0 ||
                    attempt.TerminalObserved ||
                    attempt.BackgroundTerminalHandled ||
                    attempt.ReuseTerminal)
                    return false;

                attempt.BackgroundTerminalHandled = true;
                return true;
            }

            private void HandleBackgroundTerminal(Attempt attempt)
            {
                if (attempt.Error != null)
                {
                    Log.Error(
                        $"Yoo 资源包操作“{_operationName}”在所有调用方停止等待后执行失败。",
                        attempt.Error.SourceException,
                        nameof(YooPackageOperationCoordinator));
                    return;
                }

                if (_releaseAbandonedResult == null) return;
                try
                {
                    _releaseAbandonedResult(attempt.Result);
                }
                catch (Exception ex)
                {
                    Log.Error(
                        $"Yoo 资源包操作“{_operationName}”释放无人接收的结果时失败。",
                        ex,
                        nameof(YooPackageOperationCoordinator));
                }
            }

            private sealed class Attempt
            {
                public Attempt(LeaseRequest leaseRequest, bool reuseTerminal = false)
                {
                    LeaseRequest = leaseRequest;
                    ReuseTerminal = reuseTerminal;
                }

                public readonly LeaseRequest LeaseRequest;
                public readonly bool ReuseTerminal;
                public readonly UniTaskCompletionSource Completion = new();
                public int Waiters;
                public bool PhysicalStarted;
                public bool Completed;
                public bool TerminalObserved;
                public bool BackgroundTerminalHandled;
                public T Result;
                public ExceptionDispatchInfo Error;
            }
        }
    }
}
