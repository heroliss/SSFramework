using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Test
{
    /// <summary>
    /// UI Toolkit Adapter 的日志与 Context 绑定 Seam：无效窗口类型必须经统一日志失败，
    /// View 重复绑定则给出可行动的中文异常，而不是产生第二份生命周期所有权。
    /// </summary>
    public sealed class ToolkitRuntimeLoggingTests
    {
        private ILogSink[] _previousSinks;
        private LogLevel _previousMinLevel;
        private bool _previousCapture;
        private CapturingSink _sink;

        [SetUp]
        public void SetUp()
        {
            _previousSinks = Log.Sinks.ToArray();
            _previousMinLevel = Log.MinLevel;
            _previousCapture = Log.IsCapturingUnityLogs;

            Log.CaptureUnityLogs(false);
            Log.ClearSinks();
            Log.MinLevel = LogLevel.Info;
            _sink = new CapturingSink();
            Log.AddSink(_sink);
        }

        [TearDown]
        public void TearDown()
        {
            Log.CaptureUnityLogs(false);
            Log.ClearSinks();
            foreach (var sink in _previousSinks) Log.AddSink(sink);
            Log.MinLevel = _previousMinLevel;
            if (_previousCapture) Log.CaptureUnityLogs(true);
        }

        [Test]
        public void ToolkitBackend_RejectsNonToolkitWindow_ThroughLoggingSeam()
        {
            var documentObject = new GameObject("toolkit-log-probe", typeof(UIDocument));
            using var context = new GameContext(new ContainerBuilder().Build(), inheritFromGlobal: false);
            try
            {
                var backend = new ToolkitBackend(documentObject.GetComponent<UIDocument>());
                var result = backend.CreateWindow(
                    UIWindowMeta.Of(typeof(PlainWindow)), context, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert.IsNull(result);
                Assert.AreEqual(1, _sink.Entries.Count);
                Assert.AreEqual(LogLevel.Error, _sink.Entries[0].Level);
                Assert.AreEqual(nameof(ToolkitBackend), _sink.Entries[0].Category);
                StringAssert.Contains(nameof(UIToolkitWindowBase), _sink.Entries[0].Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(documentObject);
            }
        }

        [Test]
        public void ToolkitView_RejectsRepeatedContextBindingWithActionableChineseMessage()
        {
            using var first = new GameContext(new ContainerBuilder().Build(), inheritFromGlobal: false);
            using var second = new GameContext(new ContainerBuilder().Build(), inheritFromGlobal: false);
            var view = new BindingOnceToolkitView();
            try
            {
                view.BindTo(first);

                var error = Assert.Throws<InvalidOperationException>(() => view.BindTo(second));

                StringAssert.Contains(nameof(BindingOnceToolkitView), error.Message);
                StringAssert.Contains("不能重复绑定", error.Message);
                StringAssert.Contains("只能绑定一次", error.Message);
            }
            finally
            {
                view.Dispose();
            }
        }

        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        [UIWindow]
        private sealed class PlainWindow : IUIWindow
        {
            public void OnCreate() { }
            public void OnOpen(object args) { }
            public void OnClose() { }
            public void OnCover() { }
            public void OnReveal() { }
            public UniTask OnOpenTransition(CancellationToken ct) => UniTask.CompletedTask;
            public UniTask OnCloseTransition(CancellationToken ct) => UniTask.CompletedTask;
        }

        private sealed class BindingOnceToolkitView : UIToolkitViewBase
        {
            protected override void OnCreated() { }
        }
    }
}
