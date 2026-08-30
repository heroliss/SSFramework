using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.UI.Toolkit
{
    /// <summary>
    /// UI Toolkit 渲染后端：层 = <see cref="UIDocument"/> 根可视树下的全屏容器 <see cref="VisualElement"/>
    /// （按 <see cref="UILayer"/> 顺序建，子节点顺序即绘制顺序）；窗口 = UXML clone（或纯代码搭建）的 VisualElement，
    /// 层内顺序 = 子节点 index（<see cref="VisualElement.BringToFront"/> 置顶）；模态遮罩 = 铺在 owner 之下、吃事件的全屏元素。
    /// </summary>
    /// <remarks>
    /// 窗口 UXML 经资源系统加载：用跟随 backend 的 <see cref="DisposableBag"/> 派生每窗口子 bag 持 <c>VisualTreeAsset</c> handle。
    /// 窗口实例（<see cref="UIToolkitWindowBase"/>）的订阅/资源由它自己的 <c>Bag</c> 在 <c>Dispose</c> 时释放。
    /// 层容器本身 <see cref="PickingMode.Ignore"/>，空白处点击落到下层；窗口根也 Ignore，由窗口自己的内容元素（或模态遮罩）吃事件。
    /// </remarks>
    public sealed class ToolkitBackend : IUIBackend
    {
        private readonly UIDocument _document;
        private readonly Dictionary<UILayer, VisualElement> _layerRoots = new();
        private readonly Dictionary<IUIWindow, Slot> _slots = new();
        private DisposableBag _loadBag;
        private VisualElement _inputBlocker; // 全屏吃事件挡板（过渡期间），懒建复用
        private bool _initialized;

        private sealed class Slot
        {
            public UIToolkitWindowBase View;
            public VisualElement Element;
            public DisposableBag LoadBag;
            public VisualElement Mask;
        }

        public ToolkitBackend(UIDocument document) => _document = document != null
            ? document
            : throw new ArgumentNullException(nameof(document));

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            var root = _document.rootVisualElement;
            foreach (UILayer layer in Enum.GetValues(typeof(UILayer)))
            {
                var container = new VisualElement { name = "UILayer_" + layer };
                Stretch(container);
                container.pickingMode = PickingMode.Ignore; // 空白处透传到下层
                root.Add(container);
                _layerRoots[layer] = container;
            }
        }

        public async UniTask<IUIWindow> CreateWindow(UIWindowMeta meta, IGameContext context, CancellationToken ct)
        {
            if (meta == null) throw new ArgumentNullException(nameof(meta));
            if (context == null) throw new ArgumentNullException(nameof(context));
            ct.ThrowIfCancellationRequested();
            if (!typeof(UIToolkitWindowBase).IsAssignableFrom(meta.WindowType))
            {
                Log.Error($"{meta.WindowType.Name} 不是 {nameof(UIToolkitWindowBase)} 派生类型。",
                    category: nameof(ToolkitBackend));
                return null;
            }
            // 框架用 Activator 实例化窗口，必须有 public 无参构造——缺了就在加载资源前给清晰错误，
            // 而不是等到 Activator.CreateInstance 抛晦涩的 MissingMethodException（且那时 UXML 已加载、句柄要回收）。
            if (meta.WindowType.GetConstructor(Type.EmptyTypes) == null)
            {
                Log.Error($"{meta.WindowType.Name} 缺少 public 无参构造——UI Toolkit 窗口由框架 Activator 实例化，" +
                          "数据走 OnOpen(args) 而非构造函数。", category: nameof(ToolkitBackend));
                return null;
            }

            VisualElement root = null;
            DisposableBag wbag = null;
            UIToolkitWindowBase window = null;
            bool committed = false;
            try
            {
                if (!string.IsNullOrEmpty(meta.Asset))
                {
                    wbag = (_loadBag ??= context.CreateBag()).CreateChild();
                    var vta = await wbag.Load<VisualTreeAsset>(meta.Asset, ct);
                    if (vta == null) return null; // 加载失败，资源系统已打日志；finally 释放本窗口子 bag
                    ct.ThrowIfCancellationRequested();
                    root = vta.Instantiate();
                }
                else
                {
                    root = new VisualElement(); // 纯代码搭建：窗口在 OnCreated 里往 Root 加元素
                }

                window = (UIToolkitWindowBase)Activator.CreateInstance(meta.WindowType);
                window.BindContextInternal(context, root); // 绑定 Context，不调 OnCreated（由框架 OnCreate hook 触发）
                ct.ThrowIfCancellationRequested();

                Stretch(root);
                root.pickingMode = PickingMode.Ignore; // 窗口根不吃事件，由其内容/遮罩负责
                _layerRoots[meta.Layer].Add(root);     // 末尾 = 栈顶
                ct.ThrowIfCancellationRequested();

                _slots[window] = new Slot { View = window, Element = root, LoadBag = wbag };
                committed = true;
                return window;
            }
            finally
            {
                // 只有完成物理映射才移交给 Slot；此前任何取消/异常都必须释放 View、摘除可视树并释放 UXML handle。
                if (!committed)
                {
                    try
                    {
                        if (window != null) DisposeViewSafely(window, "回滚未提交窗口");
                        else root?.RemoveFromHierarchy();
                    }
                    finally
                    {
                        wbag?.Dispose();
                    }
                }
            }
        }

        public void BringToFront(IUIWindow window)
        {
            if (!_slots.TryGetValue(window, out var s)) return;
            // 遮罩先置顶、窗口再置顶 → 窗口在遮罩之上、遮罩紧贴其下（与 SetModalMask 的"铺在 owner 正下方"一致）。
            s.Mask?.BringToFront();
            s.Element.BringToFront();
        }

        public void SetVisible(IUIWindow window, bool visible)
        {
            if (_slots.TryGetValue(window, out var s))
                s.Element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void SetModalMask(IUIWindow ownerWindow, bool on)
        {
            if (!_slots.TryGetValue(ownerWindow, out var s)) return;

            if (on)
            {
                if (s.Mask != null) return;
                var mask = new VisualElement { name = "Modal Mask" };
                Stretch(mask);
                mask.pickingMode = PickingMode.Position; // 吃掉点击，拦截下层
                mask.style.backgroundColor = new Color(0f, 0f, 0f, 0.5f);
                int ownerIndex = s.Element.parent.IndexOf(s.Element);
                s.Element.parent.Insert(ownerIndex, mask); // 铺在 owner 正下方
                s.Mask = mask;
            }
            else if (s.Mask != null)
            {
                s.Mask.RemoveFromHierarchy();
                s.Mask = null;
            }
        }

        public void SetInputBlocked(bool blocked)
        {
            if (blocked)
            {
                if (_inputBlocker == null)
                {
                    _inputBlocker = new VisualElement { name = "Input Blocker" };
                    Stretch(_inputBlocker);
                    _inputBlocker.pickingMode = PickingMode.Position; // 透明但吃掉全部指针事件
                }
                if (_inputBlocker.parent == null) _document.rootVisualElement.Add(_inputBlocker);
                _inputBlocker.BringToFront(); // 盖所有层根之上
                _inputBlocker.style.display = DisplayStyle.Flex;
            }
            else if (_inputBlocker != null)
            {
                _inputBlocker.style.display = DisplayStyle.None;
            }
        }

        public void DestroyWindow(IUIWindow window)
        {
            if (!_slots.TryGetValue(window, out var s)) return;
            _slots.Remove(window);
            s.Mask?.RemoveFromHierarchy();
            try
            {
                DisposeViewSafely(s.View, "销毁窗口"); // Bag.Dispose + Root.RemoveFromHierarchy
            }
            finally
            {
                s.LoadBag?.Dispose(); // 释放 UXML handle
            }
        }

        public void Teardown()
        {
            foreach (var s in _slots.Values)
            {
                s.Mask?.RemoveFromHierarchy();
                try
                {
                    DisposeViewSafely(s.View, "拆除 UI 后端");
                }
                finally
                {
                    s.LoadBag?.Dispose();
                }
            }
            _slots.Clear();
            _loadBag?.Dispose();
            _loadBag = null;
            if (_inputBlocker != null) { _inputBlocker.RemoveFromHierarchy(); _inputBlocker = null; }
            foreach (var c in _layerRoots.Values) c.RemoveFromHierarchy();
            _layerRoots.Clear();
            _initialized = false;
        }

        // Toolkit View 的 OnDisposing 是业务可覆写 hook。它失败时，UIToolkitViewBase 仍会穷尽 Bag/Root 清理；
        // Adapter 再把异常送入 Log Seam，避免一个坏窗口阻断其它窗口与层根的物理拆除。
        private static void DisposeViewSafely(UIToolkitWindowBase view, string operation)
        {
            try
            {
                view?.Dispose();
            }
            catch (Exception exception)
            {
                Log.Error(
                    $"{operation}时，窗口 {view?.GetType().Name ?? "<unknown>"} 的 OnDisposing 抛出异常；" +
                    "视图 Bag 与可视树已继续清理。",
                    exception,
                    nameof(ToolkitBackend));
            }
        }

        private static void Stretch(VisualElement e)
        {
            e.style.position = Position.Absolute;
            e.style.left = 0;
            e.style.top = 0;
            e.style.right = 0;
            e.style.bottom = 0;
        }
    }
}
