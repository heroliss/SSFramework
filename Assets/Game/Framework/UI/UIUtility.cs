using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;
using Game.Framework.Logging;
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
    public sealed class UIUtility : IUIUtility, ILoadingHandleOwner
    {
        private readonly IGameContext _context;
        private readonly IUIBackend _backend;
        // Toast 的时间推进是唯一的外部时钟依赖。生产走实时 PlayerLoop；内部构造注入只供确定性测试，
        // 避免 Unity Editor 后台节流把“owner 是否正确”误测成“某一墙钟区间是否恰好得到更新帧”。
        private readonly Func<TimeSpan, CancellationToken, UniTask> _toastDelay;

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

        // Toast / Loading 内置窗口类型表（adapter 入口提供）；null = 未配置，Show* 调用报错提示。
        private readonly UIBuiltinWindows _builtins;

        // ShowToast 的打开请求与自动关闭 owner 都由渲染中立核心持有：两个 adapter 只渲染文本。
        // request id 让 Close/CloseAll 在异步创建期间也能使旧请求失效；CTS 身份让迟到计时器不能关闭新 Toast。
        private readonly HashSet<long> _toastRequestIds = new();
        private long _nextToastRequestId;
        private long _latestToastCommittedRequestId;
        private CancellationTokenSource _toastAutoClose;

        // AcquireLoading 的并发所有权：集合大小就是占用计数，id 让重复 Dispose 与 CloseAll 后的陈旧句柄安全 no-op。
        private readonly HashSet<int> _loadingHandleIds = new();
        private int _nextLoadingHandleId;
        // 旧 Show/Hide 对作为一个兼容 owner；generation 让“打开途中 Hide/CloseAll”不会在加载完成后幽灵重现。
        // pending 也算 owner：窗口尚在创建时，别让其它 lease 的释放误把这次请求强制清场。
        private long _legacyLoadingGeneration;
        private bool _legacyLoadingHeld;
        private int _legacyLoadingPending;

        public UIUtility(IGameContext context, IUIBackend backend, UIBuiltinWindows builtins = null)
            : this(context, backend, builtins, DelayToastRealtime)
        {
        }

        internal UIUtility(
            IGameContext context,
            IUIBackend backend,
            UIBuiltinWindows builtins,
            Func<TimeSpan, CancellationToken, UniTask> toastDelay)
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _builtins = builtins;
            _toastDelay = toastDelay ?? throw new ArgumentNullException(nameof(toastDelay));
        }

        public UniTask<T> Open<T>(CancellationToken ct = default) where T : class, IUIWindow
            => Open<T>(null, ct);

        public async UniTask<T> Open<T>(object args, CancellationToken ct = default) where T : class, IUIWindow
            => (T)await OpenCore(typeof(T), args, ct);

        // Open 的非泛型主体：泛型壳与内置件（ShowToast/AcquireLoading 按注册的 Type 开窗）共用。
        private async UniTask<IUIWindow> OpenCore(Type type, object args, CancellationToken ct)
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            ThrowIfDisposed();
            EnsureInitialized();
            ct.ThrowIfCancellationRequested();

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
                    if (curTop != null) SafeHook(nameof(IUIWindow.OnCover), curTop.OnCover, curTop);
                    SafeHook(nameof(IUIWindow.OnReveal), already.OnReveal, already);
                }
                SafeOnOpen(already, args);
                return already;
            }

            // 同类型正在异步创建中（并发 Open）：等首个创建完成，再整体重走一遍——
            // 成功则命中上面的「已打开 → 置顶 + OnOpen(args) 刷新」路径（本次 args 生效），失败则由本次调用重试创建。
            if (_creating.TryGetValue(type, out var creating))
            {
                await CompleteOnMainThread(creating.Task.AttachExternalCancellation(ct));
                if (_disposed) return null;
                return await OpenCore(type, args, ct);
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
                    window = await CompleteOnMainThread(_backend.CreateWindow(meta, _context, ct));
                    if (window == null) return null; // 资源加载失败，已由资源系统打日志
                    // 加载期间被释放、或 token 在加载完成后才被取消（竞态）：物理拆掉刚建好的窗口，不入栈。
                    if (_disposed) { _backend.DestroyWindow(window); return null; }
                    if (ct.IsCancellationRequested)
                    {
                        _backend.DestroyWindow(window);
                        ct.ThrowIfCancellationRequested();
                    }
                    SafeOnCreate(window);
                }

                var layerList = GetLayerList(meta.Layer);
                var prevTop = layerList.Count > 0 ? layerList[layerList.Count - 1] : null;
                layerList.Add(window);
                _open[type] = window;

                if (meta.Modal) _backend.SetModalMask(window, true);
                if (prevTop != null) SafeHook(nameof(IUIWindow.OnCover), prevTop.OnCover, prevTop);

                SafeOnOpen(window, args);
                // 入场过渡（新建 / 缓存复用都播；已打开置顶刷新不播）。不 await——Open 在 OnOpen 后即返回，
                // 过渡是表现层的事，动画期间的防护由框架挡输入承担（ADR-0020）。
                StartOpenTransition(window);
                return window;
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
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            var list = GetLayerList(layer);
            if (list.Count > 0) Close(list[list.Count - 1]);
        }

        // 返回导航参与的层，从高到低。Top/System 不参与（Toast/系统提示不是导航单元）、Background 不参与（底景）。
        private static readonly UILayer[] BackLayers = { UILayer.Popup, UILayer.Window, UILayer.Page };

        public bool Back()
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
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
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            // Toast 可能仍在异步创建、尚未入栈；先使创建请求与旧计时器失效，避免清场后幽灵重现。
            if (IsToastLayer(layer)) InvalidateToastOwners();
            // Loading 可能尚在异步创建、还没进入层栈。先使其 owner/句柄失效，创建续体回来后会发现陈旧并立即关掉。
            if (IsLoadingLayer(layer)) InvalidateLoadingOwners();

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
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            return _open.TryGetValue(typeof(T), out var w) ? (T)w : null;
        }

        public bool IsOpen<T>() where T : class, IUIWindow
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            return _open.ContainsKey(typeof(T));
        }

        // ── Top 层内置件（ADR-0020 §4）：按注册的类型表开窗，业务对后端零感知 ──

        public async UniTask ShowToast(string text, float duration = 2f, CancellationToken ct = default)
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            if (_builtins?.Toast == null)
            {
                Log.Error("未注册 Toast 内置窗口类型（UIBuiltinWindows.Toast）——本后端入口未提供内置件。",
                    category: nameof(UIUtility));
                return;
            }

            long requestId = AllocateToastRequestId();
            _toastRequestIds.Add(requestId);
            try
            {
                var window = await OpenCore(_builtins.Toast, new UIToastArgs(text, duration), ct);
                // Close/CloseAll 可能发生在异步创建途中。被清场的旧请求不能安装计时器；若 backend
                // 不遵守 token 而迟到建出了窗口，则在没有更新请求/计时 owner 时立刻收口，避免幽灵 Toast。
                if (!_toastRequestIds.Remove(requestId))
                {
                    ReconcileToastVisibility();
                    return;
                }
                if (window == null) return;

                // OpenCore 唤醒同类型等待者时，较新的 Show continuation 可能先于首个创建者恢复。
                // 只允许调用序号更新的成功请求安装 timer，避免旧请求在最后恢复后把新 duration 覆盖回去。
                if (requestId <= _latestToastCommittedRequestId) return;
                _latestToastCommittedRequestId = requestId;
                StartToastAutoClose(window, duration);
            }
            catch
            {
                bool wasCurrent = _toastRequestIds.Remove(requestId);
                if (!wasCurrent) ReconcileToastVisibility();
                throw;
            }
        }

        public async UniTask<LoadingHandle> AcquireLoading(string text = null, CancellationToken ct = default)
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            ThrowIfDisposed();
            if (!TryGetLoadingType(out var loadingType)) return default;

            int id = AllocateLoadingHandleId();
            _loadingHandleIds.Add(id);
            try
            {
                var window = await OpenCore(loadingType, new UILoadingArgs(text), ct);
                // Close/CloseAll 可能发生在异步创建途中：此时 id 已被清掉，不得把陈旧所有权交给调用方，
                // 也不得让刚完成创建的窗口在无 owner 时留下来。
                if (window == null || !_loadingHandleIds.Contains(id))
                {
                    _loadingHandleIds.Remove(id);
                    ReconcileLoadingVisibility();
                    return default;
                }

                return new LoadingHandle(this, id);
            }
            catch
            {
                _loadingHandleIds.Remove(id);
                ReconcileLoadingVisibility();
                throw;
            }
        }

        [Obsolete("ShowLoading/HideLoading 仅用于旧源码迁移；请改用 using var loading = await AcquireLoading(text, ct)，由句柄表达并发所有权。", false)]
        public async UniTask ShowLoading(string text = null, CancellationToken ct = default)
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            ThrowIfDisposed();
            if (!TryGetLoadingType(out var loadingType)) return;

            // generation 只由 Hide/Close/CloseAll 推进；同一代内的重复 Show 都是同一个 legacy owner 的刷新请求。
            // pending 用计数而不是布尔：多个刷新重叠时，一个失败不能抹掉其它仍在创建的请求或既有 owner。
            long generation = _legacyLoadingGeneration;
            _legacyLoadingPending++;
            try
            {
                var window = await OpenCore(loadingType, new UILoadingArgs(text), ct);
                if (window != null && generation == _legacyLoadingGeneration)
                    _legacyLoadingHeld = true;
            }
            finally
            {
                // Hide/CloseAll 已换代时 pending 已被统一清零；旧续体不能再减新一代的计数。
                if (generation == _legacyLoadingGeneration)
                    _legacyLoadingPending--;
                // 打开途中可能发生 Hide/CloseAll；无论成功、失败或取消，都按最新 owner 状态复核。
                ReconcileLoadingVisibility();
            }
        }

        [Obsolete("ShowLoading/HideLoading 仅用于旧源码迁移；请释放 AcquireLoading 返回的 LoadingHandle，通常使用 using var 自动释放。", false)]
        public void HideLoading()
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            ++_legacyLoadingGeneration;
            _legacyLoadingHeld = false;
            ReconcileLoadingVisibility();
        }

        public bool IsLoadingActive(int id)
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            return !_disposed && id != 0 && _loadingHandleIds.Contains(id);
        }

        public void ReleaseLoading(int id)
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            if (_disposed || id == 0 || !_loadingHandleIds.Remove(id)) return;
            ReconcileLoadingVisibility();
        }

        /// <summary>释放：拆掉所有窗口与层根。<b>不</b>触发窗口生命周期 hook（此时 Context 通常已在销毁、调 hook 会触碰已释放的 Context）——纯物理拆除，窗口各自的 Bag 由 backend 销毁时释放。</summary>
        public void Dispose()
        {
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            if (_disposed) return;
            _disposed = true;
            InvalidateToastOwners();
            InvalidateLoadingOwners();
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
            MainThreadGuard.AssertMainThread(nameof(UIUtility));
            // Toast 的计时与创建请求都是核心 owner；显式关闭也必须先让旧 continuation 失效。
            if (IsToastType(type)) InvalidateToastOwners();
            // Close/CloseAll 是强制清场语义：让所有旧 handle 失效，之后新 Acquire 得到的新 id 不会被旧句柄误关。
            if (IsLoadingType(type)) InvalidateLoadingOwners();
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
                SafeHook(nameof(IUIWindow.OnReveal), layerList[layerList.Count - 1].OnReveal,
                    layerList[layerList.Count - 1]);

            // 出场过渡：批量关闭不播（场景切换要的是立刻干净）。hook 同步抛异常 → 记日志按无过渡走。
            var transition = UniTask.CompletedTask;
            CancellationToken ownerToken = default;
            if (!_batchClosing)
            {
                ownerToken = _context.CancellationToken;
                try { transition = window.OnCloseTransition(ownerToken); }
                catch (OperationCanceledException) when (ownerToken.IsCancellationRequested) { }
                catch (Exception e) { LogHookFailure(window, nameof(IUIWindow.OnCloseTransition), e); }
            }

            if (transition.Status == UniTaskStatus.Succeeded)
            {
                CompleteClose(window, meta, ownerToken); // 无过渡：同步走完，行为与旧版逐帧一致
                return;
            }
            RunCloseTransition(window, meta, transition, ownerToken).Forget();
        }

        private bool TryGetLoadingType(out Type loadingType)
        {
            loadingType = _builtins?.Loading;
            if (loadingType != null) return true;
            Log.Error("未注册 Loading 内置窗口类型（UIBuiltinWindows.Loading）——本后端入口未提供内置件。",
                category: nameof(UIUtility));
            return false;
        }

        private int AllocateLoadingHandleId()
        {
            do
            {
                unchecked { _nextLoadingHandleId++; }
                if (_nextLoadingHandleId <= 0) _nextLoadingHandleId = 1;
            } while (_loadingHandleIds.Contains(_nextLoadingHandleId));

            return _nextLoadingHandleId;
        }

        private long AllocateToastRequestId()
        {
            if (_nextToastRequestId == long.MaxValue)
                throw new InvalidOperationException("Toast 请求编号已耗尽——请重建 UIUtility 实例。");
            return ++_nextToastRequestId;
        }

        private bool IsToastType(Type type)
            => type != null && type == _builtins?.Toast;

        private bool IsToastLayer(UILayer layer)
            => _builtins?.Toast != null && UIWindowMeta.Of(_builtins.Toast).Layer == layer;

        private void InvalidateToastOwners()
        {
            _toastRequestIds.Clear();
            CancelToastAutoClose();
        }

        private void ReconcileToastVisibility()
        {
            if (_disposed || _toastRequestIds.Count > 0 || _toastAutoClose != null) return;
            if (_builtins?.Toast != null) CloseType(_builtins.Toast);
        }

        private void StartToastAutoClose(IUIWindow window, float duration)
        {
            // 先成功创建新 owner，再取消旧 owner；若 Context 已无法提供 token，既有 Toast 计时不受影响。
            var owner = CancellationTokenSource.CreateLinkedTokenSource(_context.CancellationToken);
            CancelToastAutoClose();
            _toastAutoClose = owner;
            RunToastAutoClose(window, duration, owner).Forget();
        }

        private async UniTaskVoid RunToastAutoClose(
            IUIWindow window,
            float duration,
            CancellationTokenSource owner)
        {
            bool shouldClose = false;
            // CTS 可能被刷新 / Close / Dispose 取消并立即 Dispose；先捕获值类型 token，catch filter
            // 不能在 owner 已释放后再访问 owner.Token。只有这份 owner token 真正取消的 OCE 才是正常收口。
            CancellationToken ownerToken = owner.Token;
            try
            {
                await CompleteOnMainThread(_toastDelay(TimeSpan.FromSeconds(duration), ownerToken));
                shouldClose = ReferenceEquals(_toastAutoClose, owner);
            }
            catch (OperationCanceledException) when (ownerToken.IsCancellationRequested)
            {
                // 刷新 / 显式关闭 / 清场 / Context Dispose：框架 owner 发起的正常取消。
            }
            catch (Exception e)
            {
                if (ReferenceEquals(_toastAutoClose, owner))
                {
                    Log.Error("Toast 自动关闭任务异常停止；框架将关闭当前 Toast，避免提示永久残留。",
                        e, nameof(UIUtility));
                    // 日志 sink 可能重入 ShowToast 并安装新 owner；只允许仍在位的失败任务关闭窗口。
                    shouldClose = ReferenceEquals(_toastAutoClose, owner);
                }
            }
            finally { ReleaseToastAutoClose(owner); }

            if (shouldClose && !_disposed)
                Close(window);
        }

        private void CancelToastAutoClose()
        {
            var owner = _toastAutoClose;
            if (owner == null) return;
            _toastAutoClose = null;
            try { owner.Cancel(); }
            finally { owner.Dispose(); }
        }

        private void ReleaseToastAutoClose(CancellationTokenSource owner)
        {
            if (!ReferenceEquals(_toastAutoClose, owner)) return;
            _toastAutoClose = null;
            owner.Dispose();
        }

        private static UniTask DelayToastRealtime(TimeSpan duration, CancellationToken ct)
            => UniTask.Delay(duration, ignoreTimeScale: true, cancellationToken: ct);

        private bool IsLoadingType(Type type)
            => type != null && type == _builtins?.Loading;

        private bool IsLoadingLayer(UILayer layer)
            => _builtins?.Loading != null && UIWindowMeta.Of(_builtins.Loading).Layer == layer;

        private void InvalidateLoadingOwners()
        {
            ++_legacyLoadingGeneration;
            _legacyLoadingHeld = false;
            _legacyLoadingPending = 0;
            _loadingHandleIds.Clear();
        }

        private void ReconcileLoadingVisibility()
        {
            if (_disposed || _legacyLoadingHeld || _legacyLoadingPending > 0 || _loadingHandleIds.Count > 0) return;
            if (_builtins?.Loading != null) CloseType(_builtins.Loading);
        }

        // 出场过渡期间挡输入；结束（含异常/取消）后走真正的关闭收尾。
        private async UniTaskVoid RunCloseTransition(
            IUIWindow window,
            UIWindowMeta meta,
            UniTask transition,
            CancellationToken ownerToken)
        {
            BeginTransition();
            try { await CompleteOnMainThread(transition); }
            catch (OperationCanceledException) when (ownerToken.IsCancellationRequested)
            {
                // Context 销毁级联取消：正常路径，无需日志。
            }
            catch (Exception e) { LogHookFailure(window, nameof(IUIWindow.OnCloseTransition), e); }
            finally
            {
                EndTransition();
                // Dispose 后 Teardown 已物理拆除全部窗口，这里不能再碰。
                if (!_disposed) CompleteClose(window, meta, ownerToken);
            }
        }

        // Context 已进入 terminal 时不能再调用业务 hook：GameContext.Dispose 先标记 disposed 再取消 token，
        // OnClose 中的 GetUtility / ExecuteCommand 此刻都会访问已释放 Context。窗口已从逻辑栈摘除，直接物理回收即可。
        private void CompleteClose(IUIWindow window, UIWindowMeta meta, CancellationToken ownerToken)
        {
            if (ownerToken.CanBeCanceled && ownerToken.IsCancellationRequested)
            {
                _backend.DestroyWindow(window);
                return;
            }

            FinishClose(window, meta);
        }

        // 关闭收尾：OnClose → 按缓存策略隐藏或销毁。
        private void FinishClose(IUIWindow window, UIWindowMeta meta)
        {
            SafeHook(nameof(IUIWindow.OnClose), window.OnClose, window);

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
            CancellationToken ownerToken = _context.CancellationToken;
            UniTask transition;
            try { transition = window.OnOpenTransition(ownerToken); }
            catch (OperationCanceledException) when (ownerToken.IsCancellationRequested) { return; }
            catch (Exception e) { LogHookFailure(window, nameof(IUIWindow.OnOpenTransition), e); return; }
            if (transition.Status == UniTaskStatus.Succeeded) return; // 默认无过渡：零开销
            RunOpenTransition(window, transition, ownerToken).Forget();
        }

        private async UniTaskVoid RunOpenTransition(
            IUIWindow window,
            UniTask transition,
            CancellationToken ownerToken)
        {
            BeginTransition();
            try { await CompleteOnMainThread(transition); }
            catch (OperationCanceledException) when (ownerToken.IsCancellationRequested) { }
            catch (Exception e) { LogHookFailure(window, nameof(IUIWindow.OnOpenTransition), e); }
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
        private static void SafeOnCreate(IUIWindow w)
            => SafeHook(nameof(IUIWindow.OnCreate), w.OnCreate, w);

        private static void SafeOnOpen(IUIWindow w, object args)
        {
            try { w.OnOpen(args); }
            catch (Exception e) { LogHookFailure(w, nameof(IUIWindow.OnOpen), e); }
        }

        private static void SafeHook(string hookName, Action hook, IUIWindow owner)
        {
            try { hook(); }
            catch (Exception e) { LogHookFailure(owner, hookName, e); }
        }

        private static void LogHookFailure(IUIWindow owner, string hookName, Exception exception)
            => Log.Error(
                $"窗口 {owner.GetType().Name} 的 {hookName} 抛出异常；异常已隔离，UI 编排继续。",
                exception,
                nameof(UIUtility));

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UIUtility));
        }

        // Adapter / hook / 测试时钟可以在任意线程完成。finally 既保留原始成功、异常和取消身份，
        // 又保证窗口字典、生命周期 hook 与后续 backend 调用只在 Unity 主线程提交。
        private static async UniTask CompleteOnMainThread(UniTask task)
        {
            try { await task; }
            finally { await UniTask.SwitchToMainThread(); }
        }

        private static async UniTask<T> CompleteOnMainThread<T>(UniTask<T> task)
        {
            try { return await task; }
            finally { await UniTask.SwitchToMainThread(); }
        }
    }
}
