using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Game.Framework.Editor.Tests
{
    public sealed class AssetPackageOperationLaneTests
    {
        [UnityTest]
        public IEnumerator CallerCancellation_DoesNotReleaseRunningLane_AndMixedOperationsStaySerial()
            => CallerCancellation_DoesNotReleaseRunningLane_AndMixedOperationsStaySerialAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator QueuedCallerCancellation_SkipsPhysicalOperation()
            => QueuedCallerCancellation_SkipsPhysicalOperationAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator PhysicalFailure_ReleasesLaneAndIsRethrown()
            => PhysicalFailure_ReleasesLaneAndIsRethrownAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator ProviderWorkerCompletion_QueueAndPublicTerminalReturnMainThread()
            => ProviderWorkerCompletion_QueueAndPublicTerminalReturnMainThreadAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator WorkerWaiterCancellation_PublicTerminalReturnsMainThread()
            => WorkerWaiterCancellation_PublicTerminalReturnsMainThreadAsync().ToCoroutine();

        private static async UniTask CallerCancellation_DoesNotReleaseRunningLane_AndMixedOperationsStaySerialAsync()
        {
            var lane = new AssetPackageOperationLane();
            using var owner = new CancellationTokenSource();
            using var firstWaiter = new CancellationTokenSource();
            var firstStarted = new UniTaskCompletionSource();
            var secondStarted = new UniTaskCompletionSource();
            var thirdStarted = new UniTaskCompletionSource();
            var firstRelease = new UniTaskCompletionSource();
            var secondRelease = new UniTaskCompletionSource();
            var thirdRelease = new UniTaskCompletionSource();
            var order = new List<string>();
            int active = 0;
            int maxActive = 0;

            async UniTask Physical(
                string name,
                UniTaskCompletionSource started,
                UniTaskCompletionSource release,
                CancellationToken token)
            {
                order.Add(name);
                active++;
                maxActive = Math.Max(maxActive, active);
                started.TrySetResult();
                try
                {
                    await release.Task.AttachExternalCancellation(token);
                }
                finally
                {
                    active--;
                }
            }

            var first = lane.Run(
                "ClearCache",
                ct => Physical("clear", firstStarted, firstRelease, ct),
                owner.Token,
                firstWaiter.Token);
            await firstStarted.Task;

            firstWaiter.Cancel();
            await ExpectCanceled(first);

            var second = lane.Run(
                "ClearCacheByTags",
                ct => Physical("tags", secondStarted, secondRelease, ct),
                owner.Token,
                CancellationToken.None);
            var third = lane.Run(
                "UnloadUnusedAssets",
                ct => Physical("unload", thirdStarted, thirdRelease, ct),
                owner.Token,
                CancellationToken.None);

            await UniTask.Yield();
            Assert.AreEqual(1, order.Count, "取消首个 waiter 后不应提前启动下一项物理操作");

            firstRelease.TrySetResult();
            await secondStarted.Task;
            Assert.AreEqual(new[] { "clear", "tags" }, order);
            Assert.AreEqual(1, active);

            secondRelease.TrySetResult();
            await thirdStarted.Task;
            Assert.AreEqual(new[] { "clear", "tags", "unload" }, order);

            thirdRelease.TrySetResult();
            await UniTask.WhenAll(second, third);
            Assert.AreEqual(1, maxActive, "同包维护操作必须严格串行");
            Assert.AreEqual(0, active);
        }

        private static async UniTask QueuedCallerCancellation_SkipsPhysicalOperationAsync()
        {
            var lane = new AssetPackageOperationLane();
            using var owner = new CancellationTokenSource();
            using var queuedWaiter = new CancellationTokenSource();
            var firstStarted = new UniTaskCompletionSource();
            var firstRelease = new UniTaskCompletionSource();
            int physicalCalls = 0;

            var first = lane.Run("first", async ct =>
            {
                physicalCalls++;
                firstStarted.TrySetResult();
                await firstRelease.Task.AttachExternalCancellation(ct);
            }, owner.Token, CancellationToken.None);
            await firstStarted.Task;

            var queued = lane.Run("queued", _ =>
            {
                physicalCalls++;
                return UniTask.CompletedTask;
            }, owner.Token, queuedWaiter.Token);

            // 注册顺序刻意让这个回调有机会先于 AttachExternalCancellation 回调执行：旧实现会先把
            // queued 的 _done 成功完成，从而把“未执行”误报成成功。lane 必须显式保存 waiter 取消结果。
            using var releaseOnCancel = queuedWaiter.Token.Register(() => firstRelease.TrySetResult());
            queuedWaiter.Cancel();
            await ExpectCanceled(queued);
            await first;
            await UniTask.Yield();

            Assert.AreEqual(1, physicalCalls, "排队期间已取消的条目不应产生物理副作用");
        }

        private static async UniTask PhysicalFailure_ReleasesLaneAndIsRethrownAsync()
        {
            var lane = new AssetPackageOperationLane();
            using var owner = new CancellationTokenSource();
            var expected = new InvalidOperationException("clear-failed");
            bool secondRan = false;

            var first = lane.Run("failing", _ => UniTask.FromException(expected), owner.Token, CancellationToken.None);
            var second = lane.Run("next", _ =>
            {
                secondRan = true;
                return UniTask.CompletedTask;
            }, owner.Token, CancellationToken.None);

            Exception actual = null;
            try
            {
                await first;
            }
            catch (Exception ex)
            {
                actual = ex;
            }

            await second;
            Assert.AreSame(expected, actual, "provider 异常应原样交给仍在等待的调用者");
            Assert.IsTrue(secondRan, "失败必须在 finally 语义下释放 lane");
        }

        private static async UniTask ProviderWorkerCompletion_QueueAndPublicTerminalReturnMainThreadAsync()
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int providerThread = -1;
            int nextStartThread = -1;
            var lane = new AssetPackageOperationLane();
            using var owner = new CancellationTokenSource();

            var first = lane.Run("worker-provider", async _ =>
            {
                await UniTask.SwitchToThreadPool();
                providerThread = Thread.CurrentThread.ManagedThreadId;
            }, owner.Token, CancellationToken.None);
            var second = lane.Run("next", _ =>
            {
                nextStartThread = Thread.CurrentThread.ManagedThreadId;
                return UniTask.CompletedTask;
            }, owner.Token, CancellationToken.None);

            await first;
            Assert.AreNotEqual(mainThread, providerThread,
                "测试 operation 必须真实结束在 worker");
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                "lane 的成功终态必须从 Unity 主线程交付");
            await second;
            Assert.AreEqual(mainThread, nextStartThread,
                "worker 物理终态不能让 Drain 从 worker 启动下一项");
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
        }

        private static async UniTask WorkerWaiterCancellation_PublicTerminalReturnsMainThreadAsync()
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int cancellationThread = -1;
            int nextStartThread = -1;
            var lane = new AssetPackageOperationLane();
            using var owner = new CancellationTokenSource();
            using var waiter = new CancellationTokenSource();
            var started = new UniTaskCompletionSource();
            var release = new UniTaskCompletionSource();

            var first = lane.Run("worker-cancel", async ct =>
            {
                started.TrySetResult();
                await release.Task.AttachExternalCancellation(ct);
            }, owner.Token, waiter.Token);
            await started.Task;

            CancelOnThreadPool(waiter, thread => cancellationThread = thread).Forget();
            await ExpectCanceled(first);
            Assert.AreNotEqual(mainThread, cancellationThread,
                "测试 token 必须真实从 worker 发出取消");
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                "waiter 的 OCE 必须回到 Unity 主线程交付");

            var second = lane.Run("after-cancel", _ =>
            {
                nextStartThread = Thread.CurrentThread.ManagedThreadId;
                return UniTask.CompletedTask;
            }, owner.Token, CancellationToken.None);
            release.TrySetResult();
            await second;
            Assert.AreEqual(mainThread, nextStartThread);
        }

        private static async UniTask CancelOnThreadPool(
            CancellationTokenSource source,
            Action<int> recordThread)
        {
            await UniTask.SwitchToThreadPool();
            recordThread(Thread.CurrentThread.ManagedThreadId);
            source.Cancel();
        }

        private static async UniTask ExpectCanceled(UniTask task)
        {
            bool canceled = false;
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Assert.IsTrue(canceled, "调用者取消等待应保持 OperationCanceledException");
        }
    }
}
