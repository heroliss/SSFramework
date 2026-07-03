using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace Game.Framework.Test
{
    /// <summary>
    /// UI 过渡与返回导航（ADR-0020）的纯逻辑测试：入场/出场过渡的挡输入编排、逻辑关闭先于表现、
    /// 过渡异常隔离、CloseAll 直通、Back() 逐层语义与 BackClosable 拦截。
    /// 手法同 <see cref="UIWindowStackTests"/>：fake backend 记录调用序列 + 可控完成时机的过渡窗口。
    /// </summary>
    public class UITransitionTests
    {
        private GameContext _ctx;
        private RecordingBackend _backend;
        private UIUtility _ui;

        [SetUp]
        public void SetUp()
        {
            _ctx = new GameContext(new ContainerBuilder().Build(), inheritFromGlobal: false);
            _backend = new RecordingBackend();
            _ui = new UIUtility(_ctx, _backend);
            TransitionWindow.ResetGates();
        }

        [TearDown]
        public void TearDown()
        {
            _ui.Dispose();
            _ctx.Dispose();
        }

        private T Open<T>(object args = null) where T : class, IUIWindow
            => _ui.Open<T>(args).GetAwaiter().GetResult();

        // ── 过渡：挡输入编排 ─────────────────────────────────────────────

        [Test]
        public void NoTransition_NeverTouchesInputBlocker()
        {
            Open<PlainWindow>();
            _ui.Close<PlainWindow>();

            Assert.AreEqual(0, _backend.Log.Count(x => x.StartsWith("block:")),
                "默认无过渡的窗口不应触碰挡板（零开销路径）");
        }

        [Test]
        public void OpenTransition_BlocksInput_UntilComplete_AndOpenReturnsImmediately()
        {
            var w = Open<TransitionWindow>(); // Open 不等过渡——同步返回时过渡仍挂起

            Assert.IsNotNull(w, "Open 应在 OnOpen 后立即返回，不等过渡完成");
            Assert.IsTrue(w.Calls.Contains("openTransition"));
            CollectionAssert.AreEqual(new[] { "block:True" }, _backend.BlockLog, "过渡进行中应挡输入");

            TransitionWindow.OpenGate.TrySetResult();

            CollectionAssert.AreEqual(new[] { "block:True", "block:False" }, _backend.BlockLog, "过渡完成应放开输入");
        }

        [Test]
        public void CloseTransition_LogicalCloseImmediate_PhysicalCloseDeferred()
        {
            var w = Open<TransitionWindow>();
            TransitionWindow.OpenGate.TrySetResult(); // 入场过渡放行，回到静止态

            _ui.Close<TransitionWindow>();

            // 逻辑关闭立即生效：不再 IsOpen；但出场动画未完，窗口未销毁、OnClose 未调。
            Assert.IsFalse(_ui.IsOpen<TransitionWindow>(), "出场过渡期间 IsOpen 应已为 false");
            Assert.IsFalse(w.Calls.Contains("close"), "OnClose 应等出场过渡完成后才调");
            Assert.AreEqual(0, _backend.Count("destroy:TransitionWindow"), "过渡期间不应销毁");
            Assert.AreEqual("block:True", _backend.BlockLog.Last(), "出场过渡应挡输入");

            TransitionWindow.CloseGate.TrySetResult();

            Assert.IsTrue(w.Calls.Contains("close"), "过渡完成后应走 OnClose");
            Assert.AreEqual(1, _backend.Count("destroy:TransitionWindow"), "过渡完成后按策略销毁");
            Assert.AreEqual("block:False", _backend.BlockLog.Last(), "过渡完成应放开输入");
        }

        [Test]
        public void CloseTransition_SameTypeReopen_CreatesNewInstance()
        {
            var w1 = Open<TransitionWindow>();
            TransitionWindow.OpenGate.TrySetResult();

            _ui.Close<TransitionWindow>();          // 出场过渡挂起中
            TransitionWindow.ResetOpenGate();        // 新实例的入场过渡用新闸
            var w2 = Open<TransitionWindow>();       // 立即重开同类型

            Assert.AreNotSame(w1, w2, "出场过渡中的旧实例已逻辑关闭，重开应新建实例");
            Assert.AreEqual(2, _backend.Count("create:TransitionWindow"));
        }

        [Test]
        public void TransitionThrows_UnblocksInput_AndCloseStillFinishes()
        {
            LogAssert.Expect(LogType.Exception, new Regex("transition boom"));

            var w = Open<ThrowingCloseTransitionWindow>();
            _ui.Close<ThrowingCloseTransitionWindow>();

            // 过渡抛异常被隔离：挡板解除、关闭流程照常收尾（不能挡死全屏输入）。
            Assert.IsTrue(w.Calls.Contains("close"));
            Assert.AreEqual(1, _backend.Count("destroy:ThrowingCloseTransitionWindow"));
            if (_backend.BlockLog.Count > 0)
                Assert.AreEqual("block:False", _backend.BlockLog.Last(), "异常路径必须放开输入");
        }

        [Test]
        public void CloseAll_SkipsTransitions_DestroysImmediately()
        {
            Open<TransitionWindow>();
            TransitionWindow.OpenGate.TrySetResult();

            _ui.CloseAll(UILayer.Window);

            Assert.AreEqual(1, _backend.Count("destroy:TransitionWindow"),
                "CloseAll 应走立即路径，不播出场过渡");
        }

        // ── Back()：逐层返回导航 ─────────────────────────────────────────

        [Test]
        public void Back_EmptyUI_ReturnsFalse()
        {
            Assert.IsFalse(_ui.Back(), "三层皆空应返回 false（业务可做退出兜底）");
        }

        [Test]
        public void Back_ClosesPopupBeforePage()
        {
            Open<PlainPage>();
            Open<PlainPopup>();

            Assert.IsTrue(_ui.Back(), "有窗可关应返回 true");

            Assert.IsFalse(_ui.IsOpen<PlainPopup>(), "应先关更高层的 Popup");
            Assert.IsTrue(_ui.IsOpen<PlainPage>(), "低层的 Page 不受影响");
        }

        [Test]
        public void Back_UnclosableTop_ConsumesWithoutClosing()
        {
            Open<PlainPage>();
            Open<UnclosablePopup>();

            Assert.IsTrue(_ui.Back(), "BackClosable=false 的栈顶应消费返回键");

            Assert.IsTrue(_ui.IsOpen<UnclosablePopup>(), "但窗口本身不应被关");
            Assert.IsTrue(_ui.IsOpen<PlainPage>(), "更低层也不应被越过关闭");
        }

        [Test]
        public void Back_DuringTransition_IsSwallowed()
        {
            Open<PlainPage>();
            Open<TransitionWindow>(); // 入场过渡挂起中

            Assert.IsTrue(_ui.Back(), "过渡进行中 Back 应被吞掉（返回 true）");
            Assert.IsTrue(_ui.IsOpen<PlainPage>(), "吞掉 = 不动作，Page 不应被关");
            Assert.IsTrue(_ui.IsOpen<TransitionWindow>());
        }

        // ── fakes ────────────────────────────────────────────────────────────

        private class RecordingWindow : IUIWindow
        {
            public readonly List<string> Calls = new();
            public virtual void OnCreate() => Calls.Add("create");
            public virtual void OnOpen(object args) => Calls.Add("open");
            public virtual void OnClose() => Calls.Add("close");
            public virtual void OnCover() => Calls.Add("cover");
            public virtual void OnReveal() => Calls.Add("reveal");
            public virtual UniTask OnOpenTransition(CancellationToken ct) => UniTask.CompletedTask;
            public virtual UniTask OnCloseTransition(CancellationToken ct) => UniTask.CompletedTask;
        }

        [UIWindow(Layer = UILayer.Window)] private class PlainWindow : RecordingWindow { }
        [UIWindow(Layer = UILayer.Page)] private class PlainPage : RecordingWindow { }
        [UIWindow(Layer = UILayer.Popup)] private class PlainPopup : RecordingWindow { }
        [UIWindow(Layer = UILayer.Popup, BackClosable = false)] private class UnclosablePopup : RecordingWindow { }

        // 过渡完成时机由静态闸门控制（fake backend 用 Activator 实例化，无法传实例引用，故用静态）。
        [UIWindow(Layer = UILayer.Window)]
        private class TransitionWindow : RecordingWindow
        {
            public static UniTaskCompletionSource OpenGate;
            public static UniTaskCompletionSource CloseGate;
            public static void ResetGates() { OpenGate = new(); CloseGate = new(); }
            public static void ResetOpenGate() => OpenGate = new();

            public override UniTask OnOpenTransition(CancellationToken ct)
            {
                Calls.Add("openTransition");
                return OpenGate.Task;
            }

            public override UniTask OnCloseTransition(CancellationToken ct)
            {
                Calls.Add("closeTransition");
                return CloseGate.Task;
            }
        }

        // 出场过渡同步抛异常：验证框架隔离（记日志 + 按无过渡收尾）。
        [UIWindow(Layer = UILayer.Window)]
        private class ThrowingCloseTransitionWindow : RecordingWindow
        {
            public override UniTask OnCloseTransition(CancellationToken ct)
                => throw new InvalidOperationException("transition boom");
        }

        private class RecordingBackend : IUIBackend
        {
            public readonly List<string> Log = new();
            public List<string> BlockLog => Log.Where(x => x.StartsWith("block:")).ToList();
            public int Count(string entry) => Log.Count(x => x == entry);

            public void Initialize() => Log.Add("init");

            public UniTask<IUIWindow> CreateWindow(UIWindowMeta meta, IGameContext context, CancellationToken ct)
            {
                Log.Add("create:" + meta.WindowType.Name);
                return UniTask.FromResult((IUIWindow)Activator.CreateInstance(meta.WindowType));
            }

            public void BringToFront(IUIWindow window) => Log.Add("front:" + window.GetType().Name);
            public void SetVisible(IUIWindow window, bool visible) => Log.Add("visible:" + window.GetType().Name + ":" + visible);
            public void SetModalMask(IUIWindow ownerWindow, bool on) => Log.Add("mask:" + ownerWindow.GetType().Name + ":" + on);
            public void DestroyWindow(IUIWindow window) => Log.Add("destroy:" + window.GetType().Name);
            public void SetInputBlocked(bool blocked) => Log.Add("block:" + blocked);
            public void Teardown() => Log.Add("teardown");
        }
    }
}
