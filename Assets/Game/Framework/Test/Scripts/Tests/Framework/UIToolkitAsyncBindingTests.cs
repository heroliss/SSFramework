using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
using Game.Framework.UI.Toolkit;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证 UI Toolkit 异步点击绑定的公共契约：点击任务由绑定观察、订阅生命周期提供取消令牌，
    /// Bag / 单独订阅释放都取消在途任务，预期取消静默，真正失败进入统一日志 Seam。
    /// </summary>
    public sealed class UIToolkitAsyncBindingTests
    {
        private static readonly MethodInfo InvokeClickable = typeof(Clickable).GetMethod(
            "Invoke",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private DisposableBag _bag;
        private Button _button;
        private CapturingSink _sink;
        private List<ILogSink> _previousSinks;
        private LogLevel _previousMinLevel;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(InvokeClickable, "当前 Unity 版本应提供 Clickable.Invoke，供测试模拟真实 Button.clicked");
            _bag = new DisposableBag();
            _button = new Button { name = "async-action", text = "异步动作" };
            _sink = new CapturingSink();
            _previousSinks = new List<ILogSink>(Log.Sinks);
            _previousMinLevel = Log.MinLevel;
            Log.ClearSinks();
            Log.AddSink(_sink);
            Log.MinLevel = LogLevel.Trace;
        }

        [TearDown]
        public void TearDown()
        {
            _bag.Dispose();
            Log.ClearSinks();
            foreach (var sink in _previousSinks) Log.AddSink(sink);
            Log.MinLevel = _previousMinLevel;
        }

        [Test]
        public void HandlerFailure_IsObservedAndSentToLoggingSeam()
        {
            _bag.SubscribeClickAsync(_button,
                _ => throw new InvalidOperationException("click-boom"));

            Click();

            Assert.AreEqual(1, _sink.Entries.Count);
            var entry = _sink.Entries[0];
            Assert.AreEqual(LogLevel.Error, entry.Level);
            Assert.AreEqual("UIBinding", entry.Category);
            StringAssert.Contains("async-action", entry.Message);
            Assert.AreEqual("click-boom", entry.Exception.Message);
        }

        [Test]
        public void BagDispose_CancelsInflightHandlerWithoutErrorLog()
        {
            var gate = new UniTaskCompletionSource();
            CancellationToken receivedToken = default;
            bool reachedFinally = false;
            _bag.SubscribeClickAsync(_button, async ct =>
            {
                receivedToken = ct;
                try { await gate.Task.AttachExternalCancellation(ct); }
                finally { reachedFinally = true; }
            });

            Click();
            Assert.IsFalse(receivedToken.IsCancellationRequested);

            _bag.Dispose();

            Assert.IsTrue(receivedToken.IsCancellationRequested);
            Assert.IsTrue(reachedFinally, "Bag 释放应让协作式异步点击及时离开等待");
            Assert.AreEqual(0, _sink.Entries.Count, "生命周期取消是正常收口，不应伪装成 Error");
        }

        [Test]
        public void SubscriptionDispose_CancelsInflightAndUnbindsFutureClicks()
        {
            var gate = new UniTaskCompletionSource();
            int starts = 0;
            bool canceled = false;
            var subscription = _bag.SubscribeClickAsync(_button, async ct =>
            {
                starts++;
                try { await gate.Task.AttachExternalCancellation(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { canceled = true; throw; }
            });

            Click();
            subscription.Dispose();
            Click();

            Assert.AreEqual(1, starts, "释放订阅后不能再接收后续点击");
            Assert.IsTrue(canceled, "单独释放订阅也应取消其已启动的点击任务");
            Assert.AreEqual(0, _sink.Entries.Count);
        }

        [Test]
        public void HandlerMayIgnoreViewToken_ButRemainsObservedAfterBagDispose()
        {
            var physicalOperation = new UniTaskCompletionSource();
            bool reachedPhysicalTerminal = false;
            _bag.SubscribeClickAsync(_button, async _ =>
            {
                await physicalOperation.Task;
                reachedPhysicalTerminal = true;
                throw new InvalidOperationException("late-physical-failure");
            });

            Click();
            _bag.Dispose();
            Assert.IsFalse(reachedPhysicalTerminal, "View 释放不能伪造物理操作已经结束");

            physicalOperation.TrySetResult();

            Assert.IsTrue(reachedPhysicalTerminal, "明确忽略 View token 的物理操作应能走到自己的终态");
            Assert.AreEqual(1, _sink.Entries.Count, "窗口消失后，绑定仍须观察物理操作的迟到失败");
            Assert.AreEqual("late-physical-failure", _sink.Entries[0].Exception.Message);
        }

        [Test]
        public void SubscribeAfterBagDisposed_DoesNotAttachHandler()
        {
            int starts = 0;
            _bag.Dispose();

            _bag.SubscribeClickAsync(_button, _ =>
            {
                starts++;
                return UniTask.CompletedTask;
            });
            Click();

            Assert.AreEqual(0, starts);
            Assert.AreEqual(0, _sink.Entries.Count);
        }

        private void Click() => InvokeClickable.Invoke(_button.clickable, new object[] { null });

        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }
    }
}
