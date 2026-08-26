using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Framework.UI.UGui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace Game.Framework.Test
{
    /// <summary>
    /// UI 框架核心编排（<see cref="UIUtility"/>）的纯逻辑测试：栈/层、cover-reveal、模态遮罩调度、缓存复用、关闭策略。
    /// 用 fake <see cref="IUIBackend"/>（只记录调用、不碰 Unity）+ 真实空 <see cref="GameContext"/>，
    /// 脱离场景验证渲染中立的编排逻辑——印证"核心可单测"。
    /// </summary>
    public class UIWindowStackTests
    {
        private GameContext _ctx;
        private FakeBackend _backend;
        private UIUtility _ui;
        private CapturingSink _logSink;

        [SetUp]
        public void SetUp()
        {
            _ctx = new GameContext(new ContainerBuilder().Build(), inheritFromGlobal: false);
            _backend = new FakeBackend();
            _ui = new UIUtility(_ctx, _backend);
            _logSink = new CapturingSink();
            Log.AddSink(_logSink);
        }

        [TearDown]
        public void TearDown()
        {
            Log.RemoveSink(_logSink);
            _ui.Dispose();
            _ctx.Dispose();
        }

        // fake backend 同步返回，UniTask 立即完成，可直接取结果。
        private T Open<T>(object args = null) where T : class, IUIWindow
            => _ui.Open<T>(args).GetAwaiter().GetResult();

        [Test]
        public void Open_FirstTime_InitializesAndRunsCreateThenOpen()
        {
            var w = Open<PageA>("hi");

            Assert.IsNotNull(w);
            Assert.IsTrue(_ui.IsOpen<PageA>());
            Assert.AreSame(w, _ui.Get<PageA>());
            CollectionAssert.AreEqual(new[] { "create", "open:hi" }, w.Calls);
            Assert.AreEqual(1, _backend.Count("init"));
        }

        [Test]
        public void Open_SameTypeTwice_BringsToFront_NoRecreate()
        {
            var w1 = Open<PageA>();
            var w2 = Open<PageA>();

            Assert.AreSame(w1, w2);
            Assert.AreEqual(1, _backend.Count("create:PageA")); // 只建一次
            Assert.AreEqual(1, _backend.Count("front:PageA"));  // 第二次置顶
        }

        [Test]
        public void ConcurrentOpen_SameType_SharesSingleCreation()
        {
            // 用可挂起的 backend 模拟真实异步加载窗口期：第二个 Open 在第一个创建完成前发起。
            var backend = new GatedBackend();
            var ui = new UIUtility(_ctx, backend);

            var t1 = ui.Open<PageA>("first");
            var t2 = ui.Open<PageA>("second");
            Assert.AreEqual(UniTaskStatus.Pending, t1.Status, "创建被闸住时 Open 不应完成");

            backend.Release(); // 放行创建

            var w1 = t1.GetAwaiter().GetResult();
            var w2 = t2.GetAwaiter().GetResult();

            Assert.AreSame(w1, w2, "并发 Open 同类型应复用同一次创建");
            Assert.AreEqual(1, backend.Count("create:PageA"), "backend 只应创建一次实例");
            Assert.AreEqual(1, w1.Calls.Count(c => c == "create"), "OnCreate 只应执行一次");
            Assert.AreEqual(2, w1.Calls.Count(c => c.StartsWith("open")), "两次 Open 各触发一次 OnOpen");
            ui.Dispose();
        }

        [Test]
        public void SameLayer_OpenSecond_CoversFirst()
        {
            var a = Open<PageA>();
            var b = Open<PageB>();

            Assert.IsTrue(a.Calls.Contains("cover"), "前一个栈顶应收到 OnCover");
            Assert.IsFalse(b.Calls.Contains("cover"), "新栈顶不应被 cover");
        }

        [Test]
        public void Back_ClosesTopPage_RevealsPrevious()
        {
            var a = Open<PageA>();
            var b = Open<PageB>();

            _ui.Back(); // 关 Page 层栈顶 = B

            Assert.IsTrue(b.Calls.Contains("close"));
            Assert.AreEqual(1, _backend.Count("destroy:PageB"));
            Assert.IsTrue(a.Calls.Contains("reveal"), "新栈顶应收到 OnReveal");
            Assert.IsFalse(_ui.IsOpen<PageB>());
            Assert.IsTrue(_ui.IsOpen<PageA>());
        }

        [Test]
        public void Modal_TogglesMaskOnOpenAndClose()
        {
            Open<ModalPopup>();
            Assert.AreEqual(1, _backend.Count("mask:ModalPopup:True"));

            _ui.Close<ModalPopup>();
            Assert.AreEqual(1, _backend.Count("mask:ModalPopup:False"));
        }

        [Test]
        public void CachePolicy_CloseHides_ReopenReusesSameInstance()
        {
            var w1 = Open<CachedWindow>();
            _ui.Close<CachedWindow>();

            Assert.IsFalse(_ui.IsOpen<CachedWindow>());
            Assert.AreEqual(1, _backend.Count("visible:CachedWindow:False")); // 隐藏
            Assert.AreEqual(0, _backend.Count("destroy:CachedWindow"));       // 不销毁

            var w2 = Open<CachedWindow>();
            Assert.AreSame(w1, w2, "缓存窗口应复用同一实例");
            Assert.AreEqual(1, _backend.Count("create:CachedWindow"));        // 仍只建一次
            Assert.AreEqual(1, _backend.Count("visible:CachedWindow:True"));  // 重新显示
            Assert.AreEqual(1, w2.Calls.Count(c => c == "create"));           // OnCreate 只一次
            Assert.AreEqual(2, w2.Calls.Count(c => c.StartsWith("open")));    // OnOpen 两次
        }

        [Test]
        public void DestroyPolicy_Close_DestroysAndReleases()
        {
            Open<PageA>();
            _ui.Close<PageA>();

            Assert.AreEqual(1, _backend.Count("destroy:PageA"));
            Assert.IsFalse(_ui.IsOpen<PageA>());
        }

        [Test]
        public void CloseAll_ClosesEveryLayer()
        {
            Open<PageA>();
            Open<ModalPopup>();
            Open<CachedWindow>();

            _ui.CloseAll();

            Assert.IsFalse(_ui.IsOpen<PageA>());
            Assert.IsFalse(_ui.IsOpen<ModalPopup>());
            Assert.IsFalse(_ui.IsOpen<CachedWindow>());
        }

        [Test]
        public void CloseAll_SuppressesIntermediateReveals()
        {
            var a = Open<PageA>();
            var b = Open<PageB>(); // a 被盖 cover

            _ui.CloseAll(UILayer.Page); // 先关 b、再关 a——a 不应先 reveal 再立即被关

            Assert.IsFalse(a.Calls.Contains("reveal"), "批量关闭时中间窗口不应收到 OnReveal");
            Assert.IsTrue(a.Calls.Contains("close"));
            Assert.IsTrue(b.Calls.Contains("close"));
        }

        [Test]
        public void CloseAll_OfOneLayer_LeavesOtherLayers()
        {
            Open<PageA>();
            Open<ModalPopup>(); // Popup 层

            _ui.CloseAll(UILayer.Page);

            Assert.IsFalse(_ui.IsOpen<PageA>());
            Assert.IsTrue(_ui.IsOpen<ModalPopup>(), "只关 Page 层不应影响 Popup 层");
        }

        [Test]
        public void Reopen_LowerWindow_RevealsItself_AndCoversPreviousTop()
        {
            var a = Open<PageA>();
            var b = Open<PageB>(); // b 盖住 a → a.cover
            Open<PageA>();          // 重开下方的 a → a 重新置顶（不重建）

            Assert.AreEqual(1, _backend.Count("create:PageA"), "重开不应再建实例");
            Assert.AreEqual(1, _backend.Count("front:PageA"), "重开应置顶");
            Assert.IsTrue(a.Calls.Contains("reveal"), "重开后自己应 OnReveal");
            Assert.AreEqual(1, b.Calls.Count(c => c == "cover"), "原栈顶 b 应被盖 OnCover");
        }

        [Test]
        public void HookThrows_IsIsolated_StackStaysConsistent()
        {
            LogAssert.Expect(LogType.Error, new Regex("ThrowingOnOpen.*OnOpen.*异常已隔离"));
            LogAssert.Expect(LogType.Exception, new Regex("boom"));

            var w = Open<ThrowingOnOpen>(); // OnOpen 抛异常，但不应让 Open 抛出
            Assert.IsNotNull(w);
            Assert.IsTrue(_ui.IsOpen<ThrowingOnOpen>(), "hook 抛异常不应阻止窗口入栈");

            var entry = _logSink.Entries.Single(e => e.Exception is InvalidOperationException);
            Assert.AreEqual(LogLevel.Error, entry.Level);
            Assert.AreEqual(nameof(UIUtility), entry.Category);
            StringAssert.Contains(nameof(IUIWindow.OnOpen), entry.Message,
                "文件/遥测 sink 也应知道失败的是哪个生命周期 hook，而不只收到一条裸异常");

            // 内部状态未被污染：后续开/关仍正常。
            Open<PageA>();
            Assert.IsTrue(_ui.IsOpen<PageA>());
            _ui.Close<PageA>();
            Assert.IsFalse(_ui.IsOpen<PageA>());
        }

        // ── Toast / Loading 内置件（类型表见 ADR-0020；Loading 所有权见 ADR-0037）──

        [Test]
        public void ShowToast_OpensRegisteredType_WithArgs()
        {
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });

            ui.ShowToast("hello", 1.5f).GetAwaiter().GetResult();

            var toast = ui.Get<ToastFake>();
            Assert.IsNotNull(toast, "ShowToast 应打开注册的 Toast 类型");
            var args = toast.LastArgs as UIToastArgs;
            Assert.IsNotNull(args, "OnOpen 应收到 UIToastArgs");
            Assert.AreEqual("hello", args.Text);
            Assert.AreEqual(1.5f, args.Duration);
            ui.Dispose();
        }

        [Test]
        public void ShowLoading_ThenHide_OpensAndCloses()
        {
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });

            ui.ShowLoading("加载中").GetAwaiter().GetResult();
            Assert.IsTrue(ui.IsOpen<LoadingFake>());
            Assert.AreEqual("加载中", (ui.Get<LoadingFake>().LastArgs as UILoadingArgs)?.Text);

            ui.HideLoading();
            Assert.IsFalse(ui.IsOpen<LoadingFake>());
            ui.Dispose();
        }

        [Test]
        public void ShowLoading_PreCancelledRefresh_PreservesExistingLegacyOwner()
        {
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });
            ui.ShowLoading("既有任务").GetAwaiter().GetResult();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                ui.ShowLoading("取消的刷新", cts.Token).GetAwaiter().GetResult());
            Assert.IsTrue(ui.IsOpen<LoadingFake>(), "刷新失败不能释放此前已经建立的 legacy owner");
            Assert.AreEqual("既有任务", (ui.Get<LoadingFake>().LastArgs as UILoadingArgs)?.Text);

            ui.HideLoading();
            Assert.IsFalse(ui.IsOpen<LoadingFake>());
            ui.Dispose();
        }

        [Test]
        public void ShowLoading_CancelledConcurrentRefresh_PreservesPendingLegacyOwner()
        {
            var backend = new GatedBackend();
            var ui = new UIUtility(_ctx, backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });
            using var refreshCts = new CancellationTokenSource();

            var first = ui.ShowLoading("仍有效");
            var cancelledRefresh = ui.ShowLoading("将取消", refreshCts.Token);
            refreshCts.Cancel();
            Assert.Throws<OperationCanceledException>(() => cancelledRefresh.GetAwaiter().GetResult());
            Assert.AreEqual(UniTaskStatus.Pending, first.Status,
                "一个刷新取消后，另一个 pending Show 仍应保有 legacy owner 请求");

            backend.Release();
            first.GetAwaiter().GetResult();
            Assert.IsTrue(ui.IsOpen<LoadingFake>());
            Assert.AreEqual("仍有效", (ui.Get<LoadingFake>().LastArgs as UILoadingArgs)?.Text);

            ui.HideLoading();
            Assert.IsFalse(ui.IsOpen<LoadingFake>());
            ui.Dispose();
        }

        [Test]
        public void AcquireLoading_OverlappingHandles_CloseOnlyAfterLastOwner()
        {
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });

            var first = ui.AcquireLoading("任务 A").GetAwaiter().GetResult();
            var second = ui.AcquireLoading("任务 B").GetAwaiter().GetResult();

            Assert.IsTrue(first.IsActive);
            Assert.IsTrue(second.IsActive);
            Assert.AreEqual(1, _backend.Count("create:LoadingFake"), "并发 owner 应复用同一个 Loading 窗口");
            Assert.AreEqual("任务 B", (ui.Get<LoadingFake>().LastArgs as UILoadingArgs)?.Text,
                "共享窗口沿用最后一次占用刷新出的提示文本");

            first.Dispose();
            Assert.IsFalse(first.IsActive);
            Assert.IsTrue(second.IsActive);
            Assert.IsTrue(ui.IsOpen<LoadingFake>(), "较早任务结束不能关闭仍被另一任务占用的 Loading");

            Assert.DoesNotThrow(() => first.Dispose(), "重复释放必须安全 no-op");
            Assert.DoesNotThrow(() => default(LoadingHandle).Dispose(), "default handle 必须安全 no-op");
            second.Dispose();
            Assert.IsFalse(ui.IsOpen<LoadingFake>(), "最后一个 owner 释放后才关闭 Loading");
            ui.Dispose();
        }

        [Test]
        public void LegacyLoadingOwner_AndHandles_DoNotCloseEachOther()
        {
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });

            ui.ShowLoading("旧调用").GetAwaiter().GetResult();
            var handle = ui.AcquireLoading("lease").GetAwaiter().GetResult();
            ui.HideLoading();
            Assert.IsTrue(ui.IsOpen<LoadingFake>(), "Hide 只释放兼容 owner，不能越权关闭 active lease");
            handle.Dispose();
            Assert.IsFalse(ui.IsOpen<LoadingFake>());

            handle = ui.AcquireLoading("lease 2").GetAwaiter().GetResult();
            ui.ShowLoading("旧调用 2").GetAwaiter().GetResult();
            handle.Dispose();
            Assert.IsTrue(ui.IsOpen<LoadingFake>(), "lease 释放也不能越权关闭仍有效的兼容 owner");
            ui.HideLoading();
            Assert.IsFalse(ui.IsOpen<LoadingFake>());
            ui.Dispose();
        }

        [Test]
        public void AcquireLoading_StaleHandleAfterCloseAll_CannotCloseNewLoading()
        {
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });

            var stale = ui.AcquireLoading("旧任务").GetAwaiter().GetResult();
            ui.CloseAll(UILayer.Top);
            Assert.IsFalse(stale.IsActive);
            Assert.IsFalse(ui.IsOpen<LoadingFake>());

            var current = ui.AcquireLoading("新任务").GetAwaiter().GetResult();
            stale.Dispose();
            Assert.IsTrue(current.IsActive);
            Assert.IsTrue(ui.IsOpen<LoadingFake>(), "清场前的陈旧句柄不能误关清场后新建的 Loading");

            current.Dispose();
            Assert.IsFalse(ui.IsOpen<LoadingFake>());
            ui.Dispose();
        }

        [Test]
        public void AcquireLoading_CloseAllDuringCreation_ReturnsStaleSafeDefaultAndNoGhost()
        {
            var backend = new NonCooperativeGatedBackend();
            var ui = new UIUtility(_ctx, backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });

            var pending = ui.AcquireLoading("创建中");
            Assert.AreEqual(UniTaskStatus.Pending, pending.Status);
            ui.CloseAll(UILayer.Top);
            backend.Release();

            var stale = pending.GetAwaiter().GetResult();
            Assert.IsFalse(stale.IsActive, "清场期间完成的 Acquire 不得把陈旧 owner 交给调用方");
            Assert.IsFalse(ui.IsOpen<LoadingFake>(), "创建途中 CloseAll 后不应留下迟到出现的 Loading");
            Assert.DoesNotThrow(() => stale.Dispose());
            ui.Dispose();
        }

        [Test]
        public void AcquireLoading_FirstCreatorCancelled_DoesNotReleaseWaitingOwner()
        {
            var backend = new GatedBackend();
            var ui = new UIUtility(_ctx, backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });
            using var firstCts = new CancellationTokenSource();

            var first = ui.AcquireLoading("先发起", firstCts.Token);
            var waiting = ui.AcquireLoading("仍需要 Loading");
            Assert.AreEqual(UniTaskStatus.Pending, first.Status);
            Assert.AreEqual(UniTaskStatus.Pending, waiting.Status);

            firstCts.Cancel();
            Assert.Throws<OperationCanceledException>(() => first.GetAwaiter().GetResult());
            Assert.AreEqual(UniTaskStatus.Pending, waiting.Status,
                "首个创建者取消后，另一个 owner 应接手创建而不是被一起释放");

            backend.Release();
            var waitingHandle = waiting.GetAwaiter().GetResult();
            Assert.IsTrue(waitingHandle.IsActive);
            Assert.IsTrue(ui.IsOpen<LoadingFake>());
            Assert.AreEqual("仍需要 Loading", (ui.Get<LoadingFake>().LastArgs as UILoadingArgs)?.Text);

            waitingHandle.Dispose();
            Assert.IsFalse(ui.IsOpen<LoadingFake>());
            ui.Dispose();
        }

        [Test]
        public void ShowToast_WithoutBuiltins_LogsError_NoThrow()
        {
            LogAssert.Expect(LogType.Error, new Regex("Toast"));
            // SetUp 建的 _ui 未注册内置件表：应报错提示、不抛异常。
            Assert.DoesNotThrow(() => _ui.ShowToast("x").GetAwaiter().GetResult());
        }

        [Test]
        public void ShowToast_PreCancelledToken_DoesNotOpenBuiltin()
        {
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                ui.ShowToast("late", ct: cts.Token).GetAwaiter().GetResult());
            Assert.IsFalse(ui.IsOpen<ToastFake>());
            Assert.AreEqual(0, _backend.Count("create:ToastFake"));
            ui.Dispose();
        }

        [Test]
        public void ShowLoading_CancelledDuringCreation_DoesNotAppearLater()
        {
            var backend = new GatedBackend();
            var ui = new UIUtility(_ctx, backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });
            using var cts = new CancellationTokenSource();

            var opening = ui.ShowLoading("late", cts.Token);
            Assert.AreEqual(UniTaskStatus.Pending, opening.Status);
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => opening.GetAwaiter().GetResult());
            Assert.IsFalse(ui.IsOpen<LoadingFake>());
            backend.Release();
            ui.Dispose();
        }

        [Test]
        public void ShowLoading_NonCooperativeBackendReturningAfterCancellation_DestroysLateWindow()
        {
            var backend = new NonCooperativeGatedBackend();
            var ui = new UIUtility(_ctx, backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });
            using var cts = new CancellationTokenSource();

            var opening = ui.ShowLoading("late", cts.Token);
            Assert.AreEqual(UniTaskStatus.Pending, opening.Status);
            cts.Cancel();
            backend.Release();

            Assert.Throws<OperationCanceledException>(() => opening.GetAwaiter().GetResult());
            Assert.IsFalse(ui.IsOpen<LoadingFake>());
            Assert.AreEqual(1, backend.Count("destroy:LoadingFake"),
                "不遵守 token 的后端迟到返回时，核心仍必须物理销毁窗口。 ");
            ui.Dispose();
        }

        [Test]
        public void MonoAdapters_ForwardBuiltinCancellationTokensAndLoadingHandles()
        {
            var uguiCore = new UIUtility(_ctx, new FakeBackend(), new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });
            var toolkitCore = new UIUtility(_ctx, new FakeBackend(), new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });
            var uguiObject = new GameObject("UGui adapter cancellation test");
            var toolkitObject = new GameObject("Toolkit adapter cancellation test");
            uguiObject.SetActive(false);
            toolkitObject.SetActive(false);
            var ugui = uguiObject.AddComponent<MonoUGuiUI>();
            var toolkit = toolkitObject.AddComponent<MonoToolkitUI>();
            FieldInfo uguiCoreField = typeof(MonoUGuiUI).GetField("_core", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo toolkitCoreField = typeof(MonoToolkitUI).GetField("_core", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(uguiCoreField);
            Assert.IsNotNull(toolkitCoreField);
            uguiCoreField.SetValue(ugui, uguiCore);
            toolkitCoreField.SetValue(toolkit, toolkitCore);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try
            {
                Assert.Throws<OperationCanceledException>(() =>
                    ugui.ShowToast("late", ct: cts.Token).GetAwaiter().GetResult());
                Assert.Throws<OperationCanceledException>(() =>
                    ugui.ShowLoading("late", cts.Token).GetAwaiter().GetResult());
                Assert.Throws<OperationCanceledException>(() =>
                    toolkit.ShowToast("late", ct: cts.Token).GetAwaiter().GetResult());
                Assert.Throws<OperationCanceledException>(() =>
                    toolkit.ShowLoading("late", cts.Token).GetAwaiter().GetResult());

                Assert.Throws<OperationCanceledException>(() =>
                    ugui.AcquireLoading("late", cts.Token).GetAwaiter().GetResult());
                Assert.Throws<OperationCanceledException>(() =>
                    toolkit.AcquireLoading("late", cts.Token).GetAwaiter().GetResult());

                var uguiHandle = ugui.AcquireLoading("UGUI owner").GetAwaiter().GetResult();
                var toolkitHandle = toolkit.AcquireLoading("Toolkit owner").GetAwaiter().GetResult();
                Assert.IsTrue(uguiHandle.IsActive);
                Assert.IsTrue(toolkitHandle.IsActive);
                uguiHandle.Dispose();
                toolkitHandle.Dispose();
                Assert.IsFalse(uguiCore.IsOpen<LoadingFake>());
                Assert.IsFalse(toolkitCore.IsOpen<LoadingFake>());
            }
            finally
            {
                // 先摘掉注入的测试核心，避免组件 OnDestroy 再 Dispose；核心与 GameObject 各自只释放一次。
                uguiCoreField.SetValue(ugui, null);
                toolkitCoreField.SetValue(toolkit, null);
                uguiCore.Dispose();
                toolkitCore.Dispose();
                UnityEngine.Object.DestroyImmediate(uguiObject);
                UnityEngine.Object.DestroyImmediate(toolkitObject);
            }
        }

        // ── fakes ────────────────────────────────────────────────────────────

        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        private class FakeWindow : IUIWindow
        {
            public readonly List<string> Calls = new();
            public virtual void OnCreate() => Calls.Add("create");
            public virtual void OnOpen(object args) => Calls.Add("open:" + (args ?? string.Empty));
            public virtual void OnClose() => Calls.Add("close");
            public virtual void OnCover() => Calls.Add("cover");
            public virtual void OnReveal() => Calls.Add("reveal");
            public virtual UniTask OnOpenTransition(CancellationToken ct) => UniTask.CompletedTask;
            public virtual UniTask OnCloseTransition(CancellationToken ct) => UniTask.CompletedTask;
        }

        [UIWindow(Layer = UILayer.Page)] private class PageA : FakeWindow { }
        [UIWindow(Layer = UILayer.Page)] private class PageB : FakeWindow { }
        [UIWindow(Layer = UILayer.Popup, Modal = true)] private class ModalPopup : FakeWindow { }
        [UIWindow(Layer = UILayer.Window, Cache = UICachePolicy.Cache)] private class CachedWindow : FakeWindow { }
        // OnOpen 抛异常，验证 hook 异常被框架隔离。
        [UIWindow(Layer = UILayer.Window)] private class ThrowingOnOpen : FakeWindow
        {
            public override void OnOpen(object args) => throw new InvalidOperationException("boom");
        }

        // 记录 OnOpen 参数：验证内置件转发（ShowToast/AcquireLoading/ShowLoading 的 args 形态）。
        private class ArgsRecordingWindow : FakeWindow
        {
            public object LastArgs;
            public override void OnOpen(object args) { LastArgs = args; base.OnOpen(args); }
        }

        [UIWindow(Layer = UILayer.Top, Cache = UICachePolicy.Cache)] private class ToastFake : ArgsRecordingWindow { }
        [UIWindow(Layer = UILayer.Top, Cache = UICachePolicy.Cache, Modal = true)] private class LoadingFake : ArgsRecordingWindow { }

        // 只记录调用序列，不碰 Unity——验证核心编排逻辑。
        private class FakeBackend : IUIBackend
        {
            public readonly List<string> Log = new();
            public int Count(string entry) => Log.Count(x => x == entry);

            public void Initialize() => Log.Add("init");

            public virtual UniTask<IUIWindow> CreateWindow(UIWindowMeta meta, IGameContext context, CancellationToken ct)
            {
                var w = (IUIWindow)Activator.CreateInstance(meta.WindowType);
                Log.Add("create:" + meta.WindowType.Name);
                return UniTask.FromResult(w);
            }

            public void BringToFront(IUIWindow window) => Log.Add("front:" + window.GetType().Name);
            public void SetVisible(IUIWindow window, bool visible) => Log.Add("visible:" + window.GetType().Name + ":" + visible);
            public void SetModalMask(IUIWindow ownerWindow, bool on) => Log.Add("mask:" + ownerWindow.GetType().Name + ":" + on);
            public void DestroyWindow(IUIWindow window) => Log.Add("destroy:" + window.GetType().Name);
            public void SetInputBlocked(bool blocked) => Log.Add("block:" + blocked);
            public void Teardown() => Log.Add("teardown");
        }

        // CreateWindow 挂起直到显式放行——制造「异步创建进行中」窗口期，验证并发 Open 只创建一次。
        private class GatedBackend : FakeBackend
        {
            private readonly UniTaskCompletionSource _gate = new();
            public void Release() => _gate.TrySetResult();

            public override async UniTask<IUIWindow> CreateWindow(UIWindowMeta meta, IGameContext context, CancellationToken ct)
            {
                await _gate.Task.AttachExternalCancellation(ct);
                return await base.CreateWindow(meta, context, ct);
            }
        }

        // 故意不观察 token，模拟第三方/遗留 adapter 已经创建完才返回；核心的迟到保护不能依赖后端自觉。
        private class NonCooperativeGatedBackend : FakeBackend
        {
            private readonly UniTaskCompletionSource _gate = new();
            public void Release() => _gate.TrySetResult();

            public override async UniTask<IUIWindow> CreateWindow(UIWindowMeta meta, IGameContext context, CancellationToken ct)
            {
                await _gate.Task;
                return await base.CreateWindow(meta, context, CancellationToken.None);
            }
        }
    }
}
