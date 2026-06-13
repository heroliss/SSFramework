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
    /// UI 框架核心编排（<see cref="UIUtility"/>）的纯逻辑测试：栈/层、cover-reveal、模态遮罩调度、缓存复用、关闭策略。
    /// 用 fake <see cref="IUIBackend"/>（只记录调用、不碰 Unity）+ 真实空 <see cref="GameContext"/>，
    /// 脱离场景验证渲染中立的编排逻辑——印证"核心可单测"。
    /// </summary>
    public class UIWindowStackTests
    {
        private GameContext _ctx;
        private FakeBackend _backend;
        private UIUtility _ui;

        [SetUp]
        public void SetUp()
        {
            _ctx = new GameContext(new ContainerBuilder().Build(), inheritFromGlobal: false);
            _backend = new FakeBackend();
            _ui = new UIUtility(_ctx, _backend);
        }

        [TearDown]
        public void TearDown()
        {
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
            LogAssert.Expect(LogType.Exception, new Regex("boom")); // SafeOnOpen 把异常吞成日志

            var w = Open<ThrowingOnOpen>(); // OnOpen 抛异常，但不应让 Open 抛出
            Assert.IsNotNull(w);
            Assert.IsTrue(_ui.IsOpen<ThrowingOnOpen>(), "hook 抛异常不应阻止窗口入栈");

            // 内部状态未被污染：后续开/关仍正常。
            Open<PageA>();
            Assert.IsTrue(_ui.IsOpen<PageA>());
            _ui.Close<PageA>();
            Assert.IsFalse(_ui.IsOpen<PageA>());
        }

        // ── fakes ────────────────────────────────────────────────────────────

        private class FakeWindow : IUIWindow
        {
            public readonly List<string> Calls = new();
            public virtual void OnCreate() => Calls.Add("create");
            public virtual void OnOpen(object args) => Calls.Add("open:" + (args ?? string.Empty));
            public virtual void OnClose() => Calls.Add("close");
            public virtual void OnCover() => Calls.Add("cover");
            public virtual void OnReveal() => Calls.Add("reveal");
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

        // 只记录调用序列，不碰 Unity——验证核心编排逻辑。
        private class FakeBackend : IUIBackend
        {
            public readonly List<string> Log = new();
            public int Count(string entry) => Log.Count(x => x == entry);

            public void Initialize() => Log.Add("init");

            public UniTask<IUIWindow> CreateWindow(UIWindowMeta meta, IGameContext context, CancellationToken ct)
            {
                var w = (IUIWindow)Activator.CreateInstance(meta.WindowType);
                Log.Add("create:" + meta.WindowType.Name);
                return UniTask.FromResult(w);
            }

            public void BringToFront(IUIWindow window) => Log.Add("front:" + window.GetType().Name);
            public void SetVisible(IUIWindow window, bool visible) => Log.Add("visible:" + window.GetType().Name + ":" + visible);
            public void SetModalMask(IUIWindow ownerWindow, bool on) => Log.Add("mask:" + ownerWindow.GetType().Name + ":" + on);
            public void DestroyWindow(IUIWindow window) => Log.Add("destroy:" + window.GetType().Name);
            public void Teardown() => Log.Add("teardown");
        }
    }
}
