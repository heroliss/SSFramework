using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Pool;
using R3;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

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
    /// - <b>只有「借出 + 跟随生命周期」的操作上 Bag</b>（Load / Rent / Spawn / 订阅 / 子作用域）；
    ///   纯查询（CheckLocationValid / IsNeedDownload）和用完即弃的工厂产物（CreateXxxDownloader）
    ///   走 <see cref="IAssetUtility"/>，避免 Bag 退化成 utility 的全量转发门面。
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

        // Rent / Spawn 借出的实例 → 其归还登记项的反查表，供 Return / Despawn 单个提前归还时摘除登记。
        // 惰性分配：不用池的 bag 零成本。key 用 object 同时容纳纯 C# 对象与 GameObject。
        // 必须按引用相等比较：池化类型可能重写 Equals/GetHashCode（值相等、可变字段参与哈希），
        // 默认 comparer 下实例状态改变后会查不到登记项。netstandard2.1 没有 ReferenceEqualityComparer（.NET 5+），自带一个。
        private Dictionary<object, IDisposable> _leased;

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
        /// 订阅无参 UnityEvent。<paramref name="evt"/> 为 null 时不订阅、返回空 Disposable，
        /// 且 Editor / Development Build 下 LogError（Inspector 漏配是场景搭建错误，应尽早暴露；
        /// 确属可选引用请在调用侧判空后再订阅，见「Inspector 引用默认 fail-fast」规则）。
        /// <paramref name="invokeImmediately"/> 为 true 时，注册后立刻调用一次 <paramref name="handler"/>。
        /// </summary>
        public IDisposable Subscribe(UnityEvent evt, UnityAction handler, bool invokeImmediately = false)
        {
            if (evt == null) { ErrorNullUnityEvent(); return Disposable.Empty; }
            evt.AddListener(handler);
            if (invokeImmediately) handler();
            return Track(Disposable.Create(() => evt.RemoveListener(handler)));
        }

        /// <summary>订阅带参 UnityEvent{T}。null 语义同无参重载：不订阅、返回空 Disposable，Editor/Dev 下 LogError。</summary>
        public IDisposable Subscribe<T>(UnityEvent<T> evt, UnityAction<T> handler)
        {
            if (evt == null) { ErrorNullUnityEvent(); return Disposable.Empty; }
            evt.AddListener(handler);
            return Track(Disposable.Create(() => evt.RemoveListener(handler)));
        }

        // 「Inspector 引用默认 fail-fast」规则的落点：null 事件几乎总是场景漏配，静默跳过会让按钮无响应的问题
        // 拖到交互阶段才暴露。Release 下保持容忍（编译消除），不因漏配崩掉玩家。
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        private static void ErrorNullUnityEvent()
            => Debug.LogError("[DisposableBag] Subscribe 收到 null UnityEvent——多半是 Inspector 漏配。" +
                              "确属可选引用请在调用侧判空后再订阅；Release 构建下忽略本次订阅。");

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
        /// 包<b>初始化失败</b>（CDN 不可达 / 断网）或<b>尚未初始化</b>（既没开自动初始化、也没 Initialize 过）→ <b>抛</b>异常（内部会先 EnsureInitialized）。
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

        /// <summary>直读文本内容：内容拷出即释放句柄（不进 Bag，纯数据无需托管）。普通 AB 包要求该 location 是文本类资产（.bytes/.txt/.json 等）；RawFile 包自动走原生通道。失败返回 null。</summary>
        public async UniTask<string> LoadText(string location, CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return await ResolveUtility().LoadText(location, LinkToken(ct));
        }

        /// <summary>从指定 package 直读文本内容；packageName 为空时使用默认包。语义同 <see cref="LoadText(string, CancellationToken)"/>。</summary>
        public async UniTask<string> LoadText(string packageName, string location, CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return await ResolveUtility().LoadText(packageName, location, LinkToken(ct));
        }

        /// <summary>直读二进制内容。语义同 <see cref="LoadText(string, CancellationToken)"/>。</summary>
        public async UniTask<byte[]> LoadBytes(string location, CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return await ResolveUtility().LoadBytes(location, LinkToken(ct));
        }

        /// <summary>从指定 package 直读二进制内容；packageName 为空时使用默认包。</summary>
        public async UniTask<byte[]> LoadBytes(string packageName, string location, CancellationToken ct = default)
        {
            EnsureUtility();
            ThrowIfDisposed();
            return await ResolveUtility().LoadBytes(packageName, location, LinkToken(ct));
        }

        /// <summary>
        /// 显式等待资源系统初始化完成。
        /// 加载方法内部已自动等待，业务一般无需手动调用；启动界面等需要"等资源系统就绪再进入"的场景才显式 await 它。
        /// <para>仅对「已自动初始化 / 已被 Initialize 触发」的包有意义：对既没开自动初始化、也没触发过的包调用会<b>抛</b>「未初始化」异常
        /// （而非无限等待）——这类包要先 <see cref="IAssetUtility.Initialize"/>。</para>
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

        // 注意：CheckLocationValid / IsNeedDownload / CreateXxxDownloader 这类「纯查询 / 工厂」入口刻意不在 Bag 上——
        // 它们不产生需要 bag 托管的生命周期（下载器用完即弃、查询无状态），走 this.GetUtility<IAssetUtility>()。
        // Bag 只收「借出 + 跟随生命周期」的操作（Load / Rent / Spawn / 订阅 / 子作用域）。

        // ── 对象池 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 从 <see cref="IPoolUtility"/> 的默认池租借一个实例；bag.Dispose 时自动归还。
        /// 与 <see cref="Load{T}(string, CancellationToken)"/> 的"借通道、自动释放"心智一致，业务无感知归还动作。
        /// 作用域内单个提前归还用本 bag 的 <see cref="Return{T}(T)"/>；
        /// 需要自定义工厂/钩子时，直接用 <c>this.GetUtility&lt;IPoolUtility&gt;()</c> 操作池。
        /// </summary>
        public T Rent<T>() where T : class, new()
        {
            EnsurePoolUtility();
            ThrowIfDisposed();
            var pool = ResolvePoolUtility().GetPool<T>();
            var instance = pool.Rent();
            TrackLeased(instance, Disposable.Create(() => pool.Return(instance)));
            return instance;
        }

        /// <summary>
        /// 把 <see cref="Rent{T}"/> 借出的实例提前归还到池，并摘除本 bag 的自动归还登记——bag.Dispose 时不会重复归还。
        /// 适合「子 bag 整批借出、作用域内逐个提前归还」（实例提前退场，剩余的随 bag.Dispose 整批归还）。
        /// 只对「本 bag 借出且尚未归还」的实例有效：外来实例 / 重复归还会被忽略（Editor/Dev 下 LogError）。
        /// bag 已 Dispose 后调用为无操作（实例已在 Dispose 时自动归还）。
        /// </summary>
        public void Return<T>(T instance) where T : class => ReleaseLeased(instance, nameof(Return));

        /// <summary>
        /// 从 <paramref name="prefab"/> 的 GameObject 池 Spawn 一个实例并挂到 <paramref name="parent"/>；
        /// bag.Dispose 时自动 Despawn（归还），心智同 <see cref="Rent{T}"/> / <see cref="Load{T}(string, CancellationToken)"/>。
        /// 实例若已被外部 Destroy（如随场景卸载）则跳过归还。位置加载先 <c>await Bag.Load&lt;GameObject&gt;(location)</c> 取得 prefab。
        /// <b>不要绕过 bag 直接对实例调池的 Despawn</b>（与 <see cref="Rent{T}"/> 同约定）：bag 的自动归还会叠加成
        /// 重复 Despawn——作用域内单个提前归还用本 bag 的 <see cref="Despawn(GameObject)"/>（归还同时摘除登记）。
        /// </summary>
        public GameObject Spawn(GameObject prefab, Transform parent = null)
        {
            EnsurePoolUtility();
            ThrowIfDisposed();
            var pool = ResolvePoolUtility().GetGameObjectPool(prefab);
            var go = pool.Spawn(parent);
            TrackLeased(go, Disposable.Create(() => { if (go != null) pool.Despawn(go); }));
            return go;
        }

        /// <summary>Spawn 一个实例并置于指定世界位置/旋转；bag.Dispose 时自动归还。见 <see cref="Spawn(GameObject, Transform)"/>。</summary>
        public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            EnsurePoolUtility();
            ThrowIfDisposed();
            var pool = ResolvePoolUtility().GetGameObjectPool(prefab);
            var go = pool.Spawn(position, rotation, parent);
            TrackLeased(go, Disposable.Create(() => { if (go != null) pool.Despawn(go); }));
            return go;
        }

        /// <summary>
        /// 把 <see cref="Spawn(GameObject, Transform)"/> 借出的实例提前 Despawn 归还，并摘除本 bag 的自动归还登记。
        /// 约定同 <see cref="Return{T}(T)"/>：仅限本 bag 借出且尚未归还的实例；bag 已 Dispose 后调用为无操作。
        /// </summary>
        public void Despawn(GameObject instance) => ReleaseLeased(instance, nameof(Despawn));

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
            // 租借反查表随之作废（实例已全部归还）；置空让实例/句柄可被 GC——bag 引用可能仍被宿主持有。
            _leased = null;
        }

        // ── 内部 ────────────────────────────────────────────────────────────

        private IDisposable Track(IDisposable d)
        {
            if (_disposed) { d?.Dispose(); return d; }
            _composite.Add(d);
            return d;
        }

        // Rent / Spawn 共用：归还登记进 composite（Dispose 自动归还）的同时记录「实例 → 登记项」反查，
        // 供 Return / Despawn 单个提前归还时摘除登记。
        private void TrackLeased(object instance, IDisposable handle)
        {
            if (_disposed) { handle.Dispose(); return; }
            _composite.Add(handle);
            (_leased ??= new Dictionary<object, IDisposable>(ReferenceComparer.Instance))[instance] = handle;
        }

        // Return / Despawn 共用实现：按实例反查归还登记项 → 同时摘出 _leased 与 composite → 触发归还。
        // 内存 O(当前借出数)：借出 +1、归还 -1，不随总租借次数增长。
        // 注意 CompositeDisposable.Remove 是对登记列表的线性查找（O(本 bag 总登记数)）——
        // 弹幕级高频热路径别走这里，用「领域 List + 手动池」。
        private void ReleaseLeased(object instance, string op)
        {
            if (instance == null) return;
            // Dispose 已把所有登记项归还，实例不再归本 bag 管，静默无操作。
            if (_disposed) return;
            if (_leased == null || !_leased.TryGetValue(instance, out var handle))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogError(
                    $"[DisposableBag] {op} target was not leased by this bag (or already returned). Ignored — " +
                    "early return must be called on the same bag that rented/spawned the instance.");
#endif
                return;
            }
            _leased.Remove(instance);
            // R3 CompositeDisposable.Remove 找到即移除（并按 Rx 约定 dispose）；下一行兜底 dispose 一次，
            // 让语义不依赖 Remove 的 dispose 行为——Disposable.Create 幂等，双 dispose 安全，
            // 最终 pool.Return / pool.Despawn 恰好执行一次。
            _composite.Remove(handle);
            handle.Dispose();
        }

        // 引用相等 comparer：netstandard2.1 没有 System.Collections.Generic.ReferenceEqualityComparer（.NET 5+）。
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
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
            // 但把旧 CTS 交给 _composite，随 bag.Dispose 一并释放：否则它会作为回调挂在长寿命源
            // （如 view destroy / ctx 令牌）上、直到该源取消才被 GC，构成「多 external 交替」场景下的隐性泄漏。
            // CTS.Dispose 幂等，且只有被替换下来的旧 CTS 进 composite（当前 _cachedLinkedCts 在 bag.Dispose 单独释放），不会重复。
            if (_cachedLinkedCts != null) _composite.Add(_cachedLinkedCts);
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
