using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Game.Framework.Asset.Yoo.Tests
{
    /// <summary>
    /// 用可控 gate 验证 Yoo package 进程级协调语义，不启动真实 Package、文件系统或 CDN。
    /// </summary>
    public sealed class YooPackageOperationCoordinatorTests
    {
        [UnityTest]
        public IEnumerator ReadersCanOverlap_WriterIsExclusive_AndLaterReaderCannotOvertakeQueuedWriter()
            => ReadersCanOverlap_WriterIsExclusive_AndLaterReaderCannotOvertakeQueuedWriterAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator SoleWaiterCanceledBeforeGrant_SkipsQueuedWriter_AndDoesNotAdvanceEpoch()
            => SoleWaiterCanceledBeforeGrant_SkipsQueuedWriter_AndDoesNotAdvanceEpochAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator WaiterCanceledAfterPhysicalStart_DetachesOnly_AndWriterAdvancesEpochAtTerminal()
            => WaiterCanceledAfterPhysicalStart_DetachesOnly_AndWriterAdvancesEpochAtTerminalAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator WriterFailure_IsRethrown_AdvancesEpoch_AndReleasesNextRequest()
            => WriterFailure_IsRethrown_AdvancesEpoch_AndReleasesNextRequestAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator SharedReaderOwner_StartsOnce_AndWaiterCancellationIsIndependent()
            => SharedReaderOwner_StartsOnce_AndWaiterCancellationIsIndependentAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator SharedReaderOwner_CanceledBeforeGrant_CanStartOnLaterWait()
            => SharedReaderOwner_CanceledBeforeGrant_CanStartOnLaterWaitAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator TerminalReaderOwner_ReacquiresLeaseBeforeReusingResult()
            => TerminalReaderOwner_ReacquiresLeaseBeforeReusingResultAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator SynchronousReader_WithoutWriter_Executes_AndWriterCannotStartInsideDelegate()
            => SynchronousReader_WithoutWriter_Executes_AndWriterCannotStartInsideDelegateAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator SynchronousReader_ActiveOrQueuedWriter_ThrowsWithoutExecutingDelegate()
            => SynchronousReader_ActiveOrQueuedWriter_ThrowsWithoutExecutingDelegateAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator CancelledReaderWaiter_SuccessfulOwnedResult_IsReleasedExactlyOnce()
            => CancelledReaderWaiter_SuccessfulOwnedResult_IsReleasedExactlyOnceAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator CancelledSoleWaiter_BackgroundFailure_IsLoggedExactlyOnce()
            => CancelledSoleWaiter_BackgroundFailure_IsLoggedExactlyOnceAsync().ToCoroutine();

        private static async UniTask ReadersCanOverlap_WriterIsExclusive_AndLaterReaderCannotOvertakeQueuedWriterAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            var firstStarted = new UniTaskCompletionSource();
            var secondStarted = new UniTaskCompletionSource();
            var writerStarted = new UniTaskCompletionSource();
            var laterStarted = new UniTaskCompletionSource();
            var firstRelease = new UniTaskCompletionSource();
            var secondRelease = new UniTaskCompletionSource();
            var writerRelease = new UniTaskCompletionSource();
            var laterRelease = new UniTaskCompletionSource();
            var order = new List<string>();
            int activeReaders = 0;
            int maxActiveReaders = 0;
            bool writerActive = false;
            bool readerOverlappedWriter = false;
            bool writerOverlappedReaders = false;

            async UniTask<int> HoldReader(
                string name,
                UniTaskCompletionSource started,
                UniTaskCompletionSource release)
            {
                readerOverlappedWriter |= writerActive;
                order.Add(name);
                activeReaders++;
                maxActiveReaders = Math.Max(maxActiveReaders, activeReaders);
                started.TrySetResult();
                try
                {
                    await release.Task;
                    return activeReaders;
                }
                finally
                {
                    activeReaders--;
                }
            }

            async UniTask HoldWriter()
            {
                writerOverlappedReaders |= activeReaders != 0;
                writerActive = true;
                order.Add("writer");
                writerStarted.TrySetResult();
                try
                {
                    await writerRelease.Task;
                }
                finally
                {
                    writerActive = false;
                }
            }

            var first = coordinator.RunReader(
                "first-reader", () => HoldReader("first", firstStarted, firstRelease), CancellationToken.None);
            var second = coordinator.RunReader(
                "second-reader", () => HoldReader("second", secondStarted, secondRelease), CancellationToken.None);
            await firstStarted.Task;
            await secondStarted.Task;
            Assert.AreEqual(2, activeReaders);

            var writer = coordinator.RunWriter(
                "writer", HoldWriter, CancellationToken.None, advanceCacheEpoch: true);
            var later = coordinator.RunReader(
                "later-reader", () => HoldReader("later", laterStarted, laterRelease), CancellationToken.None);
            await UniTask.Yield();

            Assert.IsFalse(writerStarted.Task.GetAwaiter().IsCompleted,
                "活跃 Reader 尚未全部退出时 Writer 不得开始");
            Assert.IsFalse(laterStarted.Task.GetAwaiter().IsCompleted,
                "Writer 已排队后，后来 Reader 不得插队");

            firstRelease.TrySetResult();
            await first;
            await UniTask.Yield();
            Assert.IsFalse(writerStarted.Task.GetAwaiter().IsCompleted,
                "仍有一个 Reader 活跃时 Writer 不得开始");

            secondRelease.TrySetResult();
            await second;
            await writerStarted.Task;
            Assert.IsTrue(writerActive);
            Assert.IsFalse(laterStarted.Task.GetAwaiter().IsCompleted,
                "Writer 物理操作未结束前，后来 Reader 仍须等待");

            writerRelease.TrySetResult();
            await writer;
            await laterStarted.Task;
            laterRelease.TrySetResult();
            await later;

            Assert.AreEqual(2, maxActiveReaders, "队首连续 Reader 应并行获租约");
            Assert.IsFalse(readerOverlappedWriter, "Reader 不得与 Writer 重叠");
            Assert.IsFalse(writerOverlappedReaders, "Writer 必须等所有活跃 Reader 结束");
            CollectionAssert.AreEqual(new[] { "first", "second", "writer", "later" }, order);
        }

        private static async UniTask SoleWaiterCanceledBeforeGrant_SkipsQueuedWriter_AndDoesNotAdvanceEpochAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            var blockerStarted = new UniTaskCompletionSource();
            var blockerRelease = new UniTaskCompletionSource();
            var laterStarted = new UniTaskCompletionSource();
            var laterRelease = new UniTaskCompletionSource();
            using var writerWaiter = new CancellationTokenSource();
            int writerPhysicalCalls = 0;
            long initialEpoch = coordinator.CacheEpoch;

            var blocker = coordinator.RunReader(
                "blocker",
                async () =>
                {
                    blockerStarted.TrySetResult();
                    await blockerRelease.Task;
                    return true;
                },
                CancellationToken.None);
            await blockerStarted.Task;

            var skippedWriter = coordinator.RunWriter(
                "queued-clear",
                () =>
                {
                    writerPhysicalCalls++;
                    return UniTask.CompletedTask;
                },
                writerWaiter.Token,
                advanceCacheEpoch: true);
            var laterReader = coordinator.RunReader(
                "later-reader",
                async () =>
                {
                    laterStarted.TrySetResult();
                    await laterRelease.Task;
                    return true;
                },
                CancellationToken.None);

            await UniTask.Yield();
            Assert.IsFalse(laterStarted.Task.GetAwaiter().IsCompleted,
                "未取消的队首 Writer 仍应阻止后来 Reader 插队");

            writerWaiter.Cancel();
            await ExpectCanceled(skippedWriter);
            await laterStarted.Task;

            Assert.AreEqual(0, writerPhysicalCalls, "最后一个 waiter 在获租约前离开时不得产生物理副作用");
            Assert.AreEqual(initialEpoch, coordinator.CacheEpoch, "跳过的 Clear Writer 不得推进缓存世代");

            laterRelease.TrySetResult();
            blockerRelease.TrySetResult();
            await laterReader;
            await blocker;
        }

        private static async UniTask WaiterCanceledAfterPhysicalStart_DetachesOnly_AndWriterAdvancesEpochAtTerminalAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            var writerStarted = new UniTaskCompletionSource();
            var writerRelease = new UniTaskCompletionSource();
            var laterStarted = new UniTaskCompletionSource();
            var laterRelease = new UniTaskCompletionSource();
            using var writerWaiter = new CancellationTokenSource();
            int writerPhysicalCalls = 0;
            long initialEpoch = coordinator.CacheEpoch;

            var writer = coordinator.RunWriter(
                "running-clear",
                async () =>
                {
                    writerPhysicalCalls++;
                    writerStarted.TrySetResult();
                    await writerRelease.Task;
                },
                writerWaiter.Token,
                advanceCacheEpoch: true);
            await writerStarted.Task;

            var laterReader = coordinator.RunReader(
                "reader-after-clear",
                async () =>
                {
                    laterStarted.TrySetResult();
                    await laterRelease.Task;
                    return true;
                },
                CancellationToken.None);

            writerWaiter.Cancel();
            await ExpectCanceled(writer);
            await UniTask.Yield();

            Assert.AreEqual(1, writerPhysicalCalls);
            Assert.AreEqual(initialEpoch, coordinator.CacheEpoch,
                "物理 Writer 尚未到终态时不能提前发布新缓存世代");
            Assert.IsFalse(laterStarted.Task.GetAwaiter().IsCompleted,
                "waiter 离开不能提前释放已经授予的 Writer lease");

            writerRelease.TrySetResult();
            await laterStarted.Task;
            Assert.AreEqual(initialEpoch + 1, coordinator.CacheEpoch,
                "Clear Writer 应在真实物理终态推进缓存世代");

            laterRelease.TrySetResult();
            await laterReader;
        }

        private static async UniTask WriterFailure_IsRethrown_AdvancesEpoch_AndReleasesNextRequestAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            var expected = new InvalidOperationException("writer-physical-failure");
            var writerStarted = new UniTaskCompletionSource();
            var writerRelease = new UniTaskCompletionSource();
            var readerStarted = new UniTaskCompletionSource();
            var readerRelease = new UniTaskCompletionSource();
            long initialEpoch = coordinator.CacheEpoch;

            var writer = coordinator.RunWriter(
                "failing-clear",
                async () =>
                {
                    writerStarted.TrySetResult();
                    await writerRelease.Task;
                    throw expected;
                },
                CancellationToken.None,
                advanceCacheEpoch: true);
            await writerStarted.Task;

            var reader = coordinator.RunReader(
                "reader-after-failure",
                async () =>
                {
                    readerStarted.TrySetResult();
                    await readerRelease.Task;
                    return true;
                },
                CancellationToken.None);

            writerRelease.TrySetResult();
            var actual = await CaptureException(writer);
            await readerStarted.Task;

            Assert.AreSame(expected, actual, "仍在等待的调用者应收到原始物理异常");
            Assert.AreEqual(initialEpoch + 1, coordinator.CacheEpoch,
                "失败也可能留下部分缓存变化，Clear Writer 仍须推进世代");

            readerRelease.TrySetResult();
            await reader;
        }

        private static async UniTask SharedReaderOwner_StartsOnce_AndWaiterCancellationIsIndependentAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            var physicalStarted = new UniTaskCompletionSource();
            var physicalRelease = new UniTaskCompletionSource();
            using var firstWaiter = new CancellationTokenSource();
            int physicalCalls = 0;

            var owner = coordinator.CreateReaderOwner(
                "shared-download",
                async () =>
                {
                    physicalCalls++;
                    physicalStarted.TrySetResult();
                    await physicalRelease.Task;
                    return 42;
                });

            var first = owner.Wait(firstWaiter.Token);
            var second = owner.Wait(CancellationToken.None);
            await physicalStarted.Task;

            firstWaiter.Cancel();
            await ExpectCanceled(first);
            Assert.AreEqual(1, physicalCalls);
            Assert.IsFalse(second.GetAwaiter().IsCompleted,
                "一个 waiter 取消不能完成或取消其他 waiter 的共享物理 owner");

            physicalRelease.TrySetResult();
            Assert.AreEqual(42, await second);
            Assert.AreEqual(42, await owner.Wait(CancellationToken.None),
                "终态后的调用应复用同一 owner 结果");
            Assert.AreEqual(1, physicalCalls, "共享 owner 的物理操作必须严格只启动一次");
        }

        private static async UniTask SharedReaderOwner_CanceledBeforeGrant_CanStartOnLaterWaitAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            var blockerStarted = new UniTaskCompletionSource();
            var blockerRelease = new UniTaskCompletionSource();
            using var firstWaiter = new CancellationTokenSource();
            int physicalCalls = 0;

            var blocker = coordinator.RunWriter(
                "maintenance-blocker",
                async () =>
                {
                    blockerStarted.TrySetResult();
                    await blockerRelease.Task;
                },
                CancellationToken.None,
                advanceCacheEpoch: false);
            await blockerStarted.Task;

            var owner = coordinator.CreateReaderOwner(
                "restartable-download",
                () =>
                {
                    physicalCalls++;
                    return UniTask.FromResult(7);
                });

            var abandonedWait = owner.Wait(firstWaiter.Token);
            firstWaiter.Cancel();
            await ExpectCanceled(abandonedWait);
            Assert.AreEqual(0, physicalCalls,
                "获 lease 前最后一个 waiter 离开时，不得启动共享物理 operation");

            var laterWait = owner.Wait(CancellationToken.None);
            blockerRelease.TrySetResult();
            Assert.AreEqual(7, await laterWait,
                "同一个 downloader owner 的后续调用应建立新 attempt 并正常运行");
            await blocker;
            Assert.AreEqual(1, physicalCalls);
        }

        private static async UniTask TerminalReaderOwner_ReacquiresLeaseBeforeReusingResultAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            var writerStarted = new UniTaskCompletionSource();
            var writerRelease = new UniTaskCompletionSource();
            int physicalCalls = 0;
            int validationCalls = 0;

            var owner = coordinator.CreateReaderOwner(
                "sticky-downloader",
                () =>
                {
                    physicalCalls++;
                    return UniTask.FromResult(19);
                },
                validateAfterLease: () => validationCalls++,
                reacquireLeaseAfterTerminal: true);

            Assert.AreEqual(19, await owner.Wait(CancellationToken.None));
            Assert.AreEqual(1, physicalCalls);
            Assert.AreEqual(1, validationCalls);

            var writer = coordinator.RunWriter(
                "cache-maintenance",
                async () =>
                {
                    writerStarted.TrySetResult();
                    await writerRelease.Task;
                },
                CancellationToken.None,
                advanceCacheEpoch: false);
            await writerStarted.Task;

            var reuse = owner.Wait(CancellationToken.None);
            await UniTask.Yield();
            Assert.IsFalse(reuse.GetAwaiter().IsCompleted,
                "终态 owner 的再次等待必须重新排 Reader，不能越过活跃 Writer 直接返回旧结果");
            Assert.AreEqual(1, physicalCalls, "重入只能复用物理终态，不能再次启动 downloader");
            Assert.AreEqual(1, validationCalls, "Writer 释放租约前不得执行终态重入校验");

            writerRelease.TrySetResult();
            await writer;
            Assert.AreEqual(19, await reuse);
            Assert.AreEqual(1, physicalCalls);
            Assert.AreEqual(2, validationCalls, "重入应在重新取得 Reader 租约后再次校验");
        }

        private static async UniTask SynchronousReader_WithoutWriter_Executes_AndWriterCannotStartInsideDelegateAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            UniTask queuedWriter = default;
            bool synchronousDelegateActive = false;
            bool writerStartedWhileDelegateActive = false;
            int synchronousCalls = 0;
            int writerCalls = 0;

            int result = coordinator.RunSynchronousReader(
                "create-downloader",
                () =>
                {
                    synchronousDelegateActive = true;
                    try
                    {
                        synchronousCalls++;
                        queuedWriter = coordinator.RunWriter(
                            "writer-enqueued-inside-sync-read",
                            () =>
                            {
                                writerCalls++;
                                writerStartedWhileDelegateActive |= synchronousDelegateActive;
                                return UniTask.CompletedTask;
                            },
                            CancellationToken.None,
                            advanceCacheEpoch: true);

                        Assert.IsFalse(queuedWriter.GetAwaiter().IsCompleted,
                            "同步 Reader delegate 尚未返回时，内部排入的 Writer 不能获得租约");
                        return 73;
                    }
                    finally
                    {
                        synchronousDelegateActive = false;
                    }
                });

            Assert.AreEqual(73, result);
            Assert.AreEqual(1, synchronousCalls);
            await queuedWriter;
            Assert.AreEqual(1, writerCalls);
            Assert.IsFalse(writerStartedWhileDelegateActive,
                "同步 Reader 的 operation 执行期间 Writer 不得启动");
        }

        private static async UniTask SynchronousReader_ActiveOrQueuedWriter_ThrowsWithoutExecutingDelegateAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            var activeWriterStarted = new UniTaskCompletionSource();
            var activeWriterRelease = new UniTaskCompletionSource();
            int synchronousCalls = 0;

            var activeWriter = coordinator.RunWriter(
                "active-writer",
                async () =>
                {
                    activeWriterStarted.TrySetResult();
                    await activeWriterRelease.Task;
                },
                CancellationToken.None,
                advanceCacheEpoch: true);
            await activeWriterStarted.Task;

            var activeError = Assert.Throws<InvalidOperationException>(() =>
                coordinator.RunSynchronousReader(
                    "sync-during-active-writer",
                    () =>
                    {
                        synchronousCalls++;
                        return 1;
                    }));
            StringAssert.Contains("sync-during-active-writer", activeError.Message,
                "拒绝同步读取时应带上操作名，方便定位哪个工厂需要维护完成后重试");
            Assert.AreEqual(0, synchronousCalls);

            activeWriterRelease.TrySetResult();
            await activeWriter;

            var readerStarted = new UniTaskCompletionSource();
            var readerRelease = new UniTaskCompletionSource();
            var queuedWriterStarted = new UniTaskCompletionSource();
            var queuedWriterRelease = new UniTaskCompletionSource();
            var activeReader = coordinator.RunReader(
                "active-reader",
                async () =>
                {
                    readerStarted.TrySetResult();
                    await readerRelease.Task;
                    return true;
                },
                CancellationToken.None);
            await readerStarted.Task;

            var queuedWriter = coordinator.RunWriter(
                "queued-writer",
                async () =>
                {
                    queuedWriterStarted.TrySetResult();
                    await queuedWriterRelease.Task;
                },
                CancellationToken.None,
                advanceCacheEpoch: false);
            await UniTask.Yield();
            Assert.IsFalse(queuedWriterStarted.Task.GetAwaiter().IsCompleted);

            var queuedError = Assert.Throws<InvalidOperationException>(() =>
                coordinator.RunSynchronousReader(
                    "sync-after-queued-writer",
                    () =>
                    {
                        synchronousCalls++;
                        return 2;
                    }));
            StringAssert.Contains("sync-after-queued-writer", queuedError.Message);
            Assert.AreEqual(0, synchronousCalls,
                "同步 Reader 不得越过已经排队的 Writer，也不得调用传入 operation");

            readerRelease.TrySetResult();
            await activeReader;
            await queuedWriterStarted.Task;
            queuedWriterRelease.TrySetResult();
            await queuedWriter;
        }

        private static async UniTask CancelledReaderWaiter_SuccessfulOwnedResult_IsReleasedExactlyOnceAsync()
        {
            var coordinator = new YooPackageOperationCoordinator();
            var physicalStarted = new UniTaskCompletionSource();
            var physicalRelease = new UniTaskCompletionSource();
            using var waiter = new CancellationTokenSource();
            var expectedResult = new object();
            object releasedResult = null;
            int releaseCalls = 0;

            var waiting = coordinator.RunReader(
                "abandoned-handle",
                async () =>
                {
                    physicalStarted.TrySetResult();
                    await physicalRelease.Task;
                    return expectedResult;
                },
                waiter.Token,
                result =>
                {
                    releasedResult = result;
                    releaseCalls++;
                });
            await physicalStarted.Task;

            waiter.Cancel();
            await ExpectCanceled(waiting);
            physicalRelease.TrySetResult();
            await UniTask.Yield();
            await UniTask.Yield();

            Assert.AreSame(expectedResult, releasedResult);
            Assert.AreEqual(1, releaseCalls, "无人接收的成功 handle 必须回收且只能回收一次");
        }

        private static async UniTask CancelledSoleWaiter_BackgroundFailure_IsLoggedExactlyOnceAsync()
        {
            const string OperationName = "background-failure-probe";
            var previousSinks = new List<ILogSink>(Game.Framework.Logging.Log.Sinks);
            var previousMinLevel = Game.Framework.Logging.Log.MinLevel;
            var probe = new ErrorProbeSink(OperationName);

            Game.Framework.Logging.Log.ClearSinks();
            Game.Framework.Logging.Log.MinLevel = LogLevel.Info;
            Game.Framework.Logging.Log.AddSink(probe);
            try
            {
                var coordinator = new YooPackageOperationCoordinator();
                var expected = new InvalidOperationException("background-owner-failure");
                var physicalStarted = new UniTaskCompletionSource();
                var physicalRelease = new UniTaskCompletionSource();
                using var waiter = new CancellationTokenSource();

                var waiting = coordinator.RunReader<int>(
                    OperationName,
                    async () =>
                    {
                        physicalStarted.TrySetResult();
                        await physicalRelease.Task;
                        throw expected;
                    },
                    waiter.Token);
                await physicalStarted.Task;

                waiter.Cancel();
                await ExpectCanceled(waiting);
                physicalRelease.TrySetResult();
                await UniTask.Yield();
                await UniTask.Yield();

                Assert.AreEqual(1, probe.MatchCount, "后台物理失败必须被观察且只记录一次");
                Assert.AreEqual(LogLevel.Error, probe.Entry.Level);
                Assert.AreEqual(nameof(YooPackageOperationCoordinator), probe.Entry.Category);
                Assert.AreSame(expected, probe.Entry.Exception);
                StringAssert.Contains("failed after all callers stopped waiting", probe.Entry.Message);
            }
            finally
            {
                Game.Framework.Logging.Log.ClearSinks();
                for (int i = 0; i < previousSinks.Count; i++)
                    Game.Framework.Logging.Log.AddSink(previousSinks[i]);
                Game.Framework.Logging.Log.MinLevel = previousMinLevel;
            }
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

            Assert.IsTrue(canceled, "调用者取消等待应保留 OperationCanceledException");
        }

        private static async UniTask ExpectCanceled<T>(UniTask<T> task)
        {
            bool canceled = false;
            try
            {
                _ = await task;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Assert.IsTrue(canceled, "调用者取消等待应保留 OperationCanceledException");
        }

        private static async UniTask<Exception> CaptureException(UniTask task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private sealed class ErrorProbeSink : ILogSink
        {
            private readonly object _gate = new();
            private readonly string _operationName;

            public ErrorProbeSink(string operationName) => _operationName = operationName;

            public LogLevel MinLevel => LogLevel.Trace;
            public int MatchCount { get; private set; }
            public LogEntry Entry { get; private set; }

            public void Log(in LogEntry entry)
            {
                if (entry.Level != LogLevel.Error ||
                    entry.Message == null ||
                    !entry.Message.Contains(_operationName))
                    return;

                lock (_gate)
                {
                    MatchCount++;
                    Entry = entry;
                }
            }
        }
    }
}
