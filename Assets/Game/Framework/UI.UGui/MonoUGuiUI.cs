using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;
using Game.Framework.Utility;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.UI.UGui
{
    /// <summary>
    /// UGUI 后端的 UI 框架入口——挂在 Context 子节点上的单个 <see cref="MonoUtilityBase"/>，自动注册为
    /// <see cref="IUIUtility"/>（镜像 <c>MonoPoolUtility</c>）。把渲染中立的 <see cref="UIUtility"/> 核心与 <see cref="UGuiBackend"/>
    /// 接到一起，并把 <see cref="IUIUtility"/> 调用转发给核心。
    /// </summary>
    /// <remarks>
    /// <b>同一 Context 只能挂一个 UI 入口</b>（UGui 或 Toolkit 二选一）——两个都挂会因重复注册 <see cref="IUIUtility"/> 报错。<br/>
    /// 核心<b>懒建</b>（首次开窗时）：此时 Awake 早已跑完、Context 就绪（遵循 <c>Assets/Game/AGENTS.md</c>
    /// 「Mono 生命周期与 Context」中不在同帧 Awake 假设父 Context 已就绪的约束）。
    /// 作为框架适配层，经 <c>((IHasGameContext)this).Context</c> 合法取自身 Context（用于资源加载 + 注入窗口）。
    /// </remarks>
    public sealed class MonoUGuiUI : MonoUtilityBase, IUIUtility
    {
        [SerializeField]
        [Tooltip("窗口根 Canvas。留空则首次开窗时自动在本节点下建一个 ScreenSpaceOverlay Canvas。\n" +
                 "注意：UGUI 输入需要场景里有 EventSystem。")]
        private Canvas _canvas;

        private UIUtility _core;

        // 懒建核心 + backend。首次 IUIUtility 调用时触发，此刻 Context 已就绪。
        private UIUtility Core
        {
            get
            {
                if (_core == null)
                {
                    var ctx = ((IHasGameContext)this).Context
                        ?? throw new InvalidOperationException("[MonoUGuiUI] Context 未就绪，无法初始化 UI 框架。");
                    var canvas = _canvas != null ? _canvas : CreateOverlayCanvas();
                    _core = new UIUtility(ctx, new UGuiBackend(canvas), new UIBuiltinWindows
                    {
                        Toast = typeof(UGuiToastWindow),
                        Loading = typeof(UGuiLoadingWindow),
                    });
                }
                return _core;
            }
        }

        private Canvas CreateOverlayCanvas()
        {
            var go = new GameObject("UIRoot (Canvas)", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            _canvas = canvas;
            return canvas;
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
            _core?.Dispose(); // 拆掉所有窗口 + 层根
            _core = null;
            base.OnDestroy();
        }
    }
}
