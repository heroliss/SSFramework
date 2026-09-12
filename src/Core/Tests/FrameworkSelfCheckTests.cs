using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Framework.Context;
using Game.Framework.Diagnostics;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// PlayMode 生命周期测试（所属 asmdef 非 Editor-only）：用真实组件、Context 取消和 UniTask PlayerLoop
    /// 验证冒烟自检的每轮结果与释放边界。EditMode PreviewScene 不保证派发普通组件的 OnDestroy，不能替代此验证。
    /// </summary>
    public sealed class FrameworkSelfCheckTests
    {
        private GameObject _host;
        private FrameworkSelfCheck _check;
        private SummarySink _sink;
        private List<ILogSink> _previousSinks;
        private LogLevel _previousMinLevel;

        [SetUp]
        public void SetUp()
        {
            Assert.IsTrue(Application.isPlaying, "组件销毁回调必须在 PlayMode 验证，请从 Test Runner 的 PlayMode 运行本组测试。");
            _previousSinks = new List<ILogSink>(Log.Sinks);
            _previousMinLevel = Log.MinLevel;
            _sink = new SummarySink();
            // 消费工程可能关闭 Info 或移除 Console sink；用例自行建立观察条件，收尾恢复原配置。
            Log.ClearSinks();
            Log.AddSink(_sink);
            // 成功汇总由 SummarySink 断言；Console 只观察警告/错误，避免 NoUnexpectedReceived 把正常 Info 当成失败。
            Log.AddSink(new UnityDebugLogSink { MinLevel = LogLevel.Warning });
            Log.MinLevel = LogLevel.Info;
            _host = new GameObject(nameof(FrameworkSelfCheckTests));
            _check = _host.AddComponent<FrameworkSelfCheck>();
            // 用例显式控制 Run，避免 Unity 的 Start 在下一帧再启动一轮。
            _check.enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
            }
            finally
            {
                Log.ClearSinks();
                foreach (var sink in _previousSinks) Log.AddSink(sink);
                Log.MinLevel = _previousMinLevel;
            }
        }

        [UnityTest]
        public IEnumerator Run_OnlyReportsSuccessAfterAsyncChecksComplete()
        {
            Assert.IsFalse(_check.AllOk);
            Assert.IsFalse(_check.HasCompleted);
            _check.Run();

            Assert.IsTrue(_check.IsRunning);
            Assert.IsFalse(_check.AllOk, "同步项通过不代表异步命令已经通过。");
            Assert.IsFalse(_check.HasCompleted);
            Assert.AreEqual(6, _check.Results.Count);
            Assert.IsEmpty(_sink.Entries);

            yield return WaitForCompletion();
            AssertSuccessfulRound(1);
        }

        [UnityTest]
        public IEnumerator Run_WhilePreviousRoundIsPending_CancelsOldContextWithoutPublishingItsFailure()
        {
            _check.Run();
            var previous = CurrentContext();
            _check.Run();

            Assert.IsTrue(previous.IsDisposed);
            Assert.AreNotSame(previous, CurrentContext());
            Assert.IsFalse(_check.AllOk);

            yield return WaitForCompletion();
            AssertSuccessfulRound(1);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator Run_AfterCompletion_ResetsSuccessUntilTheNewRoundCompletes()
        {
            _check.Run();
            yield return WaitForCompletion();
            var previous = CurrentContext();
            AssertSuccessfulRound(1);

            _check.Run();
            Assert.IsTrue(previous.IsDisposed);
            Assert.IsTrue(_check.IsRunning);
            Assert.IsFalse(_check.HasCompleted);
            Assert.IsFalse(_check.AllOk);
            Assert.AreEqual(6, _check.Results.Count);

            yield return WaitForCompletion();
            AssertSuccessfulRound(2);
        }

        [UnityTest]
        public IEnumerator CurrentRoundCancellation_ReportsFailureInsteadOfHangingOrPassing()
        {
            _check.Run();
            // 当前轮的异常必须可见；只有因重跑/销毁而失效的旧轮才应静默退出。
            LogAssert.Expect(LogType.Error, new Regex(@"\[FrameworkSelfCheck\].*allOk=False"));
            CurrentContext().Dispose();

            yield return WaitForCompletion();
            Assert.IsFalse(_check.IsRunning);
            Assert.IsTrue(_check.HasCompleted);
            Assert.IsFalse(_check.AllOk);
            Assert.AreEqual(7, _check.Results.Count);
            StringAssert.StartsWith("✗ 异步命令", _check.Results[6]);
            Assert.AreEqual(1, _sink.Entries.Count);
            Assert.AreEqual(LogLevel.Error, _sink.Entries[0].Level);
        }

        [UnityTest]
        public IEnumerator Destroy_DuringRun_SuppressesLateResultsAndPreventsResurrection()
        {
            _check.Run();
            var previous = CurrentContext();
            var results = _check.Results;
            int resultCount = results.Count;
            UnityEngine.Object.DestroyImmediate(_host);

            Assert.IsTrue(previous.IsDisposed);
            Assert.IsFalse(_check.IsRunning);
            Assert.IsFalse(_check.HasCompleted);
            Assert.IsFalse(_check.AllOk);
            Assert.Throws<ObjectDisposedException>(() => _check.Run());

            // UniTask 的 Delay 在 PlayerLoop 观察取消；等待其最迟一次回调交付。
            double deadline = Time.realtimeSinceStartupAsDouble + 0.25;
            while (Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(resultCount, results.Count);
            Assert.IsEmpty(_sink.Entries);
            LogAssert.NoUnexpectedReceived();
        }

        private IEnumerator WaitForCompletion()
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!_check.HasCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.IsTrue(_check.HasCompleted, "自检应进入完成状态，不能永久显示运行中。");
        }

        private void AssertSuccessfulRound(int summaryCount)
        {
            Assert.IsFalse(_check.IsRunning);
            Assert.IsTrue(_check.HasCompleted);
            Assert.IsTrue(_check.AllOk);
            Assert.AreEqual(7, _check.Results.Count, "仅包含当前轮六项同步检查与一项异步检查。");
            Assert.AreEqual(summaryCount, _sink.Entries.Count);
            foreach (var result in _check.Results) StringAssert.StartsWith("✓", result);
            foreach (var entry in _sink.Entries) Assert.AreEqual(LogLevel.Info, entry.Level);
        }

        private GameContext CurrentContext() => (GameContext)typeof(FrameworkSelfCheck)
            .GetField("_ctx", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_check);

        private sealed class SummarySink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Info;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry)
            {
                if (entry.Category == nameof(FrameworkSelfCheck)) Entries.Add(entry);
            }
        }
    }
}
