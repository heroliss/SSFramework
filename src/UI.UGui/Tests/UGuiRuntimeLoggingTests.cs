using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.UI;
using Game.Framework.UI.UGui;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.Test
{
    /// <summary>
    /// UGUI Adapter 的日志 Seam 契约：配置错误必须在产生资源或层级副作用前 fail-fast，
    /// 并把 category、消息与 Unity context 交给 <see cref="ILogSink"/>，而不只写入当前 Editor Console。
    /// </summary>
    public sealed class UGuiRuntimeLoggingTests
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
        public void UGuiBackend_RejectsNonUGuiWindow_BeforeCreatingHierarchy()
        {
            var canvasObject = new GameObject("ugui-log-probe", typeof(RectTransform), typeof(Canvas));
            using var context = new GameContext(new ContainerBuilder().Build(), inheritFromGlobal: false);
            try
            {
                var backend = new UGuiBackend(canvasObject.GetComponent<Canvas>());
                var result = backend.CreateWindow(
                    UIWindowMeta.Of(typeof(NonUGuiMonoWindow)), context, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert.IsNull(result);
                Assert.AreEqual(0, canvasObject.transform.childCount,
                    "类型违反 Adapter Interface 时应在建层、加载 prefab 或创建窗口对象前失败");
                AssertSingleError(nameof(UGuiBackend), nameof(UGuiWindowBase));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void UGuiBindingError_CarriesWindowAsUnityContext()
        {
            var windowObject = new GameObject("binding-log-probe");
            windowObject.SetActive(false);
            try
            {
                var window = windowObject.AddComponent<BindingProbeWindow>();

                Assert.IsNull(window.FindMissingNode());

                Assert.AreEqual(1, _sink.Entries.Count);
                var entry = _sink.Entries[0];
                Assert.AreEqual(LogLevel.Error, entry.Level);
                Assert.AreEqual(nameof(BindingProbeWindow), entry.Category);
                Assert.AreSame(window, entry.Context,
                    "Console 双击/对象定位与外部 sink 应共享同一个窗口 context");
                StringAssert.Contains("Missing/Node", entry.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(windowObject);
            }
        }

        private void AssertSingleError(string category, string messagePart)
        {
            Assert.AreEqual(1, _sink.Entries.Count);
            Assert.AreEqual(LogLevel.Error, _sink.Entries[0].Level);
            Assert.AreEqual(category, _sink.Entries[0].Category);
            StringAssert.Contains(messagePart, _sink.Entries[0].Message);
        }

        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        [UIWindow]
        private sealed class NonUGuiMonoWindow : MonoBehaviour, IUIWindow
        {
            public void OnCreate() { }
            public void OnOpen(object args) { }
            public void OnClose() { }
            public void OnCover() { }
            public void OnReveal() { }
            public UniTask OnOpenTransition(CancellationToken ct) => UniTask.CompletedTask;
            public UniTask OnCloseTransition(CancellationToken ct) => UniTask.CompletedTask;
        }

        private sealed class BindingProbeWindow : UGuiWindowBase
        {
            public GameObject FindMissingNode() => BindGameObject("Missing/Node");
        }
    }
}
