using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;
using Game.Framework.Logging;

namespace Game.Framework
{
    /// <summary>
    /// 串行执行同一资源包的维护操作，并把“调用者等待”与“已经启动的物理操作”分开。
    /// 调用者取消后可以立即离开；已经交给 provider 的操作仍由 owner token 持有，完成前不会释放 lane。
    /// </summary>
    /// <remarks>
    /// 本类型只在 Unity 主线程使用。队列按提交顺序执行；排队期间调用者已取消的条目不会启动。
    /// provider 异常会原样交给仍在等待的调用者；若调用者已取消，则记录一次错误，避免后台失败静默丢失。
    /// </remarks>
    internal sealed class AssetPackageOperationLane
    {
        private readonly Queue<Entry> _pending = new();
        private bool _draining;

        /// <summary>
        /// 排入一个物理操作。<paramref name="waiterToken"/> 只控制当前调用者的等待；
        /// 操作一旦开始，只由 <paramref name="ownerToken"/> 控制其生命周期。
        /// </summary>
        public UniTask Run(
            string operationName,
            Func<CancellationToken, UniTask> operation,
            CancellationToken ownerToken,
            CancellationToken waiterToken)
        {
            MainThreadGuard.AssertMainThread(nameof(AssetPackageOperationLane));
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            waiterToken.ThrowIfCancellationRequested();

            var entry = new Entry(operationName, operation, ownerToken, waiterToken);
            _pending.Enqueue(entry);
            if (!_draining)
            {
                _draining = true;
                Drain().Forget(ex => Log.Error(
                    "资源包操作队列异常停止。", ex,
                    nameof(AssetPackageOperationLane)));
            }

            return entry.Wait();
        }

        private async UniTask Drain()
        {
            try
            {
                while (_pending.Count > 0)
                {
                    var entry = _pending.Dequeue();

                    // 还在排队时取消 = 没有任何物理副作用；owner 已结束时也不要再调用 provider。
                    if (entry.WaiterToken.IsCancellationRequested)
                    {
                        // 显式保存取消结果，不能只依赖 AttachExternalCancellation 的回调先于 Drain 获胜：
                        // 同一次 Cancel 的其他回调可能先结束当前 operation，让这里先完成 _done。
                        entry.Complete(new OperationCanceledException(entry.WaiterToken));
                        continue;
                    }

                    if (entry.OwnerToken.IsCancellationRequested)
                    {
                        entry.Complete(new OperationCanceledException(entry.OwnerToken));
                        continue;
                    }

                    try
                    {
                        // Provider / 第三方 Adapter 可以在 worker 结束。队列、Entry 与 TCS 都是主线程独占状态，
                        // 所以成功、失败、取消三种物理终态都先回主线程再提交并推进下一项。
                        await MainThreadGuard.AwaitOnMainThread(entry.Operation(entry.OwnerToken));
                        entry.Complete();
                    }
                    catch (Exception ex)
                    {
                        entry.Complete(ex);
                    }
                }
            }
            finally
            {
                _draining = false;
            }
        }

        private sealed class Entry
        {
            private readonly UniTaskCompletionSource _done = new();
            private readonly CancellationTokenRegistration _waiterCancellation;
            private ExceptionDispatchInfo _error;
            private int _waiterDetached;

            public readonly string OperationName;
            public readonly Func<CancellationToken, UniTask> Operation;
            public readonly CancellationToken OwnerToken;
            public readonly CancellationToken WaiterToken;

            public Entry(
                string operationName,
                Func<CancellationToken, UniTask> operation,
                CancellationToken ownerToken,
                CancellationToken waiterToken)
            {
                OperationName = string.IsNullOrWhiteSpace(operationName) ? "资源维护" : operationName;
                Operation = operation;
                OwnerToken = ownerToken;
                WaiterToken = waiterToken;
                // token 可能从 worker 取消。先在取消线程只做一次原子标记，再由 Wait 负责切回主线程传播 OCE；
                // 这样物理 operation 若紧接着失败，Complete 仍知道已无人接收并会记录根异常。
                if (waiterToken.CanBeCanceled)
                    _waiterCancellation = waiterToken.Register(
                        () => Interlocked.Exchange(ref _waiterDetached, 1));
            }

            public async UniTask Wait()
            {
                try
                {
                    await MainThreadGuard.AwaitOnMainThread(
                        _done.Task.AttachExternalCancellation(WaiterToken));
                    _error?.Throw();
                }
                finally
                {
                    _waiterCancellation.Dispose();
                }
            }

            public void Complete(Exception error = null)
            {
                MainThreadGuard.AssertMainThread(nameof(AssetPackageOperationLane));
                if (error != null)
                    _error = ExceptionDispatchInfo.Capture(error);

                _done.TrySetResult();

                if (error != null &&
                    Volatile.Read(ref _waiterDetached) != 0 &&
                    !(error is OperationCanceledException &&
                      (OwnerToken.IsCancellationRequested || WaiterToken.IsCancellationRequested)))
                {
                    Log.Error(
                        $"资源包操作“{OperationName}”在调用方停止等待后执行失败。",
                        error,
                        nameof(AssetPackageOperationLane));
                }
            }
        }
    }
}
