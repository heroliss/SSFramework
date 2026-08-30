using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;
using Game.Framework.Logging;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Framework.UI.UGui
{
    /// <summary>
    /// UGUI 渲染后端：层 = root Canvas 下的全屏 RectTransform 子节点（按 <see cref="UILayer"/> 顺序建，
    /// hierarchy 顺序即绘制顺序）；窗口 = 实例化到对应层下的 prefab，层内顺序 = sibling index；
    /// 模态遮罩 = 铺在 owner 之下的全屏 <see cref="Image"/>（raycast 拦截下层输入）。
    /// </summary>
    /// <remarks>
    /// 窗口 prefab 经资源系统加载：用一个跟随 backend 的 <see cref="DisposableBag"/> 派生<b>每窗口子 bag</b> 持 prefab handle，
    /// 窗口销毁时 Dispose 子 bag 释放 prefab（Cache 策略下窗口不销毁，prefab 也就留着）。
    /// 窗口实例自身的订阅/资源由它的 <c>MonoViewBase.Bag</c> 在 GameObject 被 Destroy 时释放。
    /// </remarks>
    public sealed class UGuiBackend : IUIBackend
    {
        private readonly Canvas _canvas;
        private readonly Dictionary<UILayer, RectTransform> _layerRoots = new();
        private readonly Dictionary<IUIWindow, Slot> _slots = new();
        private DisposableBag _loadBag;
        private GameObject _inputBlocker; // 全屏透明 raycast 挡板（过渡期间），懒建复用
        private bool _initialized;

        // 每窗口的物理状态：所属层、GameObject、持 prefab handle 的子 bag、模态遮罩。
        private sealed class Slot
        {
            public GameObject Go;
            public DisposableBag LoadBag;
            public GameObject Mask;
        }

        public UGuiBackend(Canvas canvas) => _canvas = canvas != null
            ? canvas
            : throw new ArgumentNullException(nameof(canvas));

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            foreach (UILayer layer in Enum.GetValues(typeof(UILayer)))
            {
                var go = new GameObject(layer.ToString(), typeof(RectTransform));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_canvas.transform, false);
                Stretch(rt);
                _layerRoots[layer] = rt;
            }
        }

        public async UniTask<IUIWindow> CreateWindow(UIWindowMeta meta, IGameContext context, CancellationToken ct)
        {
            if (meta == null) throw new ArgumentNullException(nameof(meta));
            if (context == null) throw new ArgumentNullException(nameof(context));
            ct.ThrowIfCancellationRequested();
            if (!typeof(UGuiWindowBase).IsAssignableFrom(meta.WindowType))
            {
                Log.Error($"{meta.WindowType.Name} 不是 {nameof(UGuiWindowBase)} 派生类型。",
                    category: nameof(UGuiBackend));
                return null;
            }

            var parent = _layerRoots[meta.Layer];
            GameObject go = null;
            DisposableBag wbag = null;
            bool committed = false;
            try
            {
                if (string.IsNullOrEmpty(meta.Asset))
                {
                    // 纯代码搭建（无 prefab）：空 GameObject + AddComponent 窗口类型，窗口在 OnCreated 里代码搭 UGUI。
                    // 先挂到层根再 AddComponent，使 MonoViewBase.Awake 能沿父链找到 Context 自动注入。
                    go = new GameObject(meta.WindowType.Name, typeof(RectTransform));
                    go.transform.SetParent(parent, false);
                    Stretch((RectTransform)go.transform);
                    ct.ThrowIfCancellationRequested();
                    go.AddComponent(meta.WindowType);
                }
                else
                {
                    _loadBag ??= context.CreateBag();
                    wbag = _loadBag.CreateChild();
                    var prefab = await wbag.Load<GameObject>(meta.Asset, ct);
                    if (prefab == null) return null; // 加载失败，资源系统已打日志；finally 释放本窗口子 bag
                    ct.ThrowIfCancellationRequested();
                    // Instantiate 到层根下：prefab 上的 MonoViewBase 在 Awake 沿父链找到 Context 自动注入。
                    go = Object.Instantiate(prefab, parent);
                }

                ct.ThrowIfCancellationRequested();
                var window = go.GetComponent(meta.WindowType) as IUIWindow;
                if (window == null)
                {
                    Log.Error($"{meta.WindowType.Name} 组件未出现在" +
                              (string.IsNullOrEmpty(meta.Asset) ? "代码创建的窗口根节点。" : $" prefab '{meta.Asset}' 根节点。"),
                        category: nameof(UGuiBackend));
                    return null;
                }

                go.transform.SetAsLastSibling();
                _slots[window] = new Slot { Go = go, LoadBag = wbag };
                committed = true;
                return window;
            }
            finally
            {
                // CreateWindow 的提交点是 _slots 登记。取消、加载异常、无效 prefab 或 AddComponent 失败时，
                // 立即回滚本次创建，而不是把部分层级/handle 留到整个 UI Teardown 才清理。
                if (!committed)
                {
                    if (go != null) Object.Destroy(go);
                    wbag?.Dispose();
                }
            }
        }

        public void BringToFront(IUIWindow window)
        {
            if (!_slots.TryGetValue(window, out var s) || s.Go == null) return;
            // 先把遮罩置顶、再把窗口置顶 → 窗口落在遮罩正上方、遮罩紧贴其下。
            // （否则置顶一个已开的模态窗口后，遮罩会留在原 sibling index，模态拦截层级错乱。）
            if (s.Mask != null) s.Mask.transform.SetAsLastSibling();
            s.Go.transform.SetAsLastSibling();
        }

        public void SetVisible(IUIWindow window, bool visible)
        {
            if (_slots.TryGetValue(window, out var s) && s.Go != null) s.Go.SetActive(visible);
        }

        public void SetModalMask(IUIWindow ownerWindow, bool on)
        {
            if (!_slots.TryGetValue(ownerWindow, out var s) || s.Go == null) return;

            if (on)
            {
                if (s.Mask != null) return;
                int ownerIndex = s.Go.transform.GetSiblingIndex();
                var maskGo = new GameObject("Modal Mask", typeof(RectTransform), typeof(Image));
                var rt = (RectTransform)maskGo.transform;
                rt.SetParent(s.Go.transform.parent, false);
                Stretch(rt);
                var img = maskGo.GetComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 0.5f);
                img.raycastTarget = true;          // 吃掉点击，拦截下层
                rt.SetSiblingIndex(ownerIndex);    // 铺在 owner 正下方（owner 随之上移一格）
                s.Mask = maskGo;
            }
            else if (s.Mask != null)
            {
                Object.Destroy(s.Mask);
                s.Mask = null;
            }
        }

        public void SetInputBlocked(bool blocked)
        {
            if (blocked)
            {
                if (_inputBlocker == null)
                {
                    _inputBlocker = new GameObject("Input Blocker", typeof(RectTransform), typeof(Image));
                    var rt = (RectTransform)_inputBlocker.transform;
                    rt.SetParent(_canvas.transform, false);
                    Stretch(rt);
                    var img = _inputBlocker.GetComponent<Image>();
                    img.color = Color.clear;  // 全透明，但 raycastTarget 仍拦截点击
                    img.raycastTarget = true;
                }
                _inputBlocker.transform.SetAsLastSibling(); // 盖所有层根之上
                _inputBlocker.SetActive(true);
            }
            else if (_inputBlocker != null)
            {
                _inputBlocker.SetActive(false);
            }
        }

        public void DestroyWindow(IUIWindow window)
        {
            if (!_slots.TryGetValue(window, out var s)) return;
            _slots.Remove(window);
            if (s.Mask != null) Object.Destroy(s.Mask);
            if (s.Go != null) Object.Destroy(s.Go); // → MonoViewBase.OnDestroy → 窗口 Bag.Dispose
            s.LoadBag?.Dispose();                   // 释放 prefab handle
        }

        public void Teardown()
        {
            foreach (var s in _slots.Values)
            {
                if (s.Mask != null) Object.Destroy(s.Mask);
                if (s.Go != null) Object.Destroy(s.Go);
            }
            _slots.Clear();
            _loadBag?.Dispose();
            _loadBag = null;
            if (_inputBlocker != null) { Object.Destroy(_inputBlocker); _inputBlocker = null; }
            foreach (var rt in _layerRoots.Values)
                if (rt != null) Object.Destroy(rt.gameObject);
            _layerRoots.Clear();
            _initialized = false;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }
    }
}
