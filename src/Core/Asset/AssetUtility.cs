using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Utility;
using R3;
using Sirenix.OdinInspector;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Framework
{
    /// <summary>
    /// <see cref="IAssetUtility"/> 的默认实现，挂在场景中的 Context 节点上。
    ///
    /// 职责边界：
    /// - 管理多个 package 的初始化状态、失败异常和等待入口；
    /// - 提供类型化加载 API 与加载结果的类型校验（<see cref="CastHandle{T}"/>）；
    /// - 把具体资源库的初始化、加载（含"Component 请求解析到 GameObject prefab 再取组件"）、handle 包装和下载器适配委托给 provider。
    ///
    /// 每次 Load 都返回独立 handle，调用方可以手动 Dispose；业务层通常通过 <see cref="DisposableBag"/> 托管。
    /// </summary>
    public class AssetUtility : MonoUtilityBase, IAssetUtility
    {
        private sealed class PackageState
        {
            public readonly string Name;
            public readonly ReactiveProperty<AssetInitState> State = new(AssetInitState.Idle);
            public UniTaskCompletionSource InitTcs = new();
            public Exception InitError;

            public PackageState(string name) => Name = name;
        }

        private readonly Dictionary<string, PackageState> _packages = new();
        private IAssetProvider _provider;
        private CancellationTokenSource _disposeCts;
        private string _defaultPackageName = "DefaultPackage";
        private AssetProviderConfig _config = new();
        private bool _disposedByDestroy;

        // 无默认包（DefaultPackageName 留空）时恒为 false——没有「默认包」可言。
        public bool IsInitialized =>
            !string.IsNullOrWhiteSpace(_defaultPackageName) &&
            GetState(_defaultPackageName).State.Value == AssetInitState.Ready;
        public AssetPlayMode CurrentPlayMode { get; private set; } = AssetPlayMode.EditorSimulate;
        public ReadOnlyReactiveProperty<AssetInitState> InitState => GetState(_defaultPackageName).State;

        // ── 运行时诊断（只读，仅 Play 模式显示）──
        // 摆出来的是 utility 自己的运行时状态，不是配置（配置真源在 AssetSystemConfigModel，规则 #19，故这里不回显 CDN 等 Model 字段，
        // 只显示 utility 解析/初始化的实际结果）。排查初始化失败 / 502 / 端口不一致时直接看「各包状态」。
        // Build 下无 Inspector，getter 不会被调用，零运行时成本。沿用 MonoLayerBase.ResolvedContext 的同一套 Odin 只读展示约定。
        [FoldoutGroup(DiagGroup), ShowInInspector, ReadOnly, HideInEditorMode, LabelText("运行模式"), PropertyOrder(-90)]
        [PropertyTooltip("当前生效的资源运行模式（首次初始化时由 AssetInitSystem 写入）。")]
        private AssetPlayMode InspectorPlayMode => CurrentPlayMode;

        [FoldoutGroup(DiagGroup), ShowInInspector, ReadOnly, HideInEditorMode, LabelText("默认包"), PropertyOrder(-89)]
        [PropertyTooltip("utility 解析出的默认资源包名。真源是 AssetSystemConfigModel.DefaultPackageName，经 Configure 写入；此处仅只读回看。")]
        private string InspectorDefaultPackage => _defaultPackageName;

        [FoldoutGroup(DiagGroup), ShowInInspector, ReadOnly, HideInEditorMode, LabelText("各包初始化状态"), PropertyOrder(-88)]
        [PropertyTooltip("每个已登记包的初始化状态（Idle / Pending / Initializing / Ready / Failed）。Ready 附当前资源版本、Failed 附简短原因——排查初始化失败 / 确认版本切换先看这里。")]
        private Dictionary<string, string> InspectorPackageStates
        {
            get
            {
                var view = new Dictionary<string, string>(_packages.Count);
                foreach (var kv in _packages)
                {
                    var st = kv.Value.State.Value;
                    view[kv.Key] = st switch
                    {
                        AssetInitState.Failed when kv.Value.InitError != null => $"{st} — {kv.Value.InitError.Message}",
                        AssetInitState.Ready => $"{st} — 版本 {GetPackageVersion(kv.Key) ?? "?"}",
                        _ => st.ToString(),
                    };
                }
                return view;
            }
        }

#if UNITY_EDITOR
        // 编辑器「模拟断网」开关：开启后 provider 的远端请求走不可达地址，使远端拉取（初始化 / 下载 / 需下载的 Load）失败。
        // 序列化且置于诊断折叠组外——它是可在进入 Play 前设置的「控制开关」而非「只读诊断」：已 Ready 的包不会因开关回退，
        // 故只有在包初始化前开启才能让其初始化失败。用 RP<bool> 让 Inspector 与订阅方实时同步。
        [SerializeField, LabelText("模拟断网（仅 Host/Web）"), PropertyOrder(-100)]
        [PropertyTooltip("开启 = 远端请求走不可达地址，远端拉取失败。仅编辑器 / 仅远端模式有意义；进 Play 前开启才能让初始化失败，已 Ready 的包不受影响。")]
        private RP<bool> _simulateOffline = new(false);

        ReadOnlyReactiveProperty<bool> IAssetUtility.SimulateOffline => _simulateOffline;
        void IAssetUtility.SetSimulateOffline(bool on) => _simulateOffline.Value = on;
#endif

        protected override void Awake()
        {
            base.Awake();
            _disposeCts = new CancellationTokenSource();
            _provider = AssetProviderFactory.CreateDefault();
#if UNITY_EDITOR
            _provider.SimulateOffline = () => _simulateOffline.CurrentValue; // 把开关接到 provider（实时读取当前值）
#endif
            // 默认包状态在 Configure（拿到真实默认包名后）按需建立；此处不预建，避免留下 field 默认名的「孤儿」状态。
        }

        protected override void OnDestroy()
        {
            _disposedByDestroy = true;
            _disposeCts?.Cancel();
            _disposeCts?.Dispose();
            _disposeCts = null;
            _provider?.Dispose();
            _provider = null;

            foreach (var state in _packages.Values)
            {
                state.InitTcs.TrySetCanceled();
                state.State.Dispose();
            }
            _packages.Clear();

#if UNITY_EDITOR
            _simulateOffline?.Dispose();
#endif
            base.OnDestroy();
        }

        /// <summary>
        /// 由 <see cref="AssetInitSystem"/> 在初始化前写入运行时配置、默认包名与运行模式。重复调用会更新后续包初始化使用的配置。
        /// <para>运行模式在此即写入 <see cref="CurrentPlayMode"/>（而非等到 <see cref="InitializePackageAsync"/>）：
        /// 某些包关闭自动初始化、延迟到业务显式 <c>Initialize</c> 触发时，仍能用正确模式初始化，而不是回落到默认值。</para>
        /// </summary>
        internal void Configure(string defaultPackageName, AssetProviderConfig config, AssetPlayMode mode)
        {
            ThrowIfDisposed();
            // 允许空默认包名（= 无默认包：不带 packageName 的便捷重载会清晰报错，而不是兜一个写死的名字）。
            _defaultPackageName = defaultPackageName?.Trim() ?? string.Empty;
            _config = config ?? new AssetProviderConfig();
            CurrentPlayMode = mode;
            if (!string.IsNullOrWhiteSpace(_defaultPackageName))
                GetState(_defaultPackageName);
        }

        /// <summary>
        /// 初始化单个 package。失败只记录到该 package 的状态，不向外抛出，避免阻断其他包初始化。
        /// </summary>
        internal async UniTask InitializePackageAsync(string packageName, AssetPlayMode mode, CancellationToken token)
        {
            ThrowIfDisposed();
            // 记录当前模式：所有包共享同一 PlayMode（由 AssetInitSystem 用 _settings.ActualPlayMode 串行调用）。
            // 即便业务后续手动用别的 mode 初始化别的包，CurrentPlayMode 反映最近一次的值，足够 UI 展示用。
            CurrentPlayMode = mode;
            packageName = NormalizePackageName(packageName);
            var state = GetState(packageName);

            if (state.State.Value == AssetInitState.Ready) return;
            if (state.State.Value == AssetInitState.Initializing)
            {
                // 别人正在初始化它：等本次完成即可。
                await state.InitTcs.Task.AttachExternalCancellation(token);
                return;
            }

            // 走到这里只剩 Idle / Pending / Failed —— 都由本次调用负责初始化。
            // Pending（已登记排队、尚未开跑）复用 GetState 时建的 fresh InitTcs，无需重置；只有 Failed 要重置 TCS/错误以便重试。
            if (state.State.Value == AssetInitState.Failed)
            {
                state.InitTcs = new UniTaskCompletionSource();
                state.InitError = null;
            }

            state.State.Value = AssetInitState.Initializing;
            try
            {
                if (token.CanBeCanceled)
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _disposeCts.Token);
                    await _provider.InitializeAsync(packageName, mode, _config, linked.Token);
                }
                else
                {
                    await _provider.InitializeAsync(packageName, mode, _config, _disposeCts.Token);
                }
                state.State.Value = AssetInitState.Ready;
                state.InitTcs.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                // 失败 / 取消都让 InitTcs 以「成功完成」收尾、错误另存进 InitError——
                // 若给 InitTcs 挂异常，而失败后又没人 await 它（EnsureInitialized 在 Failed 分支直接抛 InitError、不 await），
                // UniTask 会在该 Task 被回收时把它当 unobserved exception 再报一条。等待方醒来后按状态抛 InitError 即可。
                state.InitError = ex;
                state.State.Value = AssetInitState.Failed;
                state.InitTcs.TrySetResult();
                Debug.Log($"[AssetUtility] Package '{packageName}' initialization canceled.");
            }
            catch (Exception ex)
            {
                state.InitError = ex;
                state.State.Value = AssetInitState.Failed;
                state.InitTcs.TrySetResult(); // 同上：失败经 InitError 传递，不给 InitTcs 挂异常
                Debug.LogError($"[AssetUtility] Package '{packageName}' 初始化失败（模式 {mode}）：{ex.Message}\n" + InitFailureHint(mode));
            }
        }

        // 按运行模式给出最可能的失败原因。笼统地说「没构建/部署」会误导排查——例如 Host 下资源其实都对、
        // 只是本地 CDN 服务没起（或端口和配置不一致），清单根本拉不到，此时该提示去起服务 / 对端口，而不是去重新构建。
        private static string InitFailureHint(AssetPlayMode mode) => mode switch
        {
            AssetPlayMode.Host or AssetPlayMode.Web =>
                "拉远端清单失败：确认已①构建 ②部署资源，且远端 CDN 可达——本地联调还需③启动本地 CDN 服务，且服务端口与配置的 CDN 列表（AssetSystemConfigModel.CdnUrls）第一条端口一致。开发期可改回 EditorSimulate 免构建。",
            AssetPlayMode.Offline =>
                "读内置清单失败：确认已构建、且把 bundle 内置进首包（首包 Tags）。开发期可改回 EditorSimulate 免构建。",
            _ =>
                "EditorSimulate 一般无需构建；若仍失败，检查资源收集器 / 包名配置。",
        };

        /// <summary>配置缺失导致默认包无法开始初始化时使用，让等待默认包的业务能收到明确异常。</summary>
        internal void FailDefaultInitialization(Exception exception)
        {
            if (string.IsNullOrWhiteSpace(_defaultPackageName)) return; // 无默认包则无从置 Failed（业务本就该用 packageName 重载）
            var state = GetState(_defaultPackageName);
            var ex = exception ?? new InvalidOperationException("[AssetUtility] Asset initialization failed.");
            state.InitError = ex;
            state.State.Value = AssetInitState.Failed;
            state.InitTcs.TrySetResult(); // 见 InitializePackageAsync：失败经 InitError 传递，不给 InitTcs 挂异常
        }

        /// <summary>
        /// 把这些包标记为 <see cref="AssetInitState.Pending"/>（仅当前为 <see cref="AssetInitState.Idle"/> 时）。
        /// 由 <see cref="AssetInitSystem"/> 在批量自动初始化「逐个开跑前」统一调用：让「已登记会初始化、但还没轮到」的包
        /// 对并发 Load 表现为「等待」（Pending）而非「未初始化报错」（Idle）——消除批次窗口内的抢跑竞态。
        /// </summary>
        internal void MarkPackagesPending(IEnumerable<string> packageNames)
        {
            ThrowIfDisposed();
            if (packageNames == null) return;
            foreach (var packageName in packageNames)
            {
                if (string.IsNullOrWhiteSpace(packageName)) continue;
                var state = GetState(packageName);
                if (state.State.Value == AssetInitState.Idle)
                    state.State.Value = AssetInitState.Pending;
            }
        }

        /// <summary>
        /// 把仍停在 <see cref="AssetInitState.Pending"/>（已登记、但批次初始化没轮到就被中止）的包置 <see cref="AssetInitState.Failed"/>，
        /// 并唤醒其等待者。由 <see cref="AssetInitSystem"/> 在自动初始化批次结束（含被取消）时兜底调用：
        /// 否则这些包的 InitTcs 永不完成、后续 <c>EnsureInitialized</c> 会无限挂起——与「Pending 等待 / Idle 报错」契约相悖。
        /// 置 Failed（而非退回 Idle）让既有等待者醒来即拿到清晰异常；之后业务可 <see cref="Initialize"/> 重试。
        /// </summary>
        internal void AbandonPendingPackages()
        {
            if (_disposedByDestroy) return;
            foreach (var state in _packages.Values)
            {
                if (state.State.Value != AssetInitState.Pending) continue;
                state.InitError = new InvalidOperationException(
                    $"[AssetUtility] 包 '{state.Name}' 的初始化在开始前被中止；如需加载请重新 Initialize(\"{state.Name}\")。");
                state.State.Value = AssetInitState.Failed;
                state.InitTcs.TrySetResult(); // 见 InitializePackageAsync：失败经 InitError 传递，不给 InitTcs 挂异常
            }
        }

        public ReadOnlyReactiveProperty<AssetInitState> GetInitState(string packageName)
            => GetState(NormalizePackageName(packageName)).State;

        public UniTask EnsureInitialized(CancellationToken ct = default)
            => EnsureInitialized(_defaultPackageName, ct);

        public async UniTask EnsureInitialized(string packageName, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            var name = RequirePackage(packageName);
            var state = GetState(name);
            var current = state.State.Value;
            if (current == AssetInitState.Ready) return;
            if (current == AssetInitState.Failed)
                throw state.InitError ?? new InvalidOperationException($"[AssetUtility] Package '{name}' initialization failed.");
            // Idle = 既没开自动初始化、也没人 Initialize 过它：没人会去完成 InitTcs，等下去就是无限挂起——直接报错引导。
            if (current == AssetInitState.Idle)
                throw new InvalidOperationException(
                    $"[AssetUtility] 包 '{name}' 未初始化：它既没开启自动初始化、也没被 Initialize 触发过。" +
                    $"请在 AssetSystemConfigModel 的包列表里为它开启「自动初始化」，或在加载前先调 Initialize(\"{name}\")。");

            // 剩 Pending（已登记排队）/ Initializing（进行中）：等「初始化结束」。InitTcs 失败时也以成功完成收尾
            // （不挂异常，见 InitializePackageAsync），所以醒来后按状态判定：Failed 则抛 InitError。
            if (!ct.CanBeCanceled)
            {
                await state.InitTcs.Task.AttachExternalCancellation(_disposeCts.Token);
            }
            else
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                await state.InitTcs.Task.AttachExternalCancellation(linked.Token);
            }

            if (state.State.Value == AssetInitState.Failed)
                throw state.InitError ?? new InvalidOperationException($"[AssetUtility] Package '{name}' initialization failed.");
        }

        public async UniTask Initialize(string packageName = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            var name = RequirePackage(packageName);
            // 复用 AssetInitSystem 启动时 Configure 写入的 _config、以及上次记录的 CurrentPlayMode（AssetInitSystem 总在 Awake 先跑过一轮 Configure，
            // 此时 CurrentPlayMode 已是真实模式）。InitializePackageAsync 对 Idle / Pending / Failed 包会（重新）初始化、Ready 直接返回，
            // 故这里幂等；初始化失败不抛、结果写回 InitState（仅「未指定包又无默认包」这种调用方错误会经 RequirePackage 抛）。
            if (ct.CanBeCanceled)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                await InitializePackageAsync(name, CurrentPlayMode, linked.Token);
            }
            else
            {
                await InitializePackageAsync(name, CurrentPlayMode, _disposeCts.Token);
            }
        }

        public UniTask<IAssetHandle<T>> Load<T>(string location, CancellationToken ct = default)
            where T : UnityEngine.Object
            => Load<T>(_defaultPackageName, location, ct);

        public async UniTask<IAssetHandle<T>> Load<T>(string packageName, string location, CancellationToken ct = default)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[AssetUtility] Location is empty.");
                return null;
            }

            var handle = await LoadInternal(packageName, location, byGuid: false, typeof(T), ct);
            return CastHandle<T>(handle, location);
        }

        public UniTask<IAssetHandle<T>> LoadByGuid<T>(string guid, CancellationToken ct = default)
            where T : UnityEngine.Object
            => LoadByGuid<T>(_defaultPackageName, guid, ct);

        public async UniTask<IAssetHandle<T>> LoadByGuid<T>(string packageName, string guid, CancellationToken ct = default)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning("[AssetUtility] GUID is empty.");
                return null;
            }

            var handle = await LoadInternal(packageName, guid, byGuid: true, typeof(T), ct);
            return CastHandle<T>(handle, guid);
        }

        public UniTask<ISceneHandle> LoadScene(
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default)
            => LoadScene(_defaultPackageName, location, mode, suspendLoad, ct);

        public async UniTask<ISceneHandle> LoadScene(
            string packageName,
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[AssetUtility] Scene location is empty.");
                return null;
            }

            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            return await _provider.LoadSceneAsync(packageName, location, mode, suspendLoad, lct);
        }

        public UniTask<string> LoadText(string location, CancellationToken ct = default)
            => LoadText(_defaultPackageName, location, ct);

        public async UniTask<string> LoadText(string packageName, string location, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[AssetUtility] Text location is empty.");
                return null;
            }

            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            return await _provider.LoadTextAsync(packageName, location, lct);
        }

        public UniTask<byte[]> LoadBytes(string location, CancellationToken ct = default)
            => LoadBytes(_defaultPackageName, location, ct);

        public async UniTask<byte[]> LoadBytes(string packageName, string location, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[AssetUtility] Bytes location is empty.");
                return null;
            }

            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            return await _provider.LoadBytesAsync(packageName, location, lct);
        }

        public bool CheckLocationValid(string location)
            => CheckLocationValid(_defaultPackageName, location);

        public bool CheckLocationValid(string packageName, string location)
        {
            packageName = NormalizePackageName(packageName);
            return _provider != null && _provider.CheckLocationValid(packageName, location);
        }

        public bool IsNeedDownload(string location)
            => IsNeedDownload(_defaultPackageName, location);

        public bool IsNeedDownload(string packageName, string location)
        {
            packageName = NormalizePackageName(packageName);
            return _provider != null && _provider.IsNeedDownload(packageName, location);
        }

        public string GetPackageVersion(string packageName = null)
        {
            packageName = NormalizePackageName(packageName);
            if (_provider == null || string.IsNullOrWhiteSpace(packageName)) return null;
            return _provider.GetPackageVersion(packageName);
        }

        public IAssetDownloader CreateTagDownloader(params string[] tags)
        {
            ThrowIfDisposed();
            if (tags == null || tags.Length == 0)
                throw new ArgumentException("At least one tag is required.", nameof(tags));
            return CreateTagDownloaderInternal(_defaultPackageName, tags);
        }

        public IAssetDownloader CreateTagDownloader(string packageName, IReadOnlyList<string> tags)
        {
            ThrowIfDisposed();
            if (tags == null || tags.Count == 0)
                throw new ArgumentException("At least one tag is required.", nameof(tags));
            return CreateTagDownloaderInternal(packageName, tags);
        }

        public IAssetDownloader CreateAllDownloader()
            => CreateAllDownloader(_defaultPackageName);

        public IAssetDownloader CreateAllDownloader(string packageName)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            RequireReadyForDownloader(packageName);
            return _provider.CreateAllDownloader(packageName, _config.DownloadingMaxNumber, _config.FailedTryAgain);
        }

        public IAssetDownloader CreateLocationDownloader(params string[] locations)
        {
            ThrowIfDisposed();
            if (locations == null || locations.Length == 0)
                throw new ArgumentException("At least one location is required.", nameof(locations));
            return CreateLocationDownloaderInternal(_defaultPackageName, locations);
        }

        public IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations)
        {
            ThrowIfDisposed();
            if (locations == null || locations.Count == 0)
                throw new ArgumentException("At least one location is required.", nameof(locations));
            return CreateLocationDownloaderInternal(packageName, locations);
        }

        public UniTask ClearCache(AssetCacheClearMode mode = AssetCacheClearMode.Unused, CancellationToken ct = default)
            => ClearCache(_defaultPackageName, mode, ct);

        public async UniTask ClearCache(string packageName, AssetCacheClearMode mode = AssetCacheClearMode.Unused, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            // 清理「未使用」要对照该包当前清单判断哪些 bundle 该删，所以先确保初始化完成。
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            await _provider.ClearCacheAsync(packageName, mode, lct);
        }

        public UniTask ClearCacheByTags(IReadOnlyList<string> tags, CancellationToken ct = default)
            => ClearCacheByTags(_defaultPackageName, tags, ct);

        public async UniTask ClearCacheByTags(string packageName, IReadOnlyList<string> tags, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (tags == null || tags.Count == 0)
                throw new ArgumentException("At least one tag is required.", nameof(tags));
            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            await _provider.ClearCacheByTagsAsync(packageName, tags, lct);
        }

        public UniTask ClearCacheByLocations(IReadOnlyList<string> locations, CancellationToken ct = default)
            => ClearCacheByLocations(_defaultPackageName, locations, ct);

        public async UniTask ClearCacheByLocations(string packageName, IReadOnlyList<string> locations, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (locations == null || locations.Count == 0)
                throw new ArgumentException("At least one location is required.", nameof(locations));
            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            await _provider.ClearCacheByLocationsAsync(packageName, locations, lct);
        }

        public UniTask UnloadUnusedAssets(CancellationToken ct = default)
            => UnloadUnusedAssets(_defaultPackageName, ct);

        public async UniTask UnloadUnusedAssets(string packageName, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            await _provider.UnloadUnusedAssetsAsync(packageName, lct);
        }

        private IAssetDownloader CreateTagDownloaderInternal(string packageName, IReadOnlyList<string> tags)
        {
            packageName = NormalizePackageName(packageName);
            RequireReadyForDownloader(packageName);
            return _provider.CreateTagDownloader(packageName, tags, _config.DownloadingMaxNumber, _config.FailedTryAgain);
        }

        private IAssetDownloader CreateLocationDownloaderInternal(string packageName, IReadOnlyList<string> locations)
        {
            packageName = NormalizePackageName(packageName);
            RequireReadyForDownloader(packageName);
            return _provider.CreateLocationDownloader(packageName, locations, _config.DownloadingMaxNumber, _config.FailedTryAgain);
        }

        // 三种下载器（tag / 全部 / 按地址）共用：建下载器前必须包已就绪，否则统计不出待下载清单。
        private void RequireReadyForDownloader(string packageName)
        {
            if (GetState(packageName).State.Value != AssetInitState.Ready)
                throw new InvalidOperationException($"[AssetUtility] Create downloader before package '{packageName}' initialization completed.");
        }

        private async UniTask<IAssetHandle<UnityEngine.Object>> LoadInternal(
            string packageName, string key, bool byGuid, Type type, CancellationToken ct)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            return await _provider.LoadAssetAsync(packageName, key, byGuid, type, lct);
        }

        private static IAssetHandle<T> CastHandle<T>(IAssetHandle<UnityEngine.Object> handle, string key)
            where T : UnityEngine.Object
        {
            if (handle == null) return null;
            if (handle.Asset is T asset)
                return new TypedAssetHandle<T>(handle, asset);

            Debug.LogError($"[AssetUtility] Loaded asset '{key}' cannot be used as '{typeof(T).Name}'.");
            handle.Dispose();
            return null;
        }

        private PackageState GetState(string packageName)
        {
            packageName = NormalizePackageName(packageName);
            if (_packages.TryGetValue(packageName, out var state)) return state;
            state = new PackageState(packageName);
            _packages.Add(packageName, state);
            return state;
        }

        // 把空 packageName 解析成默认包（默认包也可能为空）。被动用：仅查询 / 取状态，空结果让下游自然得到 not-ready，不抛。
        private string NormalizePackageName(string packageName)
            => string.IsNullOrWhiteSpace(packageName) ? _defaultPackageName : packageName;

        // 发起真正操作（加载 / 等待 / 初始化）前用：解析后若仍为空（未配置默认包且未指定 packageName），当场清晰报错，
        // 不把空包名一路带到 provider / 状态机产生晦涩错误。
        private string RequirePackage(string packageName)
        {
            var name = NormalizePackageName(packageName);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException(
                    "[AssetUtility] 未配置默认资源包（AssetSystemConfigModel.DefaultPackageName 为空），且本次未指定 packageName——" +
                    "请配置默认包，或改用带 packageName 的重载（如 Load(packageName, location)）。");
            return name;
        }

        // 把调用方 ct 与 utility 销毁令牌（_disposeCts）链接：加载 / 缓存操作途中 utility 被销毁
        // （OnDestroy → _disposeCts.Cancel）时，在飞行的 provider 调用也会被取消，而非只依赖 provider.Dispose() 兜底——
        // 与初始化路径（InitializePackageAsync / EnsureInitialized）的链接方式一致。无外部 ct 时直接用 _disposeCts.Token，不分配 CTS。
        // 返回的 CTS 须由调用方 using 释放（无外部 ct 时返回 null，using null 为安全空操作）。
        private CancellationTokenSource LinkDispose(CancellationToken ct, out CancellationToken linked)
        {
            if (!ct.CanBeCanceled) { linked = _disposeCts.Token; return null; }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            linked = cts.Token;
            return cts;
        }

        private void ThrowIfDisposed()
        {
            if (_disposedByDestroy)
                throw new ObjectDisposedException(nameof(AssetUtility));
        }

        private sealed class TypedAssetHandle<T> : IAssetHandle<T> where T : UnityEngine.Object
        {
            private IAssetHandle<UnityEngine.Object> _inner;

            public TypedAssetHandle(IAssetHandle<UnityEngine.Object> inner, T asset)
            {
                _inner = inner;
                Asset = asset;
            }

            public T Asset { get; private set; }
            public bool IsValid => _inner != null && _inner.IsValid;

            public void Dispose()
            {
                if (_inner == null) return;
                _inner.Dispose();
                _inner = null;
                Asset = null;
            }
        }
    }
}
