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
        // 正在异步创建中的窗口：类型 → 完成信号。并发 Open 同一类型时，后来者等首个创建完成再走「已打开」路径，
        // 避免两次 CreateWindow 各建一个实例、其中一个变成 _open 索引不到的孤儿。
        private readonly Dictionary<Type, UniTaskCompletionSource<IUIWindow>> _creating = new();
        // 关闭后按 Cache 策略保留的隐藏窗口：类型 → 实例（再次打开秒显）。
        private readonly Dictionary<Type, IUIWindow> _cached = new();
        // 每层的打开顺序（末尾 = 栈顶），驱动 cover/reveal 与 CloseTop / Back。
        private readonly Dictionary<UILayer, List<IUIWindow>> _layers = new();

        private bool _initialized;
        private bool _disposed;
        // CloseAll 批量关闭进行中：抑制关闭路径上的中间 OnReveal（见 CloseAll），且不播出场过渡（要的是立刻干净）。
        private bool _batchClosing;
        // 进行中的过渡数：>0 时 backend 全屏挡输入、Back() 直接吞掉（键盘路径不绕过挡板）。ADR-0020。
        private int _transitionCount;

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

            // 同类型正在异步创建中（并发 Open）：等首个创建完成，再整体重走一遍——
            // 成功则命中上面的「已打开 → 置顶 + OnOpen(args) 刷新」路径（本次 args 生效），失败则由本次调用重试创建。
            if (_creating.TryGetValue(type, out var creating))
            {
                await creating.Task.AttachExternalCancellation(ct);
                if (_disposed) return null;
                return await Open<T>(args, ct);
            }

            var meta = UIWindowMeta.Of(type);

            IUIWindow window;
            UniTaskCompletionSource<IUIWindow> creatingTcs = null;
            try
            {
                if (_cached.TryGetValue(type, out var cached))
                {
                    // 缓存命中：复用隐藏实例（同步路径，无需 _creating 守卫）。
                    _cached.Remove(type);
                    window = cached;
                    _backend.SetVisible(window, true);
                    _backend.BringToFront(window);
                }
                else
                {
                    // 新建：backend 加载资源 + 实例化 + 绑定 context。创建期间登记 _creating，让并发 Open 等待而非重复创建。
                    creatingTcs = new UniTaskCompletionSource<IUIWindow>();
                    _creating[type] = creatingTcs;
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
                // 入场过渡（新建 / 缓存复用都播；已打开置顶刷新不播）。不 await——Open 在 OnOpen 后即返回，
                // 过渡是表现层的事，动画期间的防护由框架挡输入承担（ADR-0020）。
                StartOpenTransition(window);
                return (T)window;
            }
            finally
            {
                // 摘除登记并唤醒等待者——必须放在 _open 写入之后（方法尾部）：
                // TrySetResult 的续体可能同步执行，若此时窗口尚未进 _open，等待者重走 Open 会再建一个实例。
                // 失败/取消路径 _open 里没有该类型 → 以 null 唤醒，等待者重走 Open 自行重试。
                if (creatingTcs != null)
                {
                    _creating.Remove(type);
                    creatingTcs.TrySetResult(_open.TryGetValue(type, out var opened) ? opened : null);
                }
            }
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

        // 返回导航参与的层，从高到低。Top/System 不参与（Toast/系统提示不是导航单元）、Background 不参与（底景）。
        private static readonly UILayer[] BackLayers = { UILayer.Popup, UILayer.Window, UILayer.Page };

        public bool Back()
        {
            ThrowIfDisposed();
            // 过渡进行中直接吞掉：与全屏挡输入同一语义，键盘/硬件返回键路径不绕过挡板（ADR-0020）。
            if (_transitionCount > 0) return true;

            foreach (var layer in BackLayers)
            {
                var list = GetLayerList(layer);
                if (list.Count == 0) continue;
                var top = list[list.Count - 1];
                // BackClosable=false 的栈顶：不动作但算消费——强引导窗口拦住返回键，防止业务误判「无 UI 可关」而退出。
                if (UIWindowMeta.Of(top.GetType()).BackClosable) Close(top);
                return true;
            }
            return false;
        }

        public void CloseAll(UILayer layer)
        {
            // 批量关闭抑制中间 reveal：从顶往下逐个关时，每个"新栈顶"下一刻就会被关掉，
            // 给它发 OnReveal 会让做「露出恢复」逻辑的窗口白跑一轮（恢复→立即关闭）。
            var list = GetLayerList(layer);
            _batchClosing = true;
            try
            {
                for (int i = list.Count - 1; i >= 0; i--) Close(list[i]);
            }
            finally
            {
                _batchClosing = false;
            }
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
            // 兜底唤醒仍在等「创建中窗口」的 Open 调用（以 null 唤醒，等待者检查 _disposed 后直接返回 null）。
            foreach (var tcs in _creating.Values) tcs.TrySetResult(null);
            _creating.Clear();
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

            // 逻辑关闭立即生效（ADR-0020）：摘栈、撤遮罩、露出下方——IsOpen 变 false、不再是 Back/CloseTop 目标、
            // 同类型可立即重开（新实例）。出场动画只是表现层残影，滞后于逻辑。
            layerList.Remove(window);
            _open.Remove(type);
            if (meta.Modal) _backend.SetModalMask(window, false);

            // 关掉的是栈顶 → 新栈顶重新露出（批量 CloseAll 时抑制——那个"新栈顶"马上也会被关掉）。
            if (wasTop && layerList.Count > 0 && !_batchClosing)
                SafeHook(layerList[layerList.Count - 1].OnReveal, layerList[layerList.Count - 1]);

            // 出场过渡：批量关闭不播（场景切换要的是立刻干净）。hook 同步抛异常 → 记日志按无过渡走。
            var transition = UniTask.CompletedTask;
            if (!_batchClosing)
            {
                try { transition = window.OnCloseTransition(_context.CancellationToken); }
                catch (Exception e) { Debug.LogException(e); }
            }

            if (transition.Status == UniTaskStatus.Succeeded)
            {
                FinishClose(window, meta); // 无过渡：同步走完，行为与旧版逐帧一致
                return;
            }
            RunCloseTransition(window, meta, transition).Forget();
        }

        // 出场过渡期间挡输入；结束（含异常/取消）后走真正的关闭收尾。
        private async UniTaskVoid RunCloseTransition(IUIWindow window, UIWindowMeta meta, UniTask transition)
        {
            BeginTransition();
            try { await transition; }
            catch (OperationCanceledException) { } // Context 销毁级联取消：正常路径，无需日志
            catch (Exception e) { Debug.LogException(e); }
            finally
            {
                EndTransition();
                // Dispose 后 Teardown 已物理拆除全部窗口，这里不能再碰。
                if (!_disposed) FinishClose(window, meta);
            }
        }

        // 关闭收尾：OnClose → 按缓存策略隐藏或销毁。
        private void FinishClose(IUIWindow window, UIWindowMeta meta)
        {
            SafeHook(window.OnClose, window);

            // 缓存入位前检查：出场动画期间同类型可能已被重新打开（_open 有新实例）或另一实例已入缓存——
            // 此时本实例已是孤儿，缓存它会永久泄漏（占坑且永不销毁），直接销毁。
            bool cacheable = meta.Cache == UICachePolicy.Cache && !_disposed
                             && !_open.ContainsKey(meta.WindowType) && !_cached.ContainsKey(meta.WindowType);
            if (cacheable)
            {
                _backend.SetVisible(window, false);
                _cached[meta.WindowType] = window;
            }
            else
            {
                _backend.DestroyWindow(window);
            }
        }

        // 入场过渡：不 await（Open 返回不等表现层）；进行中全屏挡输入。hook 同步抛异常 → 记日志视为无过渡。
        private void StartOpenTransition(IUIWindow window)
        {
            UniTask transition;
            try { transition = window.OnOpenTransition(_context.CancellationToken); }
            catch (Exception e) { Debug.LogException(e); return; }
            if (transition.Status == UniTaskStatus.Succeeded) return; // 默认无过渡：零开销
            RunOpenTransition(transition).Forget();
        }

        private async UniTaskVoid RunOpenTransition(UniTask transition)
        {
            BeginTransition();
            try { await transition; }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogException(e); }
            finally { EndTransition(); }
        }

        // 过渡计数 → 全屏挡板开关。1→挡、0→放；Dispose 后 backend 已 Teardown（挡板一并拆除），不再调它。
        private void BeginTransition()
        {
            if (++_transitionCount == 1 && !_disposed) _backend.SetInputBlocked(true);
        }

        private void EndTransition()
        {
            if (--_transitionCount == 0 && !_disposed) _backend.SetInputBlocked(false);
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
