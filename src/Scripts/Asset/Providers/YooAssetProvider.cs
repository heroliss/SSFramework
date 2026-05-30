using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using R3;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;

namespace Game.Framework
{
    /// <summary>
    /// 基于 YooAsset 的资源 provider 实现。
    ///
    /// 本类是框架内唯一直接接触 YooAsset API 的生产代码边界：全局初始化、Package 创建/复用、
    /// manifest 更新、handle 包装、下载器适配都收口在这里。上层 AssetUtility 只按 packageName
    /// 管理状态，不知道底层资源库的类型和初始化细节。
    /// </summary>
    internal sealed class YooAssetProvider : IAssetProvider
    {
        private readonly Dictionary<string, ResourcePackage> _packages = new();
        private bool _disposed;

        public async UniTask InitializeAsync(string packageName, AssetPlayMode mode, AssetProviderConfig config, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(packageName))
                throw new ArgumentException("Package name is required.", nameof(packageName));
            config ??= new AssetProviderConfig();

            if (!YooAssets.Initialized)
                YooAssets.Initialize();

            var package = YooAssets.TryGetPackage(packageName) ?? YooAssets.CreatePackage(packageName);

            if (package.InitializeStatus != EOperationStatus.Succeed)
            {
                var initOp = package.InitializeAsync(CreateInitParameters(packageName, mode, config));
                await WaitOp(initOp, ct);
                ct.ThrowIfCancellationRequested();

                if (initOp.Status != EOperationStatus.Succeed)
                    throw new InvalidOperationException($"[YooAssetProvider] Package '{packageName}' initialize failed: {initOp.Error}");
            }

            if (!package.PackageValid)
                await UpdateManifestAsync(packageName, package, ct);

            _packages[packageName] = package;
        }

        public bool IsPackageReady(string packageName)
            => !_disposed && !string.IsNullOrEmpty(packageName) &&
               _packages.TryGetValue(packageName, out var package) &&
               package.InitializeStatus == EOperationStatus.Succeed &&
               package.PackageValid;

        public async UniTask<IAssetHandle<UnityEngine.Object>> LoadAssetAsync(
            string packageName, string locationOrGuid, bool byGuid, Type type, CancellationToken ct)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            if (string.IsNullOrEmpty(locationOrGuid))
            {
                Debug.LogWarning("[YooAssetProvider] Asset location/GUID is empty.");
                return null;
            }

            type ??= typeof(UnityEngine.Object);
            var loadType = typeof(Component).IsAssignableFrom(type) ? typeof(GameObject) : type;
            var assetInfo = byGuid
                ? package.GetAssetInfoByGUID(locationOrGuid, loadType)
                : package.GetAssetInfo(locationOrGuid, loadType);

            if (assetInfo == null || !assetInfo.IsValid)
            {
                Debug.LogError($"[YooAssetProvider] Asset not found in package '{packageName}': {locationOrGuid}. {assetInfo?.Error}");
                return null;
            }

            var handle = package.LoadAssetAsync(assetInfo);
            try
            {
                await handle.Task.AsUniTask().AttachExternalCancellation(ct);
            }
            catch
            {
                handle.Release();
                throw;
            }

            if (handle.Status != EOperationStatus.Succeed)
            {
                Debug.LogError($"[YooAssetProvider] Load failed in package '{packageName}': {locationOrGuid}, {handle.LastError}");
                handle.Release();
                return null;
            }

            var asset = ResolveLoadedObject(handle.AssetObject, type);
            if (asset == null)
            {
                Debug.LogError(
                    $"[YooAssetProvider] Loaded asset '{locationOrGuid}' in package '{packageName}' cannot be used as '{type.Name}'. " +
                    $"Actual type: '{handle.AssetObject?.GetType().Name ?? "null"}'.");
                handle.Release();
                return null;
            }

            return new YooAssetHandle(handle, asset);
        }

        public async UniTask<ISceneHandle> LoadSceneAsync(
            string packageName, string location, LoadSceneMode mode, bool suspendLoad, CancellationToken ct)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[YooAssetProvider] Scene location is empty.");
                return null;
            }

            var handle = package.LoadSceneAsync(location, mode, LocalPhysicsMode.None, suspendLoad);
            try
            {
                await handle.Task.AsUniTask().AttachExternalCancellation(ct);
            }
            catch
            {
                handle.UnloadAsync();
                throw;
            }

            if (handle.Status != EOperationStatus.Succeed)
            {
                Debug.LogError($"[YooAssetProvider] Load scene failed in package '{packageName}': {location}, {handle.LastError}");
                handle.UnloadAsync();
                return null;
            }

            return new YooSceneHandle(handle);
        }

        public async UniTask<string> LoadTextAsync(string packageName, string location, CancellationToken ct)
        {
            var handle = await LoadRawFileHandle(packageName, location, ct);
            if (handle == null) return null;
            try { return handle.GetRawFileText(); }
            finally { handle.Release(); }
        }

        public async UniTask<byte[]> LoadBytesAsync(string packageName, string location, CancellationToken ct)
        {
            var handle = await LoadRawFileHandle(packageName, location, ct);
            if (handle == null) return null;
            try { return handle.GetRawFileData(); }
            finally { handle.Release(); }
        }

        public bool CheckLocationValid(string packageName, string location)
        {
            if (!IsPackageReady(packageName) || string.IsNullOrEmpty(location)) return false;
            return _packages[packageName].CheckLocationValid(location);
        }

        public bool IsNeedDownload(string packageName, string location)
        {
            if (!IsPackageReady(packageName) || string.IsNullOrEmpty(location)) return false;
            return _packages[packageName].IsNeedDownloadFromRemote(location);
        }

        public IAssetDownloader CreateTagDownloader(
            string packageName, IReadOnlyList<string> tags, int maxConcurrent, int retries)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            if (tags == null || tags.Count == 0)
                throw new ArgumentException("At least one tag is required.", nameof(tags));

            var tagArray = new string[tags.Count];
            for (int i = 0; i < tags.Count; i++)
                tagArray[i] = tags[i];
            var op = package.CreateResourceDownloader(tagArray, maxConcurrent, retries);
            return new AssetDownloader(op);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _packages.Clear();
        }

        private async UniTask<BundleFileHandle> LoadRawFileHandle(string packageName, string location, CancellationToken ct)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[YooAssetProvider] RawFile location is empty.");
                return null;
            }

            var handle = package.LoadRawFileAsync(location);
            try { await handle.Task.AsUniTask().AttachExternalCancellation(ct); }
            catch { handle.Release(); throw; }

            if (handle.Status == EOperationStatus.Succeed) return handle;
            Debug.LogError($"[YooAssetProvider] Load raw file failed in package '{packageName}': {location}, {handle.LastError}");
            handle.Release();
            return null;
        }

        private ResourcePackage GetReadyPackage(string packageName)
        {
            if (IsPackageReady(packageName)) return _packages[packageName];
            throw new InvalidOperationException($"[YooAssetProvider] Package '{packageName}' is not initialized or manifest is unavailable.");
        }

        private static async UniTask UpdateManifestAsync(string packageName, ResourcePackage package, CancellationToken token)
        {
            var versionOp = package.RequestPackageVersionAsync();
            await WaitOp(versionOp, token);
            token.ThrowIfCancellationRequested();

            if (versionOp.Status != EOperationStatus.Succeed)
                throw new InvalidOperationException($"[YooAssetProvider] Request package version failed for '{packageName}': {versionOp.Error}");

            var manifestOp = package.UpdatePackageManifestAsync(versionOp.PackageVersion);
            await WaitOp(manifestOp, token);
            token.ThrowIfCancellationRequested();

            if (manifestOp.Status != EOperationStatus.Succeed)
                throw new InvalidOperationException($"[YooAssetProvider] Update manifest failed for '{packageName}': {manifestOp.Error}");
        }

        private static InitializeParameters CreateInitParameters(string packageName, AssetPlayMode mode, AssetProviderConfig config)
        {
            switch (mode)
            {
#if UNITY_EDITOR
                case AssetPlayMode.EditorSimulate:
                {
                    var simulateResult = EditorSimulateModeHelper.SimulateBuild(packageName);
                    return new EditorSimulateModeParameters
                    {
                        EditorFileSystemParameters =
                            FileSystemParameters.CreateDefaultEditorFileSystemParameters(simulateResult.PackageRootDirectory)
                    };
                }
#endif
                case AssetPlayMode.Offline:
                    return new OfflinePlayModeParameters
                    {
                        BuildinFileSystemParameters =
                            FileSystemParameters.CreateDefaultBuildinFileSystemParameters(new GameDecryptionServices(config.FileOffset))
                    };

                case AssetPlayMode.Host:
                {
                    var remoteServices = new GameRemoteServices(config.MainCdnUrl, config.FallbackCdnUrl);
                    var decryptionServices = new GameDecryptionServices(config.FileOffset);
                    return new HostPlayModeParameters
                    {
                        BuildinFileSystemParameters =
                            FileSystemParameters.CreateDefaultBuildinFileSystemParameters(decryptionServices),
                        CacheFileSystemParameters =
                            FileSystemParameters.CreateDefaultCacheFileSystemParameters(remoteServices, decryptionServices)
                    };
                }

                case AssetPlayMode.Web:
                {
                    var remoteServices = new GameRemoteServices(config.MainCdnUrl, config.FallbackCdnUrl);
                    return new WebPlayModeParameters
                    {
                        WebServerFileSystemParameters =
                            FileSystemParameters.CreateDefaultWebServerFileSystemParameters(),
                        WebRemoteFileSystemParameters =
                            FileSystemParameters.CreateDefaultWebRemoteFileSystemParameters(remoteServices)
                    };
                }

                default:
                    throw new NotSupportedException($"Unsupported asset play mode: {mode}");
            }
        }

        private static UnityEngine.Object ResolveLoadedObject(UnityEngine.Object loaded, Type expectedType)
        {
            if (loaded == null || expectedType == null) return null;
            if (expectedType.IsInstanceOfType(loaded)) return loaded;
            if (loaded is GameObject go && typeof(Component).IsAssignableFrom(expectedType))
                return go.GetComponent(expectedType);
            return null;
        }

        // YooAsset 3.0 的操作（InitializationOperation / RequestPackageVersionOperation 等）是 IEnumerator、
        // 无 2.x 的 .Task 属性（兼容层只给 Handle 补了 .Task）。YooAsset 内部 PlayerLoop 驱动操作推进，
        // 这里用 IsDone 轮询桥接到 UniTask；取消只中断等待、不取消底层操作（与 2.x AttachExternalCancellation 行为一致）。
        private static UniTask WaitOp(AsyncOperationBase op, CancellationToken ct)
            => UniTask.WaitUntil(() => op.IsDone, cancellationToken: ct);

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(YooAssetProvider));
        }
    }

    /// <summary>
    /// YooAsset 资源句柄适配。
    /// 持有一份 AssetHandle 引用；Dispose 时释放。Dispose 幂等。
    /// </summary>
    internal sealed class YooAssetHandle : IAssetHandle<UnityEngine.Object>
    {
        private AssetHandle _native;

        public YooAssetHandle(AssetHandle native, UnityEngine.Object asset)
        {
            _native = native;
            Asset = asset;
        }

        public UnityEngine.Object Asset { get; private set; }
        public bool IsValid => _native != null;

        public void Dispose()
        {
            if (_native == null) return;
            _native.Release();
            _native = null;
            Asset = null;
        }
    }

    /// <summary>
    /// YooAsset 场景句柄适配。
    /// Dispose 发起 fire-and-forget 卸载；需要等待卸载完成时显式 await Unload。
    /// </summary>
    internal sealed class YooSceneHandle : ISceneHandle
    {
        private YooAsset.SceneHandle _native;
        private bool _unloading;

        public YooSceneHandle(YooAsset.SceneHandle native) => _native = native;

        public Scene Scene => _native != null ? _native.SceneObject : default;
        public bool IsValid => _native != null && !_unloading;

        public bool Activate() => _native != null && _native.ActivateScene();
        public bool UnSuspend() => _native != null && _native.UnSuspend();

        public async UniTask Unload()
        {
            if (_native == null || _unloading) return;
            _unloading = true;
            var native = _native;
            _native = null;

            var op = native.UnloadAsync();
            await UniTask.WaitUntil(() => op.IsDone);
            if (op.Status != EOperationStatus.Succeed)
                throw new InvalidOperationException($"[YooSceneHandle] Unload failed: {op.Error}");
        }

        public void Dispose()
        {
            if (_native == null) return;
            Unload().Forget(static ex => Debug.LogException(ex));
        }
    }

    /// <summary>
    /// YooAsset 下载器适配。
    /// 进度用 R3 状态流暴露；订阅时立即拿到当前快照。
    /// </summary>
    internal sealed class AssetDownloader : IAssetDownloader
    {
        private readonly ResourceDownloaderOperation _operation;
        private readonly ReactiveProperty<DownloadProgressReport> _progress;
        private bool _started;

        public AssetDownloader(ResourceDownloaderOperation operation)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            _progress = new ReactiveProperty<DownloadProgressReport>(
                new DownloadProgressReport(0f, operation.TotalDownloadCount, 0, operation.TotalDownloadBytes, 0));

            _operation.DownloadUpdateCallback = data =>
            {
                _progress.Value = new DownloadProgressReport(
                    data.Progress,
                    data.TotalDownloadCount,
                    data.CurrentDownloadCount,
                    data.TotalDownloadBytes,
                    data.CurrentDownloadBytes);
            };
        }

        public int TotalCount => _operation.TotalDownloadCount;
        public long TotalBytes => _operation.TotalDownloadBytes;
        public bool IsDone => _operation.Status == EOperationStatus.Succeed || TotalCount == 0;
        public bool IsSimulated => false;
        public ReadOnlyReactiveProperty<DownloadProgressReport> Progress => _progress;

        public async UniTask Download(CancellationToken ct = default)
        {
            if (TotalCount == 0)
            {
                _progress.Value = new DownloadProgressReport(1f, 0, 0, 0, 0);
                return;
            }

            using var registration = ct.Register(_operation.CancelDownload);
            if (!_started)
            {
                _started = true;
                _operation.BeginDownload();
            }

            await UniTask.WaitUntil(() => _operation.IsDone);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (_operation.Status != EOperationStatus.Succeed)
                throw new InvalidOperationException($"[AssetDownloader] Download failed: {_operation.Error}");
        }
    }

    /// <summary>
    /// YooAsset 远端地址服务。只做 URL 规范化和拼接，主备策略沿用 YooAsset 自带机制。
    /// </summary>
    internal sealed class GameRemoteServices : IRemoteServices
    {
        private readonly string _mainUrl;
        private readonly string _fallbackUrl;

        public GameRemoteServices(string mainUrl, string fallbackUrl)
        {
            _mainUrl = Normalize(mainUrl);
            _fallbackUrl = Normalize(fallbackUrl);
        }

        public string GetRemoteMainURL(string fileName) => _mainUrl + fileName;
        public string GetRemoteFallbackURL(string fileName) => _fallbackUrl + fileName;

        private static string Normalize(string url)
            => string.IsNullOrEmpty(url) ? string.Empty : url.TrimEnd('/') + "/";
    }

    /// <summary>
    /// YooAsset 解密服务。
    /// 当前实现支持文件头偏移式加密；FileOffset 为 0 时退化为普通 AssetBundle 文件加载。
    /// </summary>
    internal sealed class GameDecryptionServices : IDecryptionServices
    {
        public ulong FileOffset { get; }

        public GameDecryptionServices(ulong fileOffset = 0) => FileOffset = fileOffset;

        public DecryptResult LoadAssetBundle(DecryptFileInfo fileInfo) => new()
        {
            ManagedStream = null,
            Result = AssetBundle.LoadFromFile(fileInfo.FileLoadPath, fileInfo.FileLoadCRC, FileOffset)
        };

        public DecryptResult LoadAssetBundleAsync(DecryptFileInfo fileInfo) => new()
        {
            ManagedStream = null,
            CreateRequest = AssetBundle.LoadFromFileAsync(fileInfo.FileLoadPath, fileInfo.FileLoadCRC, FileOffset)
        };

        public DecryptResult LoadAssetBundleFallback(DecryptFileInfo fileInfo) => new()
        {
            Result = AssetBundle.LoadFromMemory(ReadFileData(fileInfo), fileInfo.FileLoadCRC)
        };

        public byte[] ReadFileData(DecryptFileInfo fileInfo)
        {
            var allBytes = File.ReadAllBytes(fileInfo.FileLoadPath);
            if (FileOffset == 0) return allBytes;

            var offset = (int)FileOffset;
            if (offset <= 0 || offset >= allBytes.Length)
            {
                Debug.LogError($"[GameDecryption] Invalid offset {offset} for '{fileInfo.BundleName}'.");
                return allBytes;
            }

            var decryptedBytes = new byte[allBytes.Length - offset];
            Buffer.BlockCopy(allBytes, offset, decryptedBytes, 0, decryptedBytes.Length);
            return decryptedBytes;
        }

        public string ReadFileText(DecryptFileInfo fileInfo)
            => FileOffset == 0
                ? File.ReadAllText(fileInfo.FileLoadPath)
                : Encoding.UTF8.GetString(ReadFileData(fileInfo));
    }
}
