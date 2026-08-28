using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Flow;
using Game.Framework.Internal;
using Game.Framework.UI;
using NUnit.Framework;

namespace Game.Framework.Test
{
    /// <summary>
    /// 锁定宽松/严格开窗的失败分工，并穿过真实 <see cref="GameFlow"/> 验证必需页面失败不会提交状态。
    /// </summary>
    public sealed class UIOpenRequiredTests
    {
        private GameContext _context;
        private GameFlow _flow;
        private ConfigurableBackend _backend;
        private UIUtility _ui;

        [SetUp]
        public void SetUp()
        {
            using var builder = new ContainerBuilder();
            _flow = new GameFlow();
            builder.RegisterOwned(_flow, typeof(IGameFlow));
            _context = new GameContext(builder.Build(), inheritFromGlobal: false);
            _backend = new ConfigurableBackend();
            _ui = new UIUtility(_context, _backend);
        }

        [TearDown]
        public void TearDown()
        {
            _ui.Dispose();
            _context.Dispose();
        }

        [Test]
        public void Open_BackendReturnsNull_RemainsLenient()
        {
            _backend.ReturnNull = true;

            var window = _ui.Open<RequiredAssetWindow>().GetAwaiter().GetResult();

            Assert.IsNull(window, "可选窗口入口应保留 null 契约，让调用方决定降级策略");
        }

        [Test]
        public void OpenRequired_BackendReturnsNull_ThrowsWindowAndResourceContext()
        {
            _backend.ReturnNull = true;

            var error = Assert.Throws<InvalidOperationException>(
                () => _ui.OpenRequired<RequiredAssetWindow>().GetAwaiter().GetResult());

            StringAssert.Contains(typeof(RequiredAssetWindow).FullName, error.Message);
            StringAssert.Contains("ui/required-window", error.Message);
            StringAssert.Contains("IUIUtility.Open<T>() 未获得窗口实例", error.Message);
        }

        [Test]
        public void OpenRequired_WithArgs_ForwardsArgumentsOnSuccess()
        {
            var args = new object();

            var window = _ui.OpenRequired<RequiredAssetWindow>(args).GetAwaiter().GetResult();

            Assert.AreSame(args, window.LastArgs);
        }

        [Test]
        public void OpenRequired_NullResultWithCanceledToken_PreservesCancellation()
        {
            var canceled = new CancellationToken(canceled: true);
            var ui = new NullIgnoringCancellationUI();

            var error = Assert.Throws<OperationCanceledException>(
                () => ui.OpenRequired<RequiredAssetWindow>(canceled).GetAwaiter().GetResult());

            Assert.AreEqual(canceled, error.CancellationToken);
        }

        [Test]
        public void OpenRequired_BackendFaults_PropagatesOriginalException()
        {
            var expected = new InvalidOperationException("backend-failed");
            _backend.Failure = expected;

            var actual = Assert.Throws<InvalidOperationException>(
                () => _ui.OpenRequired<RequiredAssetWindow>().GetAwaiter().GetResult());

            Assert.AreSame(expected, actual, "严格入口只能升级 null，不应包装 Adapter 已提供的精确异常");
        }

        [Test]
        public void OpenRequired_FailsDuringEnter_GameFlowRemainsWithoutCurrentState()
        {
            _backend.ReturnNull = true;
            var state = new RequiredWindowState(_ui);

            var transition = _flow.GoTo(state);
            var error = Assert.Throws<InvalidOperationException>(() => transition.GetAwaiter().GetResult());

            StringAssert.Contains("必需窗口", error.Message);
            Assert.IsNull(_flow.Current, "必需页面没有建立时，Flow 不得提交一个无页面的当前状态");
            Assert.IsFalse(_flow.IsTransitioning);
        }

        private sealed class RequiredWindowState : FlowState
        {
            private readonly IUIUtility _ui;

            public RequiredWindowState(IUIUtility ui) => _ui = ui;

            protected internal override async UniTask OnEnter(CancellationToken ct)
                => await _ui.OpenRequired<RequiredAssetWindow>(ct);
        }

        [UIWindow(Asset = "ui/required-window", Layer = UILayer.Page)]
        private sealed class RequiredAssetWindow : IUIWindow
        {
            public object LastArgs { get; private set; }

            public void OnCreate() { }
            public void OnOpen(object args) => LastArgs = args;
            public void OnClose() { }
            public void OnCover() { }
            public void OnReveal() { }
            public UniTask OnOpenTransition(CancellationToken ct) => UniTask.CompletedTask;
            public UniTask OnCloseTransition(CancellationToken ct) => UniTask.CompletedTask;
        }

        private sealed class ConfigurableBackend : IUIBackend
        {
            public bool ReturnNull { get; set; }
            public Exception Failure { get; set; }

            public void Initialize() { }

            public UniTask<IUIWindow> CreateWindow(
                UIWindowMeta meta,
                IGameContext context,
                CancellationToken ct)
            {
                if (Failure != null) return UniTask.FromException<IUIWindow>(Failure);
                IUIWindow window = ReturnNull
                    ? null
                    : (IUIWindow)Activator.CreateInstance(meta.WindowType);
                return UniTask.FromResult(window);
            }

            public void BringToFront(IUIWindow window) { }
            public void SetVisible(IUIWindow window, bool visible) { }
            public void SetModalMask(IUIWindow ownerWindow, bool on) { }
            public void DestroyWindow(IUIWindow window) { }
            public void SetInputBlocked(bool blocked) { }
            public void Teardown() { }
        }

        /// <summary>
        /// 模拟自定义 IUIUtility：它不观察调用方 token 且以 null 表示失败，用于直接锁定扩展方法自己的取消优先级。
        /// </summary>
        private sealed class NullIgnoringCancellationUI : IUIUtility
        {
            public UniTask<T> Open<T>(CancellationToken ct = default) where T : class, IUIWindow
                => UniTask.FromResult<T>(null);

            public UniTask<T> Open<T>(object args, CancellationToken ct = default) where T : class, IUIWindow
                => UniTask.FromResult<T>(null);

            public void Close<T>() where T : class, IUIWindow { }
            public void Close(IUIWindow window) { }
            public void CloseTop(UILayer layer) { }
            public bool Back() => false;
            public void CloseAll(UILayer layer) { }
            public void CloseAll() { }
            public T Get<T>() where T : class, IUIWindow => null;
            public bool IsOpen<T>() where T : class, IUIWindow => false;
            public UniTask ShowToast(string text, float duration = 2f, CancellationToken ct = default)
                => UniTask.CompletedTask;
            public UniTask<LoadingHandle> AcquireLoading(string text = null, CancellationToken ct = default)
                => UniTask.FromResult(default(LoadingHandle));
            public UniTask ShowLoading(string text = null, CancellationToken ct = default)
                => UniTask.CompletedTask;
            public void HideLoading() { }
        }
    }
}
