using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Utility;
using R3;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Framework
{
    /// <summary>
    /// <see cref="IAssetUtility"/> 的默认实现，挂在场景中的 Context 节点上。
    ///
    /// 职责边界：
    /// - 管理多个 package 的初始化状态、失败异常和等待入口；
    /// - 提供类型化加载 API，并处理 Component 请求到 GameObject prefab 的通用解析；
    /// - 把具体资源库的初始化、加载、handle 包装和下载器适配委托给 provider。
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

#if UNITY_EDITOR
        private float _editorSimulateDownloadSeconds;

        /// <summary>
        /// EditorSimulate 模式下，无需真实下载时模拟进度动画的时长（秒）。
        /// 由 <see cref="AssetInitSystem"/> 在 Configure 后注入，0 = 不模拟。
        /// 仅编辑器生效，整字段与逻辑都在 <c>#if UNITY_EDITOR</c> 包裹之内。
        /// </summary>
        internal float EditorSimulateDownloadSeconds
        {
            set => _editorSimulateDownloadSeconds = value;
        }
#endif

        public bool IsInitialized => GetState(_defaultPackageName).State.Value == AssetInitState.Ready;
        public AssetPlayMode CurrentPlayMode { get; private set; } = AssetPlayMode.EditorSimulate;
        public ReadOnlyReactiveProperty<AssetInitState> InitState => GetState(_defaultPackageName).State;

        protected override void Awake()
        {
            base.Awake();
            _disposeCts = new CancellationTokenSource();
            _provider = AssetProviderFactory.CreateDefault();
            GetState(_defaultPackageName);
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

            base.OnDestroy();
        }

        /// <summary>
        /// 由 <see cref="AssetInitSystem"/> 在初始化前写入运行时配置。重复调用会更新后续包初始化使用的配置。
        /// </summary>
        internal void Configure(string defaultPackageName, AssetProviderConfig config)
        {
            ThrowIfDisposed();
            _defaultPackageName = string.IsNullOrWhiteSpace(defaultPackageName) ? "DefaultPackage" : defaultPackageName;
            _config = config ?? new AssetProviderConfig();
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
                await state.InitTcs.Task.AttachExternalCancellation(token);
                return;
            }

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
                state.InitError = ex;
                state.State.Value = AssetInitState.Failed;
                state.InitTcs.TrySetException(ex);
                Debug.Log($"[AssetUtility] Package '{packageName}' initialization canceled.");
            }
            catch (Exception ex)
            {
                state.InitError = ex;
                state.State.Value = AssetInitState.Failed;
                state.InitTcs.TrySetException(ex);
                Debug.LogError($"[AssetUtility] Package '{packageName}' init failed: {ex.Message}");
            }
        }

        /// <summary>配置缺失导致默认包无法开始初始化时使用，让等待默认包的业务能收到明确异常。</summary>
        internal void FailDefaultInitialization(Exception exception)
        {
            var state = GetState(_defaultPackageName);
            var ex = exception ?? new InvalidOperationException("[AssetUtility] Asset initialization failed.");
            state.InitError = ex;
            state.State.Value = AssetInitState.Failed;
            state.InitTcs.TrySetException(ex);
        }

        public ReadOnlyReactiveProperty<AssetInitState> GetInitState(string packageName)
            => GetState(NormalizePackageName(packageName)).State;

        public UniTask EnsureInitialized(CancellationToken ct = default)
            => EnsureInitialized(_defaultPackageName, ct);

        public async UniTask EnsureInitialized(string packageName, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            var state = GetState(NormalizePackageName(packageName));
            if (state.State.Value == AssetInitState.Ready) return;
            if (state.State.Value == AssetInitState.Failed)
                throw state.InitError ?? new InvalidOperationException($"[AssetUtility] Package '{state.Name}' initialization failed.");

            if (!ct.CanBeCanceled)
            {
                await state.InitTcs.Task.AttachExternalCancellation(_disposeCts.Token);
                return;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            await state.InitTcs.Task.AttachExternalCancellation(linked.Token);
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
            return await _provider.LoadSceneAsync(packageName, location, mode, suspendLoad, ct);
        }

        public UniTask<string> LoadText(string location, CancellationToken ct = default)
            => LoadText(_defaultPackageName, location, ct);

        public async UniTask<string> LoadText(string packageName, string location, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[AssetUtility] RawFile location is empty.");
                return null;
            }

            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            return await _provider.LoadTextAsync(packageName, location, ct);
        }

        public UniTask<byte[]> LoadBytes(string location, CancellationToken ct = default)
            => LoadBytes(_defaultPackageName, location, ct);

        public async UniTask<byte[]> LoadBytes(string packageName, string location, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[AssetUtility] RawFile location is empty.");
                return null;
            }

            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            return await _provider.LoadBytesAsync(packageName, location, ct);
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

        private IAssetDownloader CreateTagDownloaderInternal(string packageName, IReadOnlyList<string> tags)
        {
            packageName = NormalizePackageName(packageName);
            if (GetState(packageName).State.Value != AssetInitState.Ready)
                throw new InvalidOperationException($"[AssetUtility] Create downloader before package '{packageName}' initialization completed.");
            var downloader = _provider.CreateTagDownloader(packageName, tags, _config.DownloadingMaxNumber, _config.FailedTryAgain);

#if UNITY_EDITOR
            // EditorSimulate 模式下所有资源都已就绪，downloader.TotalCount 必为 0，UI 上的下载流程会瞬间跳满。
            // 当 _editorSimulateDownloadSeconds > 0 时包装一层 SimulatedAssetDownloader，
            // 在配置时长内推进 Progress(0→1)，让开发者能真实体验和验证下载 UI。
            if (_editorSimulateDownloadSeconds > 0f
                && CurrentPlayMode == AssetPlayMode.EditorSimulate
                && downloader.TotalCount == 0)
            {
                return new SimulatedAssetDownloader(_editorSimulateDownloadSeconds);
            }
#endif
            return downloader;
        }

        private async UniTask<IAssetHandle<UnityEngine.Object>> LoadInternal(
            string packageName, string key, bool byGuid, Type type, CancellationToken ct)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            return await _provider.LoadAssetAsync(packageName, key, byGuid, type, ct);
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

        private string NormalizePackageName(string packageName)
            => string.IsNullOrWhiteSpace(packageName) ? _defaultPackageName : packageName;

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
