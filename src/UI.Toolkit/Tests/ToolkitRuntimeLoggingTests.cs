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
    /// UI Toolkit Adapter 的日志、创建事务与 Context 绑定 Seam：无效窗口类型必须经统一日志失败，
    /// 调用方取消不得留下可视树；View 重复绑定则给出可行动的中文异常，而不是产生第二份生命周期所有权。
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
            using var builder = new ContainerBuilder();
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
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
        public void ToolkitBackend_PreCancelledCreate_DoesNotCreateAWindowOrLogAnError()
        {
            var documentObject = new GameObject("toolkit-cancel-probe", typeof(UIDocument));
            using var builder = new ContainerBuilder();
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            var document = documentObject.GetComponent<UIDocument>();
            var backend = new ToolkitBackend(document);
            try
            {
                backend.Initialize();
                int layerRootCount = document.rootVisualElement.childCount;
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                Assert.Throws<OperationCanceledException>(() =>
                    backend.CreateWindow(
                            UIWindowMeta.Of(typeof(CancelProbeToolkitWindow)), context, cancellation.Token)
                        .GetAwaiter().GetResult());

                Assert.AreEqual(layerRootCount, document.rootVisualElement.childCount,
                    "已取消的创建只能保留 Initialize 建立的层根，不能留下窗口 VisualElement");
                Assert.AreEqual(0, _sink.Entries.Count, "生命周期取消是正常收口，不应记录成 Adapter Error");
            }
            finally
            {
                backend.Teardown();
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

        [Test]
        public void ToolkitView_BindFailureRollsBackAndPreservesThePrimaryException()
        {
            using var builder = new ContainerBuilder();
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            var parent = new VisualElement();
            var root = new VisualElement();
            parent.Add(root);
            var createFailure = new InvalidOperationException("create-probe");
            var cleanupFailure = new InvalidOperationException("cleanup-probe");
            var view = new FailingCreateToolkitView(createFailure, cleanupFailure);

            var error = Assert.Throws<InvalidOperationException>(() => view.BindTo(context, root));

            Assert.AreSame(createFailure, error, "回滚阶段的次生异常不能覆盖 OnCreated 根因。");
            Assert.IsTrue(view.IsDisposed, "绑定失败的半成品实例必须进入已释放状态，不能被误复用。");
            Assert.IsTrue(view.OwnedResourceDisposed,
                "OnCreated 已登记的 Bag 资源必须在 BindTo 返回失败前释放。");
            Assert.IsNull(root.parent, "失败时即使 Root 已在可视树中，也必须完成物理摘除。");
            Assert.Throws<ObjectDisposedException>(() => view.BindTo(context),
                "失败实例已经释放，后续调用应提示创建新实例。");

            Assert.AreEqual(1, _sink.Entries.Count,
                "回滚 hook 的次生失败应记录一次，同时不重复记录调用方仍会收到的创建根因。");
            Assert.AreEqual(nameof(UIToolkitViewBase), _sink.Entries[0].Category);
            Assert.AreSame(cleanupFailure, _sink.Entries[0].Exception);
            StringAssert.Contains("最初的绑定异常", _sink.Entries[0].Message);
        }

        [Test]
        public void ToolkitView_OnCreatedCannotCommitAfterSelfDisposal()
        {
            using var builder = new ContainerBuilder();
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            var parent = new VisualElement();
            var root = new VisualElement();
            parent.Add(root);
            var view = new SelfDisposingToolkitView();

            var error = Assert.Throws<InvalidOperationException>(() => view.BindTo(context, root));

            StringAssert.Contains(nameof(SelfDisposingToolkitView), error.Message);
            StringAssert.Contains("OnCreated", error.Message);
            StringAssert.Contains("无法返回可用 Root", error.Message);
            Assert.IsTrue(view.IsDisposed);
            Assert.IsNull(root.parent,
                "OnCreated 自行释放后不能让 BindTo 把已失去 Bag 的 Root 重新交给创建方挂载。");
            Assert.AreEqual(0, _sink.Entries.Count, "正常的幂等回滚不应产生次生错误日志。");
        }

        [Test]
        public void ToolkitView_ContextDisposalDoesNotReplaceOwnerDisposal()
        {
            using var builder = new ContainerBuilder();
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            var parent = new VisualElement();
            var view = new BindingOnceToolkitView();
            VisualElement root = view.BindTo(context);
            parent.Add(root);

            context.Dispose();

            Assert.IsFalse(view.IsDisposed,
                "Context 只提供借用的作用域能力，不接管独立 View 的物理生命周期。");
            Assert.AreSame(parent, root.parent,
                "Context 取消不会自动把独立 View 的 Root 摘出可视树。");

            view.Dispose();
            Assert.IsNull(root.parent, "创建 owner 仍须显式 Dispose 才完成物理清理。");
        }

        [Test]
        public void ToolkitView_OnDisposingFailure_StillDisposesBagAndDetachesRoot()
        {
            using var builder = new ContainerBuilder();
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            var parent = new VisualElement();
            var view = new FailingDisposeToolkitView();
            var root = view.BindTo(context);
            parent.Add(root);

            var error = Assert.Throws<InvalidOperationException>(() => view.Dispose());

            Assert.AreEqual("dispose-probe", error.Message);
            Assert.IsTrue(view.IsDisposed);
            Assert.IsTrue(view.OwnedResourceDisposed,
                "OnDisposing 失败不能截断 Bag 内订阅与资源的释放");
            Assert.IsNull(root.parent, "OnDisposing 失败后仍必须把 Root 从可视树摘除");
            Assert.DoesNotThrow(() => view.Dispose(), "重复释放保持幂等，不能再次执行失败 hook");
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

        [UIWindow]
        private sealed class CancelProbeToolkitWindow : UIToolkitWindowBase { }

        private sealed class FailingDisposeToolkitView : UIToolkitViewBase
        {
            internal bool OwnedResourceDisposed { get; private set; }

            protected override void OnCreated()
                => Bag.Add(new DisposeProbe(() => OwnedResourceDisposed = true));

            protected override void OnDisposing()
                => throw new InvalidOperationException("dispose-probe");
        }

        private sealed class FailingCreateToolkitView : UIToolkitViewBase
        {
            private readonly Exception _createFailure;
            private readonly Exception _cleanupFailure;

            internal FailingCreateToolkitView(Exception createFailure, Exception cleanupFailure)
            {
                _createFailure = createFailure;
                _cleanupFailure = cleanupFailure;
            }

            internal bool OwnedResourceDisposed { get; private set; }

            protected override void OnCreated()
            {
                Bag.Add(new DisposeProbe(() => OwnedResourceDisposed = true));
                throw _createFailure;
            }

            protected override void OnDisposing() => throw _cleanupFailure;
        }

        private sealed class SelfDisposingToolkitView : UIToolkitViewBase
        {
            protected override void OnCreated() => Dispose();
        }

        private sealed class DisposeProbe : IDisposable
        {
            private readonly Action _dispose;
            internal DisposeProbe(Action dispose) => _dispose = dispose;
            public void Dispose() => _dispose();
        }
    }
}
