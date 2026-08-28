using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Demo.Modules;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Game.Framework.Demo.Tests
{
    /// <summary>锁定启动更新中「新内容可用」与「旧缓存收尾」的失败边界。</summary>
    public sealed class AssetOpsFlowModuleTests
    {
        [UnityTest]
        public IEnumerator ReclaimUnusedCache_Success_ReportsCompletion()
            => VerifySuccessfulCleanup().ToCoroutine();

        [UnityTest]
        public IEnumerator ReclaimUnusedCache_Failure_IsRecoverableAndPreservesOriginalException()
            => VerifyRecoverableCleanupFailure().ToCoroutine();

        [UnityTest]
        public IEnumerator ReclaimUnusedCache_CallerCancellationWaitsForPhysicalSuccessBeforeThrowing()
            => VerifyCallerCancellationAfterPhysicalSuccess().ToCoroutine();

        [UnityTest]
        public IEnumerator ReclaimUnusedCache_PhysicalFailureAfterCallerCancellation_StillLogsFailure()
            => VerifyPhysicalFailureAfterCallerCancellation().ToCoroutine();

        [UnityTest]
        public IEnumerator ReclaimUnusedCache_PhysicalCancellation_PropagatesWithoutWarning()
            => VerifyPhysicalCancellation().ToCoroutine();

        [UnityTest]
        public IEnumerator ReclaimUnusedCache_PreCanceledCaller_DoesNotStartPhysicalCleanup()
            => VerifyPreCanceledCaller().ToCoroutine();

        private static async UniTask VerifySuccessfulCleanup()
        {
            var reports = new List<string>();
            bool clearCalled = false;

            await AssetOpsFlowModule.ReclaimUnusedCache(
                "Base",
                () =>
                {
                    clearCalled = true;
                    return UniTask.CompletedTask;
                },
                reports.Add,
                CancellationToken.None);

            Assert.IsTrue(clearCalled);
            Assert.AreEqual(1, reports.Count);
            StringAssert.Contains("旧版本缓存已回收", reports[0]);
        }

        private static async UniTask VerifyRecoverableCleanupFailure()
        {
            using var logCapture = new LogCaptureScope();
            var reports = new List<string>();
            var expected = new IOException("cache-in-use");

            await AssetOpsFlowModule.ReclaimUnusedCache(
                "Base",
                () => UniTask.FromException(expected),
                reports.Add,
                CancellationToken.None);

            Assert.AreEqual(1, reports.Count, "清理失败应返回可见的降级结果，而不是吞掉或终止启动。");
            StringAssert.Contains("新内容已可用", reports[0]);
            StringAssert.Contains("继续进入游戏", reports[0]);
            AssertSinglePhysicalFailure(logCapture.Sink, expected);
        }

        private static async UniTask VerifyCallerCancellationAfterPhysicalSuccess()
        {
            using var logCapture = new LogCaptureScope();
            var reports = new List<string>();
            var physicalCleanup = new UniTaskCompletionSource();
            using var caller = new CancellationTokenSource();
            UniTask running = AssetOpsFlowModule.ReclaimUnusedCache(
                "Base",
                () => physicalCleanup.Task,
                reports.Add,
                caller.Token);

            caller.Cancel();
            Assert.AreEqual(UniTaskStatus.Pending, running.Status,
                "页面取消只改变 caller 结果；waiter 必须保持在物理清理上，让 gate 覆盖收尾期。");
            physicalCleanup.TrySetResult();

            Assert.IsTrue(await ObserveCancellation(running), "物理清理到终态后，已取消的页面应观察到 OCE。");
            Assert.AreEqual(0, reports.Count, "不得向已失效的页面发布迟到结果。");
            Assert.AreEqual(0, logCapture.Sink.Entries.Count);
        }

        private static async UniTask VerifyPhysicalFailureAfterCallerCancellation()
        {
            using var logCapture = new LogCaptureScope();
            var reports = new List<string>();
            var physicalCleanup = new UniTaskCompletionSource();
            var expected = new IOException("late-cleanup-failure");
            using var caller = new CancellationTokenSource();
            UniTask running = AssetOpsFlowModule.ReclaimUnusedCache(
                "Base",
                () => physicalCleanup.Task,
                reports.Add,
                caller.Token);

            caller.Cancel();
            Assert.AreEqual(UniTaskStatus.Pending, running.Status);
            physicalCleanup.TrySetException(expected);

            Assert.IsTrue(await ObserveCancellation(running));
            Assert.AreEqual(0, reports.Count, "页面已取消时不发布「继续进游戏」这类迟到 UI 文案。");
            AssertSinglePhysicalFailure(logCapture.Sink, expected);
        }

        private static async UniTask VerifyPhysicalCancellation()
        {
            using var logCapture = new LogCaptureScope();
            var reports = new List<string>();
            using var owner = new CancellationTokenSource();
            owner.Cancel();

            bool canceled = await ObserveCancellation(AssetOpsFlowModule.ReclaimUnusedCache(
                "Base",
                () => UniTask.FromCanceled(owner.Token),
                reports.Add,
                CancellationToken.None));

            Assert.IsTrue(canceled, "AssetUtility / Context 物理 owner 取消必须保持 OCE，不能降级成旧缓存警告。");
            Assert.AreEqual(0, reports.Count);
            Assert.AreEqual(0, logCapture.Sink.Entries.Count);
        }

        private static async UniTask VerifyPreCanceledCaller()
        {
            using var logCapture = new LogCaptureScope();
            var reports = new List<string>();
            using var caller = new CancellationTokenSource();
            caller.Cancel();
            bool clearCalled = false;

            bool canceled = await ObserveCancellation(AssetOpsFlowModule.ReclaimUnusedCache(
                "Base",
                () =>
                {
                    clearCalled = true;
                    return UniTask.CompletedTask;
                },
                reports.Add,
                caller.Token));

            Assert.IsTrue(canceled);
            Assert.IsFalse(clearCalled, "调用方在入口前已取消时，不得提交新的磁盘维护。");
            Assert.AreEqual(0, reports.Count);
            Assert.AreEqual(0, logCapture.Sink.Entries.Count);
        }

        private static async UniTask<bool> ObserveCancellation(UniTask operation)
        {
            try
            {
                await operation;
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }

        private static void AssertSinglePhysicalFailure(CapturingSink sink, Exception expected)
        {
            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(LogLevel.Warning, sink.Entries[0].Level);
            Assert.AreEqual(nameof(AssetOpsFlowModule), sink.Entries[0].Category);
            Assert.AreSame(expected, sink.Entries[0].Exception,
                "可恢复失败仍必须保留原始异常，供文件或遥测 sink 诊断。");
        }

        private sealed class CapturingSink : ILogSink
        {
            internal readonly List<LogEntry> Entries = new();
            public LogLevel MinLevel => LogLevel.Trace;
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        private sealed class LogCaptureScope : IDisposable
        {
            private readonly List<ILogSink> _previousSinks = new(Log.Sinks);
            private readonly LogLevel _previousMinLevel = Log.MinLevel;
            internal readonly CapturingSink Sink = new();

            internal LogCaptureScope()
            {
                Log.ClearSinks();
                Log.AddSink(Sink);
                Log.MinLevel = LogLevel.Trace;
            }

            public void Dispose()
            {
                Log.ClearSinks();
                foreach (var previousSink in _previousSinks) Log.AddSink(previousSink);
                Log.MinLevel = _previousMinLevel;
            }
        }
    }
}
