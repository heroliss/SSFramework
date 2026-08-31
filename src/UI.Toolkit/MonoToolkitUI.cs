using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;
using Game.Framework.UI;
using Game.Framework.Utility;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.UI.Toolkit
{
    /// <summary>
    /// UI Toolkit 后端的 UI 框架入口——挂在 Context 子节点上的单个 <see cref="MonoUtilityBase"/>，自动注册为
    /// <see cref="IUIUtility"/>（镜像 <c>MonoPoolUtility</c>）。把渲染中立的 <see cref="UIUtility"/> 核心与 <see cref="ToolkitBackend"/>
    /// 接到一起，并把 <see cref="IUIUtility"/> 调用转发给核心。
    /// </summary>
    /// <remarks>
    /// <b>同一 Context 只能挂一个 UI 入口</b>（UGui 或 Toolkit 二选一）——两个都挂会因重复注册 <see cref="IUIUtility"/> 报错。<br/>
    /// 需要一个 <see cref="UIDocument"/> 承载窗口可视树：层容器会加到它的 <c>rootVisualElement</c> 上（建议用专门的
    /// 高 <c>sortingOrder</c> UIDocument，盖在业务主界面之上）。核心懒建，首次开窗时经 <c>((IHasGameContext)this).Context</c> 取自身 Context。<br/>
    /// 宿主销毁后保留已释放内核并显式拒绝旧引用调用，不会重建 UIDocument / 核心，也不会退化为空引用异常。
    /// </remarks>
    public sealed class MonoToolkitUI : MonoUtilityBase, IUIUtility
    {
        [SerializeField]
        [Tooltip("承载窗口的 UIDocument。窗口层容器会加到它的 rootVisualElement 上。留空则首次开窗时在本节点上自动添加一个。")]
        private UIDocument _document;

        private UIUtility _core;
        private bool _destroyed;

        private UIUtility Core
        {
            get
            {
                MainThreadGuard.AssertMainThread(nameof(MonoToolkitUI));
                if (_destroyed)
                    throw new ObjectDisposedException(nameof(MonoToolkitUI),
                        "UI Toolkit 入口宿主已销毁——请检查是否持有了过期的 IUIUtility 引用。");
                if (_core == null)
                {
                    var ctx = ((IHasGameContext)this).Context
                        ?? throw new InvalidOperationException("[MonoToolkitUI] Context 未就绪，无法初始化 UI 框架。");
                    if (_document == null) _document = ResolveOrCreateDocument();
                    _core = new UIUtility(ctx, new ToolkitBackend(_document), new UIBuiltinWindows
                    {
                        Toast = typeof(ToolkitToastWindow),
                        Loading = typeof(ToolkitLoadingWindow),
                    });
                }
                return _core;
            }
        }

        private UIDocument ResolveOrCreateDocument()
        {
            var doc = GetComponent<UIDocument>();
            if (doc == null) doc = gameObject.AddComponent<UIDocument>();
            return doc;
        }

        // ── IUIUtility 转发到核心 ──
        public UniTask<T> Open<T>(CancellationToken ct = default) where T : class, IUIWindow => Core.Open<T>(ct);
        public UniTask<T> Open<T>(object args, CancellationToken ct = default) where T : class, IUIWindow => Core.Open<T>(args, ct);
        public void Close<T>() where T : class, IUIWindow => Core.Close<T>();
        public void Close(IUIWindow window) => Core.Close(window);
        public void CloseTop(UILayer layer) => Core.CloseTop(layer);
        public bool Back() => Core.Back();
        public void CloseAll(UILayer layer) => Core.CloseAll(layer);
        public void CloseAll() => Core.CloseAll();
        public T Get<T>() where T : class, IUIWindow => Core.Get<T>();
        public bool IsOpen<T>() where T : class, IUIWindow => Core.IsOpen<T>();
        public UniTask ShowToast(string text, float duration = 2f, CancellationToken ct = default) => Core.ShowToast(text, duration, ct);
        public UniTask<LoadingHandle> AcquireLoading(string text = null, CancellationToken ct = default) => Core.AcquireLoading(text, ct);
        [Obsolete("ShowLoading/HideLoading 仅用于旧源码迁移；请改用 using var loading = await AcquireLoading(text, ct)，由句柄表达并发所有权。", false)]
        public UniTask ShowLoading(string text = null, CancellationToken ct = default) => Core.ShowLoading(text, ct);
        [Obsolete("ShowLoading/HideLoading 仅用于旧源码迁移；请释放 AcquireLoading 返回的 LoadingHandle，通常使用 using var 自动释放。", false)]
        public void HideLoading() => Core.HideLoading();

        protected override void OnDestroy()
        {
            _destroyed = true;
            try { _core?.Dispose(); } // 拆掉所有窗口 + 层根；保留已释放实例作为终态守卫
            finally { base.OnDestroy(); }
        }
    }
}
