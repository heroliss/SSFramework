using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.UI;
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

        /// <summary>
        /// 不观察 token 的手动 Toast 时钟：故意允许旧 delay 在 owner 取消后迟到成功，直接验证
        /// UIUtility 的 owner identity，而不是依赖 Editor 前台帧率与几十毫秒墙钟窗口。
        /// </summary>
        private sealed class ManualToastDelay
        {
            internal sealed class Request
            {
                private readonly UniTaskCompletionSource _completion = new();

                public Request(TimeSpan duration) => Duration = duration;
                public TimeSpan Duration { get; }
                public UniTask Task => _completion.Task;
                public bool Complete() => _completion.TrySetResult();
            }

            public readonly List<Request> Requests = new();

            public UniTask Wait(TimeSpan duration, CancellationToken _)
            {
                var request = new Request(duration);
                Requests.Add(request);
                return request.Task;
            }
        }

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
        public void Dispose_AllPublicIntentsFailFast_WhileDisposeRemainsIdempotent()
        {
            _ui.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ui.Open<PageA>().GetAwaiter().GetResult());
            Assert.Throws<ObjectDisposedException>(() => _ui.Open<PageA>("args").GetAwaiter().GetResult());
            Assert.Throws<ObjectDisposedException>(() => _ui.Close<PageA>());
            Assert.Throws<ObjectDisposedException>(() => _ui.Close((IUIWindow)null));
            Assert.Throws<ObjectDisposedException>(() => _ui.CloseTop(UILayer.Page));
            Assert.Throws<ObjectDisposedException>(() => _ui.Back());
            Assert.Throws<ObjectDisposedException>(() => _ui.CloseAll(UILayer.Page));
            Assert.Throws<ObjectDisposedException>(() => _ui.CloseAll());
            Assert.Throws<ObjectDisposedException>(() => _ui.Get<PageA>());
            Assert.Throws<ObjectDisposedException>(() => _ui.IsOpen<PageA>());
            Assert.Throws<ObjectDisposedException>(() => _ui.ShowToast("toast").GetAwaiter().GetResult());
            Assert.Throws<ObjectDisposedException>(() => _ui.AcquireLoading("loading").GetAwaiter().GetResult());
#pragma warning disable CS0618 // 有意验证迁移期旧入口也遵守统一终态；生产代码不得调用。
            Assert.Throws<ObjectDisposedException>(() => _ui.ShowLoading("legacy").GetAwaiter().GetResult());
            Assert.Throws<ObjectDisposedException>(() => _ui.HideLoading());
#pragma warning restore CS0618
            Assert.DoesNotThrow(() => _ui.Dispose(), "Dispose 本身仍须幂等，便于 owner 清理链重复收口");
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

        [UnityTest]
        public IEnumerator ShowToast_RepeatedCall_OnlyLatestTimerCanClose() => UniTask.ToCoroutine(async () =>
        {
            var timer = new ManualToastDelay();
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) }, timer.Wait);

            await ui.ShowToast("first", 0.1f);
            await ui.ShowToast("second", 0.25f);
            Assert.AreEqual(2, timer.Requests.Count);
            Assert.AreEqual(TimeSpan.FromSeconds(0.25), timer.Requests[1].Duration);

            Assert.IsTrue(timer.Requests[0].Complete(), "旧计时器应能模拟忽略取消后的迟到成功");
            await UniTask.Yield();
            Assert.IsTrue(ui.IsOpen<ToastFake>(), "较早 Toast 的计时器不得关闭已刷新文本的新 Toast");
            Assert.AreEqual("second", (ui.Get<ToastFake>().LastArgs as UIToastArgs)?.Text);

            Assert.IsTrue(timer.Requests[1].Complete());
            await UniTask.Yield();
            Assert.IsFalse(ui.IsOpen<ToastFake>(), "最后一次 Show 的显示时长结束后应自动关闭");
            ui.Dispose();
        });

        [UnityTest]
        public IEnumerator ShowToast_ConcurrentFirstOpen_LatestRequestOwnsTimer() => UniTask.ToCoroutine(async () =>
        {
            var backend = new GatedBackend();
            var timer = new ManualToastDelay();
            var ui = new UIUtility(_ctx, backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) }, timer.Wait);

            var first = ui.ShowToast("first", 0.1f);
            var second = ui.ShowToast("second", 0.35f);
            Assert.AreEqual(UniTaskStatus.Pending, first.Status);
            Assert.AreEqual(UniTaskStatus.Pending, second.Status);

            backend.Release();
            await UniTask.WhenAll(first, second);
            Assert.AreEqual("second", (ui.Get<ToastFake>().LastArgs as UIToastArgs)?.Text);
            Assert.AreEqual(1, timer.Requests.Count,
                "并发首开只有最新成功请求能安装自动关闭 owner");
            Assert.AreEqual(TimeSpan.FromSeconds(0.35), timer.Requests[0].Duration);
            Assert.IsTrue(ui.IsOpen<ToastFake>(),
                "后发等待者可能在首个创建者 finally 内先恢复；旧请求随后返回也不得覆盖新 timer");

            Assert.IsTrue(timer.Requests[0].Complete());
            await UniTask.Yield();
            Assert.IsFalse(ui.IsOpen<ToastFake>());
            ui.Dispose();
        });

        [UnityTest]
        public IEnumerator ShowToast_CancelledLaterWaiter_DoesNotSuppressEarlierRequest() => UniTask.ToCoroutine(async () =>
        {
            var backend = new GatedBackend();
            var timer = new ManualToastDelay();
            var ui = new UIUtility(_ctx, backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) }, timer.Wait);
            using var laterCts = new CancellationTokenSource();

            var first = ui.ShowToast("first", 0.15f);
            var cancelledLater = ui.ShowToast("cancelled", 1f, laterCts.Token);
            laterCts.Cancel();
            Assert.Throws<OperationCanceledException>(() => cancelledLater.GetAwaiter().GetResult());

            backend.Release();
            await first;
            Assert.AreEqual("first", (ui.Get<ToastFake>().LastArgs as UIToastArgs)?.Text);

            Assert.AreEqual(1, timer.Requests.Count);
            Assert.IsTrue(timer.Requests[0].Complete());
            await UniTask.Yield();
            Assert.IsFalse(ui.IsOpen<ToastFake>(),
                "后发请求取消不代表较早的有效请求也失去提交 timer 的资格");
            ui.Dispose();
        });

        [UnityTest]
        public IEnumerator ShowToast_ManualClose_CancelsOldTimerBeforeReopen() => UniTask.ToCoroutine(async () =>
        {
            var timer = new ManualToastDelay();
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) }, timer.Wait);

            await ui.ShowToast("old", 0.1f);
            ui.Close<ToastFake>();
            await ui.ShowToast("new", 0.3f);

            Assert.AreEqual(2, timer.Requests.Count);
            Assert.IsTrue(timer.Requests[0].Complete());
            await UniTask.Yield();
            Assert.IsTrue(ui.IsOpen<ToastFake>(), "手动关闭前的旧 timer 不得越权关闭重开的 Toast");
            Assert.AreEqual("new", (ui.Get<ToastFake>().LastArgs as UIToastArgs)?.Text);

            Assert.IsTrue(timer.Requests[1].Complete());
            await UniTask.Yield();
            Assert.IsFalse(ui.IsOpen<ToastFake>());
            ui.Dispose();
        });

        [Test]
        public void ShowToast_CloseAllDuringNonCooperativeCreation_DoesNotGhost()
        {
            var backend = new NonCooperativeGatedBackend();
            var ui = new UIUtility(_ctx, backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) });

            var pending = ui.ShowToast("late", 1f);
            Assert.AreEqual(UniTaskStatus.Pending, pending.Status);
            ui.CloseAll(UILayer.Top);
            backend.Release();
            pending.GetAwaiter().GetResult();

            Assert.IsFalse(ui.IsOpen<ToastFake>(),
                "创建期间清场后，即使 backend 不观察 token，迟到窗口也必须立即收口");
            ui.Dispose();
        }

        [UnityTest]
        public IEnumerator ShowToast_PreCancelledRefresh_PreservesExistingTimer() => UniTask.ToCoroutine(async () =>
        {
            var timer = new ManualToastDelay();
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) }, timer.Wait);
            await ui.ShowToast("existing", 0.15f);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                ui.ShowToast("cancelled", 1f, cts.Token).GetAwaiter().GetResult());
            Assert.AreEqual("existing", (ui.Get<ToastFake>().LastArgs as UIToastArgs)?.Text,
                "取消的刷新不能改文本或夺走既有计时 owner");

            Assert.AreEqual(1, timer.Requests.Count, "入口已取消的刷新不能安装新计时器");
            Assert.IsTrue(timer.Requests[0].Complete());
            await UniTask.Yield();
            Assert.IsFalse(ui.IsOpen<ToastFake>(), "既有 Toast 仍应按自己的原计时关闭");
            ui.Dispose();
        });

        [UnityTest]
        public IEnumerator ShowToast_Dispose_CancelsTimerWithoutCallingWindowHooks() => UniTask.ToCoroutine(async () =>
        {
            var timer = new ManualToastDelay();
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) }, timer.Wait);
            await ui.ShowToast("teardown", 0.05f);
            var toast = ui.Get<ToastFake>();

            ui.Dispose();
            Assert.AreEqual(1, timer.Requests.Count);
            Assert.IsTrue(timer.Requests[0].Complete(), "模拟不观察取消的迟到计时器完成");
            await UniTask.Yield();

            Assert.IsFalse(toast.Calls.Contains("close"),
                "UIUtility.Dispose 是纯物理 teardown；取消后的 Toast timer 不得迟到调用 OnClose");
        });

        [Test]
        public void ShowToast_TimerNonOwnerCancellation_IsLoggedAndClosesToast()
        {
            LogAssert.Expect(LogType.Error,
                new Regex("Toast 自动关闭任务异常停止.*关闭当前 Toast"));
            LogAssert.Expect(LogType.Exception, new Regex("OperationCanceledException"));
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) },
                (_, _) => UniTask.FromException(
                    new OperationCanceledException("toast clock canceled itself")));

            ui.ShowToast("failure", 1f).GetAwaiter().GetResult();

            Assert.IsFalse(ui.IsOpen<ToastFake>(),
                "计时实现未收到 owner 取消却抛 OCE，必须按故障收口，不能让 Toast 永久残留");
            Assert.That(_logSink.Entries.Any(entry =>
                entry.Exception is OperationCanceledException &&
                entry.Message.Contains("Toast 自动关闭任务异常停止")), Is.True,
                "结构化日志 sink 应保留非 owner OCE，便于定位计时 Adapter 故障");
            ui.Dispose();
        }

        [Test]
        public void ShowToast_TimerOwnerCancellation_IsSilent()
        {
            var ui = new UIUtility(_ctx, _backend, new UIBuiltinWindows
            { Toast = typeof(ToastFake), Loading = typeof(LoadingFake) }, WaitUntilCanceled);

            ui.ShowToast("owner", 1f).GetAwaiter().GetResult();
            ui.Close<ToastFake>();

            Assert.IsFalse(ui.IsOpen<ToastFake>());
            Assert.That(_logSink.Entries.Any(entry => entry.Exception is OperationCanceledException), Is.False,
                "显式关闭取消自身 timer owner 是正常生命周期，不应污染错误日志");
            ui.Dispose();
        }

#pragma warning disable CS0618 // 下列用例有意验证迁移期 ShowLoading/HideLoading 的兼容 Implementation。
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
#pragma warning restore CS0618

        [Test]
        public void LegacyLoadingPair_IsNonBreakingObsoleteAndPointsToLeaseMigration()
        {
            AssertLegacyLoadingMember(typeof(IUIUtility), "ShowLoading");
            AssertLegacyLoadingMember(typeof(IUIUtility), "HideLoading");
            AssertLegacyLoadingMember(typeof(UIUtility), "ShowLoading");
            AssertLegacyLoadingMember(typeof(UIUtility), "HideLoading");
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

#pragma warning disable CS0618 // 有意验证 legacy owner 与推荐 lease 混用时互不越权。
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
#pragma warning restore CS0618

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

#pragma warning disable CS0618 // 有意验证迁移期旧入口在取消与不合作 Adapter 下仍不会留下幽灵窗口。
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
#pragma warning restore CS0618

        // ── fakes ────────────────────────────────────────────────────────────

        private static void AssertLegacyLoadingMember(Type owner, string methodName)
        {
            var method = owner.GetMethod(methodName);
            Assert.That(method, Is.Not.Null, $"{owner.Name}.{methodName} 的迁移期兼容成员不应意外消失。 ");
            var obsolete = (ObsoleteAttribute)Attribute.GetCustomAttribute(method, typeof(ObsoleteAttribute));
            Assert.That(obsolete, Is.Not.Null, $"{owner.Name}.{methodName} 必须向新调用方发出迁移提示。 ");
            Assert.That(obsolete.IsError, Is.False, "本阶段只发编译警告，仍须保持旧源码可重新编译。 ");
            Assert.That(obsolete.Message, Does.Contain(nameof(IUIUtility.AcquireLoading)),
                "迁移提示必须给出所有权安全的替代入口。 ");
        }

        private static async UniTask WaitUntilCanceled(TimeSpan _, CancellationToken ct)
        {
            var completion = new UniTaskCompletionSource();
            using (ct.Register(() => completion.TrySetCanceled(ct)))
                await completion.Task;
        }

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
