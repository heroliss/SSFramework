using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;
using UnityEngine;

namespace Game.Framework.UI
{
    /// <summary>
    /// UI 框架核心编排——渲染中立的纯 C#：管窗口栈、层级、缓存、打开/关闭、cover/reveal、模态遮罩调度、参数传递。
    /// 所有渲染相关物理动作委托给 <see cref="IUIBackend"/>。adapter 的 Mono 入口（<c>MonoUGuiUI</c> / <c>MonoToolkitUI</c>）
    /// 各自 <c>new</c> 出对应 backend 后实例化本类并把 <see cref="IUIUtility"/> 调用转发过来。
    /// </summary>
    /// <remarks>
    /// 一类型一窗口（<c>Dictionary&lt;Type, IUIWindow&gt;</c>）——同一窗口类同时只存在一个实例，再次 <c>Open</c> 即置顶。
    /// cover/reveal 按<b>层内</b>计算：同层新窗口盖住前一个栈顶 → 前者 OnCover；栈顶关闭 → 新栈顶 OnReveal。
    /// 因为不持有 Unity 对象、只依赖注入的 backend 与 context，本类可脱离场景单测（fake backend + 桩 context）。
    /// </remarks>
    public sealed class UIUtility : IUIUtility
    {
        private readonly IGameContext _context;
        private readonly IUIBackend _backend;

        // 当前打开（可见）的窗口：类型 → 实例。
        private readonly Dictionary<Type, IUIWindow> _open = new();
        // 关闭后按 Cache 策略保留的隐藏窗口：类型 → 实例（再次打开秒显）。
        private readonly Dictionary<Type, IUIWindow> _cached = new();
        // 每层的打开顺序（末尾 = 栈顶），驱动 cover/reveal 与 CloseTop / Back。
        private readonly Dictionary<UILayer, List<IUIWindow>> _layers = new();

        private bool _initialized;
        private bool _disposed;

        public UIUtility(IGameContext context, IUIBackend backend)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public UniTask<T> Open<T>(CancellationToken ct = default) where T : class, IUIWindow
            => Open<T>(null, ct);

        public async UniTask<T> Open<T>(object args, CancellationToken ct = default) where T : class, IUIWindow
        {
            ThrowIfDisposed();
            EnsureInitialized();

            var type = typeof(T);

            // 已打开 → 置顶并重新 OnOpen（刷新参数），不重建。
            if (_open.TryGetValue(type, out var already))
            {
                var layer = UIWindowMeta.Of(type).Layer;
                var list = GetLayerList(layer);
                var curTop = list.Count > 0 ? list[list.Count - 1] : null;
                bool wasTop = curTop == already;
                MoveToTop(layer, already);
                _backend.BringToFront(already); // 始终置顶（已是栈顶则为无害 no-op）
                // 原本不在栈顶才有层内 cover/reveal 转换：旧栈顶被盖 → OnCover，自己重新露出 → OnReveal
                // （与全新打开路径的 cover 语义对称；已是栈顶则只刷新 OnOpen）。
                if (!wasTop)
                {
                    if (curTop != null) SafeHook(curTop.OnCover, curTop);
                    SafeHook(already.OnReveal, already);
                }
                SafeOnOpen(already, args);
                return (T)already;
            }

            var meta = UIWindowMeta.Of(type);

            IUIWindow window;
            if (_cached.TryGetValue(type, out var cached))
            {
                // 缓存命中：复用隐藏实例。
                _cached.Remove(type);
                window = cached;
                _backend.SetVisible(window, true);
                _backend.BringToFront(window);
            }
            else
            {
                // 新建：backend 加载资源 + 实例化 + 绑定 context。
                window = await _backend.CreateWindow(meta, _context, ct);
                if (window == null) return null; // 资源加载失败，已由资源系统打日志
                // 加载期间被释放、或 token 在加载完成后才被取消（竞态）：物理拆掉刚建好的窗口，不入栈。
                if (_disposed || ct.IsCancellationRequested) { _backend.DestroyWindow(window); return null; }
                SafeOnCreate(window);
            }

            var layerList = GetLayerList(meta.Layer);
            var prevTop = layerList.Count > 0 ? layerList[layerList.Count - 1] : null;
            layerList.Add(window);
            _open[type] = window;

            if (meta.Modal) _backend.SetModalMask(window, true);
            if (prevTop != null) SafeHook(prevTop.OnCover, prevTop);

            SafeOnOpen(window, args);
            return (T)window;
        }

        public void Close<T>() where T : class, IUIWindow => CloseType(typeof(T));

        public void Close(IUIWindow window)
        {
            if (window != null) CloseType(window.GetType());
        }

        public void CloseTop(UILayer layer)
        {
            var list = GetLayerList(layer);
            if (list.Count > 0) Close(list[list.Count - 1]);
        }

        public void Back() => CloseTop(UILayer.Page);

        public void CloseAll(UILayer layer)
        {
            var list = GetLayerList(layer);
            for (int i = list.Count - 1; i >= 0; i--) Close(list[i]); // 从栈顶往下关，cover/reveal 顺序自然
        }

        public void CloseAll()
        {
            foreach (UILayer layer in Enum.GetValues(typeof(UILayer))) CloseAll(layer);
        }

        public T Get<T>() where T : class, IUIWindow
            => _open.TryGetValue(typeof(T), out var w) ? (T)w : null;

        public bool IsOpen<T>() where T : class, IUIWindow => _open.ContainsKey(typeof(T));

        /// <summary>释放：拆掉所有窗口与层根。<b>不</b>触发窗口生命周期 hook（此时 Context 通常已在销毁、调 hook 会触碰已释放的 Context）——纯物理拆除，窗口各自的 Bag 由 backend 销毁时释放。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _open.Clear();
            _cached.Clear();
            _layers.Clear();
            _backend.Teardown();
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        private void CloseType(Type type)
        {
            if (!_open.TryGetValue(type, out var window)) return;
            var meta = UIWindowMeta.Of(type);
            var layerList = GetLayerList(meta.Layer);
            bool wasTop = layerList.Count > 0 && layerList[layerList.Count - 1] == window;

            SafeHook(window.OnClose, window);
            if (meta.Modal) _backend.SetModalMask(window, false);

            layerList.Remove(window);
            _open.Remove(type);

            // 关掉的是栈顶 → 新栈顶重新露出。
            if (wasTop && layerList.Count > 0) SafeHook(layerList[layerList.Count - 1].OnReveal, layerList[layerList.Count - 1]);

            if (meta.Cache == UICachePolicy.Cache && !_disposed)
            {
                _backend.SetVisible(window, false);
                _cached[type] = window;
            }
            else
            {
                _backend.DestroyWindow(window);
            }
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            _backend.Initialize();
            _initialized = true;
        }

        private List<IUIWindow> GetLayerList(UILayer layer)
        {
            if (!_layers.TryGetValue(layer, out var list))
            {
                list = new List<IUIWindow>();
                _layers[layer] = list;
            }
            return list;
        }

        private void MoveToTop(UILayer layer, IUIWindow window)
        {
            var list = GetLayerList(layer);
            if (list.Remove(window)) list.Add(window);
        }

        // 窗口 hook 隔离：单个窗口的回调抛异常不应连累框架（与 CommandSystem 的异常隔离一致）。
        private static void SafeOnCreate(IUIWindow w) => SafeHook(w.OnCreate, w);
        private static void SafeOnOpen(IUIWindow w, object args)
        {
            try { w.OnOpen(args); }
            catch (Exception e) { Debug.LogException(e); }
        }
        private static void SafeHook(Action hook, IUIWindow owner)
        {
            try { hook(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UIUtility));
        }
    }
}
