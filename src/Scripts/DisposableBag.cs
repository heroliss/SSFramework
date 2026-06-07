using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Pool;
using R3;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Game.Framework
{
    /// <summary>
    /// 统一生命周期容器。把"要在某时刻清理的东西"集中托管：
    /// <list type="bullet">
    ///   <item>R3 / Framework Event / UnityEvent / C# event 订阅（自动包装为 IDisposable）</item>
    ///   <item>资源加载句柄（<c>Load</c> / <c>LoadScene</c> / 任意 <see cref="IAssetHandle{T}"/>）</item>
    ///   <item>任意自定义 <see cref="IDisposable"/>（包括嵌套的 DisposableBag）</item>
    /// </list>
    ///
    /// 设计要点：
    /// - 所有"持有 = 拥有生命周期"的对象都是 IDisposable，bag 是它们的根。
    /// - 子作用域用 <see cref="CreateChild"/>：child 是 IDisposable，自动登记到 parent，parent.Dispose 级联。
    /// - <c>Load</c> 直接返回 <c>T</c>：业务无感知句柄，handle 由 bag 持有，bag.Dispose 时统一释放。
    ///   想手动管理 handle 的高级用法走 <see cref="IAssetUtility"/>。
    ///
    /// 在 <see cref="View.MonoViewBase"/> / <see cref="Model.MonoModelBase"/> /
    /// <see cref="System.MonoSystemBase"/> / <see cref="Utility.MonoUtilityBase"/> 中通过 <c>Bag</c> 属性访问；
    /// 纯 C# 场景或 Command 内手动 <c>using var bag = ctx.CreateBag()</c>。
    ///
    /// 取消语义：bag.Dispose 时 <see cref="DisposeToken"/> 取消，正在进行的加载会收到 OCE 并清理底层 handle。
    /// </summary>
    public sealed class DisposableBag : IDisposable
    {
        private readonly CompositeDisposable _composite = new();
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly IGameContext _ctx;
        private IAssetUtility _utility;
        private IPoolUtility _poolUtility;
        private bool _disposed;

        // 单槽缓存：相同 external 复用同一 linked CTS，避免每次资源加载都分配新 CTS。
        // 典型 bag 在生命周期内只用 1 种 external（如 view destroy token / ctx token），命中率极高。
        private CancellationToken _cachedLinkedExternal;
        private CancellationTokenSource _cachedLinkedCts;

        /// <summary>不持有 Context 的 bag。仅能用于 R3 / UnityEvent / C# event / IDisposable 这几类不依赖 Context 的能力。</summary>
        public DisposableBag() { }

        /// <param name="ctx">用于 Framework Event 订阅与资源加载；不需要这两类能力时可省略。</param>
        public DisposableBag(IGameContext ctx) => _ctx = ctx;

        /// <summary>bag 销毁信号。资源加载内部会和外部 ct 链接到此 token，bag dispose 时统一取消。</summary>
        public CancellationToken DisposeToken => _disposeCts.Token;

        /// <summary>bag 是否已释放。</summary>
        public bool IsDisposed => _disposed;

        // ── Observable / R3 ──────────────────────────────────────────────────

        /// <summary>
        /// 订阅 Observable / ReactiveProperty。
        /// 需要错误/完成处理时，先用 R3 操作符（如 <c>OnErrorResumeAsFailure</c>、<c>Do(onCompleted)</c>）组合再传入。
        /// </summary>
        public IDisposable Subscribe<T>(Observable<T> source, Action<T> onNext)
            => Track(source.Subscribe(onNext));

        /// <summary>用自定义 Observer 订阅，覆盖 OnNext/OnErrorResume/OnCompleted。</summary>
        public IDisposable Subscribe<T>(Observable<T> source, Observer<T> observer)
            => Track(source.Subscribe(observer));

        // ── Framework Event ─────────────────────────────────────────────────

        /// <summary>订阅 Framework Event（带事件数据）。</summary>
        public IDisposable Subscribe<T>(Action<T> handler) where T : IEvent
        {
            EnsureContext();
            return Track(_ctx.RegisterEvent(handler));
        }

        /// <summary>
        /// 订阅 Framework Event（忽略事件数据）。
        /// <paramref name="invokeImmediately"/> 为 true 时，注册后立刻调用一次 <paramref name="handler"/>，
        /// 用于"订阅 + 初始化"合一的场景（事件本身无 current value，需要显式触发）。
        /// </summary>
        public IDisposable Subscribe<T>(Action handler, bool invokeImmediately = false) where T : IEvent
        {
            var disposable = Subscribe<T>(_ => handler());
            if (invokeImmediately) handler();
            return disposable;
        }

        // ── UnityEvent ──────────────────────────────────────────────────────

        /// <summary>
        /// 订阅无参 UnityEvent。<paramref name="evt"/> 为 null 时返回空 Disposable。
        /// <paramref name="invokeImmediately"/> 为 true 时，注册后立刻调用一次 <paramref name="handler"/>。
        /// </summary>
        public IDisposable Subscribe(UnityEvent evt, UnityAction handler, bool invokeImmediately = false)
        {
            if (evt == null) return Disposable.Empty;
            evt.AddListener(handler);
            if (invokeImmediately) handler();
            return Track(Disposable.Create(() => evt.RemoveListener(handler)));
        }

        /// <summary>订阅带参 UnityEvent{T}。<paramref name="evt"/> 为 null 时返回空 Disposable。</summary>
        public IDisposable Subscribe<T>(UnityEvent<T> evt, UnityAction<T> handler)
        {
            if (evt == null) return Disposable.Empty;
            evt.AddListener(handler);
            return Track(Disposable.Create(() => evt.RemoveListener(handler)));
        }

        // ── C# event / delegate（同时传入订阅与反订阅）──────────────────────

        /// <summary>
        /// 订阅 C# event/delegate：立即执行 <paramref name="subscribe"/>，
        /// Dispose 时自动调用 <paramref name="unsubscribe"/>。
        /// </summary>
        public IDisposable Subscribe(Action subscribe, Action unsubscribe)
        {
            subscribe();
            return Track(Disposable.Create(unsubscribe));
        }

        // ── 资源加载 ────────────────────────────────────────────────────────

        /// <summary>
        /// 按 location 加载资源。
        /// 返回 <c>T</c>，handle 自动登记到 bag；bag.Dispose 时统一释放。业务无需感知句柄。
        /// 取消传播：ct 和 <see cref="DisposeToken"/> 任一触发都会取消底层加载。
        /// <para><b>失败语义：</b>地址无效 / 类型不符 / 空地址 → 返回 <c>null</c>（不抛，打 warning/error）→ null 检查兜底；
        /// 包<b>初始化</b>失败（CDN 不可达 / 断网）→ <b>抛</b>初始化异常（内部会先 EnsureInitialized）。
        /// 「资源级问题给 null、系统级问题给异常」：包 Ready 后只返 null，要零异常就先 <see cref="EnsureInitialized(CancellationToken)"/> / 判 InitState=Ready 再加载。</para>
        /// </summary>
        public async UniTask<T> Load<T>(string location, CancellationToken ct = default)
            where T : UnityEngine.Object
        {
            EnsureUtility();
            ThrowIfDisposed();
            var handle = await ResolveUtility().Load<T>(location, LinkToken(ct));
            return TrackAsset(handle);
        }

        /// <summary>从指定 package 按 location 加载资源；packageName 为空时使用默认包。</summary>
        public async UniTask<T> Load<T>(string packageName, string location, CancellationToken ct = default)
            where T : UnityEngine.Object
        {
            EnsureUtility();
            ThrowIfDisposed();
            var handle = await ResolveUtility().Load<T>(packageName, location, LinkToken(ct));
            return TrackAsset(handle);
        }

        /// <summary>按 Inspector 序列化的 GUID 加载资源（供 <see cref="AssetReference{T}"/> 使用，业务一般用 <see cref="Load{T}"/>）。</summary>
        internal UniTask<IAssetHandle<T>> LoadByGuid<T>(string guid, CancellationToken ct = default)
            where T : UnityEngine.Object
            => LoadByGuid<T>(null, guid, ct);

        /// <summary>按 Inspector 序列化的 GUID 从指定 package 加载资源；供 <see cref="AssetReference{T}"/> 使用。</summary>
        internal async UniTask<IAssetHandle<T>> LoadByGuid<T>(string packageName, string guid, CancellationToken ct = default)
            where T : UnityEngine.Object
        {
            EnsureUtility();
            ThrowIfDisposed();
            var handle = await ResolveUtility().LoadByGuid<T>(packageName, guid, LinkToken(ct));
            if (handle == null) return null;
            // 注意：AssetReference 自己持有 handle，bag 不再 track，避免双重所有权。
            // bag 在这里只是借通道转发到 utility；AssetReference 跟随宿主 Dispose 时自己释放。
            return handle;
        }

        /// <summary>加载场景。返回 <see cref="ISceneHandle"/>，业务用它做 Activate / Unload；bag.Dispose 时若仍未卸载会自动 fire-and-forget 卸载。</summary>
        public async UniTask<ISceneHandle> LoadScene(
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            var handle = await ResolveUtility().LoadScene(location, mode, suspendLoad, LinkToken(ct));
            if (handle == null) return null;
            if (_disposed) { handle.Dispose(); return null; }
            _composite.Add(handle);
            return handle;
        }

        /// <summary>从指定 package 加载场景；packageName 为空时使用默认包。</summary>
        public async UniTask<ISceneHandle> LoadScene(
            string packageName,
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            var handle = await ResolveUtility().LoadScene(packageName, location, mode, suspendLoad, LinkToken(ct));
            if (handle == null) return null;
            if (_disposed) { handle.Dispose(); return null; }
            _composite.Add(handle);
            return handle;
        }

        /// <summary>加载 RawFile 文本内容。</summary>
        public async UniTask<string> LoadText(string location, CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return await ResolveUtility().LoadText(location, LinkToken(ct));
        }

        /// <summary>从指定 package 加载 RawFile 文本内容；packageName 为空时使用默认包。</summary>
        public async UniTask<string> LoadText(string packageName, string location, CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return await ResolveUtility().LoadText(packageName, location, LinkToken(ct));
        }

        /// <summary>加载 RawFile 二进制内容。</summary>
        public async UniTask<byte[]> LoadBytes(string location, CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return await ResolveUtility().LoadBytes(location, LinkToken(ct));
        }

        /// <summary>从指定 package 加载 RawFile 二进制内容；packageName 为空时使用默认包。</summary>
        public async UniTask<byte[]> LoadBytes(string packageName, string location, CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return await ResolveUtility().LoadBytes(packageName, location, LinkToken(ct));
        }

        /// <summary>
        /// 显式等待资源系统初始化完成。
        /// 加载方法内部已自动等待，业务一般无需手动调用；启动界面等需要"等资源系统就绪再进入"的场景才显式 await 它。
        /// </summary>
        public UniTask EnsureInitialized(CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return ResolveUtility().EnsureInitialized(LinkToken(ct));
        }

        /// <summary>显式等待指定 package 初始化完成；packageName 为空时使用默认包。</summary>
        public UniTask EnsureInitialized(string packageName, CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return ResolveUtility().EnsureInitialized(packageName, LinkToken(ct));
        }

        /// <summary>
        /// 把 <paramref name="target"/> 上所有 <see cref="AssetReference{T}"/> / <see cref="AssetReferenceList{T}"/> 字段
        /// 绑定到本 bag：加载用本 bag 的 utility、取消随 <see cref="DisposeToken"/>、handle 登记进本 bag（bag.Dispose 统一释放）。
        /// 一次绑完所有字段；<paramref name="target"/> 没有可绑定字段时为空操作。
        /// </summary>
        /// <remarks>
        /// 主要用于「持有 / 加载进来的 ScriptableObject 配置」：SO 不是 <c>MonoXxxBase</c>，字段不会在 Awake 自动绑定
        /// （框架刻意不递归 SO，见 <c>AssetReferenceBindPlan</c>），由加载 / 持有它的宿主用本方法一行把它内部的引用挂到自身生命周期。
        /// </remarks>
        public void BindAssetReferences(object target)
        {
            if (target == null) return;
            EnsureUtility();
            ThrowIfDisposed();
            AssetReferenceBindPlan.For(target.GetType()).Bind(target, ResolveUtility(), DisposeToken, this);
        }

        /// <summary>检查 location 是否能在当前 manifest 中解析；未初始化时返回 false。</summary>
        public bool CheckLocationValid(string location)
        {
            EnsureUtility();
            return ResolveUtility().CheckLocationValid(location);
        }

        /// <summary>检查指定 package 中 location 是否能在 manifest 中解析；packageName 为空时使用默认包。</summary>
        public bool CheckLocationValid(string packageName, string location)
        {
            EnsureUtility();
            return ResolveUtility().CheckLocationValid(packageName, location);
        }

        /// <summary>检查指定资源是否需要从远端下载；用于进入功能前的下载提示。</summary>
        public bool IsNeedDownload(string location)
        {
            EnsureUtility();
            return ResolveUtility().IsNeedDownload(location);
        }

        /// <summary>检查指定 package 中资源是否需要从远端下载；packageName 为空时使用默认包。</summary>
        public bool IsNeedDownload(string packageName, string location)
        {
            EnsureUtility();
            return ResolveUtility().IsNeedDownload(packageName, location);
        }

        /// <summary>
        /// 创建按 tag 统计和下载资源的任务。
        /// 返回的 downloader 不会自动登记到 bag——下载完成即结束，取消用 <c>Download</c> 的 ct。
        /// </summary>
        public IAssetDownloader CreateTagDownloader(params string[] tags)
        {
            EnsureUtility();
            return ResolveUtility().CreateTagDownloader(tags);
        }

        /// <summary>创建指定 package 的按 tag 统计和下载资源任务；packageName 为空时使用默认包。</summary>
        public IAssetDownloader CreateTagDownloader(string packageName, IReadOnlyList<string> tags)
        {
            EnsureUtility();
            return ResolveUtility().CreateTagDownloader(packageName, tags);
        }

        /// <summary>创建「下载该包全部尚未缓存 bundle」的下载器（无 tag 过滤，适合整包 / 整 DLC 全量预下）。</summary>
        public IAssetDownloader CreateAllDownloader()
        {
            EnsureUtility();
            return ResolveUtility().CreateAllDownloader();
        }

        /// <summary>创建指定 package 的全量下载器；packageName 为空时使用默认包。</summary>
        public IAssetDownloader CreateAllDownloader(string packageName)
        {
            EnsureUtility();
            return ResolveUtility().CreateAllDownloader(packageName);
        }

        /// <summary>创建「下载这些 location 资源所需 bundle（含依赖）」的下载器；解析不到的 location 跳过并打 warning。</summary>
        public IAssetDownloader CreateLocationDownloader(params string[] locations)
        {
            EnsureUtility();
            return ResolveUtility().CreateLocationDownloader(locations);
        }

        /// <summary>创建指定 package 的按 location 下载器；packageName 为空时使用默认包。</summary>
        public IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations)
        {
            EnsureUtility();
            return ResolveUtility().CreateLocationDownloader(packageName, locations);
        }

        // ── 对象池 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 从 <see cref="IPoolUtility"/> 的默认池租借一个实例；bag.Dispose 时自动归还。
        /// 与 <see cref="Load{T}(string, CancellationToken)"/> 的"借通道、自动释放"心智一致，业务无感知归还动作。
        /// 需要更早归还、或自定义工厂/钩子时，直接用 <c>this.GetUtility&lt;IPoolUtility&gt;()</c> 操作池。
        /// </summary>
        public T Rent<T>() where T : class, new()
        {
            EnsurePoolUtility();
            ThrowIfDisposed();
            var pool = ResolvePoolUtility().GetPool<T>();
            var instance = pool.Rent();
            Track(Disposable.Create(() => pool.Return(instance)));
            return instance;
        }

        /// <summary>
        /// 从 <paramref name="prefab"/> 的 GameObject 池 Spawn 一个实例并挂到 <paramref name="parent"/>；
        /// bag.Dispose 时自动 Despawn（归还），心智同 <see cref="Rent{T}"/> / <see cref="Load{T}(string, CancellationToken)"/>。
        /// 实例若已被外部 Destroy（如随场景卸载）则跳过归还。位置加载先 <c>await Bag.Load&lt;GameObject&gt;(location)</c> 取得 prefab。
        /// <b>不要对交给本方法的实例再手动 Despawn</b>（与 <see cref="Rent{T}"/> 同约定）：归还由 bag 负责，手动归还会与
        /// bag 的自动归还叠加成重复 Despawn——需要更早归还就别用 Bag.Spawn，直接走 <c>this.GetUtility&lt;IPoolUtility&gt;()</c>。
        /// </summary>
        public GameObject Spawn(GameObject prefab, Transform parent = null)
        {
            EnsurePoolUtility();
            ThrowIfDisposed();
            var pool = ResolvePoolUtility().GetGameObjectPool(prefab);
            var go = pool.Spawn(parent);
            Track(Disposable.Create(() => { if (go != null) pool.Despawn(go); }));
            return go;
        }

        /// <summary>Spawn 一个实例并置于指定世界位置/旋转；bag.Dispose 时自动归还。见 <see cref="Spawn(GameObject, Transform)"/>。</summary>
        public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            EnsurePoolUtility();
            ThrowIfDisposed();
            var pool = ResolvePoolUtility().GetGameObjectPool(prefab);
            var go = pool.Spawn(position, rotation, parent);
            Track(Disposable.Create(() => { if (go != null) pool.Despawn(go); }));
            return go;
        }

        // ── 通用挂载 / 嵌套 ─────────────────────────────────────────────────

        /// <summary>直接登记任意 IDisposable（订阅、handle、子 bag、自定义对象都可以）。</summary>
        public IDisposable Add(IDisposable disposable)
        {
            if (disposable == null) return null;
            if (_disposed) { disposable.Dispose(); return disposable; }
            _composite.Add(disposable);
            return disposable;
        }

        /// <summary>
        /// 创建子作用域。
        /// child 是 IDisposable，自动登记到 parent；parent.Dispose 时级联 dispose，child 单独 Dispose 也无副作用（Dispose 幂等）。
        /// 子作用域共享 parent 的 Context（IAssetUtility / Event 等能力直接可用）。
        /// </summary>
        public DisposableBag CreateChild()
        {
            ThrowIfDisposed();
            var child = new DisposableBag(_ctx);
            _composite.Add(child);
            return child;
        }

        // ── 释放 ────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCts.Cancel();
            _disposeCts.Dispose();
            // 缓存的 linked CTS 已被根 _disposeCts.Cancel 触发取消，这里仅释放底层资源。
            _cachedLinkedCts?.Dispose();
            _composite.Dispose();
        }

        // ── 内部 ────────────────────────────────────────────────────────────

        private IDisposable Track(IDisposable d)
        {
            if (_disposed) { d?.Dispose(); return d; }
            _composite.Add(d);
            return d;
        }

        private T TrackAsset<T>(IAssetHandle<T> handle) where T : UnityEngine.Object
        {
            if (handle == null) return null;
            if (_disposed) { handle.Dispose(); return null; }
            _composite.Add(handle);
            return handle.Asset;
        }

        private CancellationToken LinkToken(CancellationToken external)
        {
            // 没有外部取消时直接用 bag 的 dispose token，避免每次加载分配 linked CTS。
            if (!external.CanBeCanceled) return _disposeCts.Token;

            // 缓存命中：相同 external 复用同一 linked CTS（CancellationToken 比较的是 Source 引用，
            // 同源的 token 等值。view.GetCancellationTokenOnDestroy() / ctx.CancellationToken 都是稳定 Source）。
            if (_cachedLinkedCts != null && _cachedLinkedExternal == external)
                return _cachedLinkedCts.Token;

            // miss：换 external 时不立即 dispose 旧 CTS——已发出去的 token 仍可能在 await 中。
            // 旧 CTS 由 GC 回收（external 取消后回调被消费、CTS 可达性断开）；
            // 或在 bag 自身 dispose 时由根 _disposeCts.Cancel 顺带触发其取消。
            _cachedLinkedExternal = external;
            _cachedLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(external, _disposeCts.Token);
            return _cachedLinkedCts.Token;
        }

        private IAssetUtility ResolveUtility() => _utility ??= _ctx.GetUtility<IAssetUtility>();

        private IPoolUtility ResolvePoolUtility() => _poolUtility ??= _ctx.GetUtility<IPoolUtility>();

        private void EnsurePoolUtility()
        {
            if (_ctx == null) throw new InvalidOperationException(
                "[DisposableBag] Context is required for object pooling. " +
                "Construct with a ctx, or use MonoXxxBase.Bag which auto-binds the host context.");
        }

        private void EnsureContext()
        {
            if (_ctx == null) throw new InvalidOperationException(
                "[DisposableBag] Context is required for Framework Event subscriptions. " +
                "Construct with a ctx, or use MonoXxxBase.Bag which auto-binds the host context.");
        }

        private void EnsureUtility()
        {
            if (_ctx == null) throw new InvalidOperationException(
                "[DisposableBag] Context is required for asset loading. " +
                "Construct with a ctx, or use MonoXxxBase.Bag which auto-binds the host context.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DisposableBag));
        }
    }
}
