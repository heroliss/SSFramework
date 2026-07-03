using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using R3;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;

namespace Game.Framework
{
    /// <summary>
    /// 基于 YooAsset 3.0（原生 API）的资源 provider 实现。
    ///
    /// 本类是框架内唯一直接接触 YooAsset API 的生产代码边界：全局初始化、Package 创建/复用、
    /// manifest 更新、handle 包装、下载器适配都收口在这里。上层 AssetUtility 只按 packageName
    /// 管理状态，不知道底层资源库的类型和初始化细节。
    ///
    /// <para>
    /// <b>3.0 原生 API（不再依赖 YOOASSET_LEGACY_API 兼容层）：</b>初始化用 <see cref="InitializePackageOptions"/>
    /// 分模式选项 + <c>InitializePackageAsync</c>；远端地址用 <see cref="IRemoteService"/>；解密用拆分后的
    /// <see cref="IBundleOffsetDecryptor"/> / <see cref="IBundleMemoryDecryptor"/>（经 <see cref="EFileSystemParameter"/> 注入）；
    /// 文本/字节直读（LoadText/LoadBytes）按包构建管线自动路由——RawFile 包走 <c>LoadAssetAsync&lt;RawFileObject&gt;</c>，
    /// 普通 AB 包按 <c>TextAsset</c> 取内容；下载器用 <see cref="ResourceDownloaderOptions"/> + 进度事件。
    /// </para>
    /// </summary>
    internal sealed class YooAssetProvider : IAssetProvider
    {
        private readonly Dictionary<string, ResourcePackage> _packages = new();
        private bool _disposed;

#if UNITY_EDITOR
        /// <inheritdoc/>
        public Func<bool> SimulateOffline { get; set; }
#endif

        public async UniTask InitializeAsync(string packageName, AssetPlayMode mode, AssetProviderConfig config, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(packageName))
                throw new ArgumentException("Package name is required.", nameof(packageName));
            config ??= new AssetProviderConfig();

            if (!YooAssets.IsInitialized)
                YooAssets.Initialize();

            if (!YooAssets.TryGetPackage(packageName, out var package))
                package = YooAssets.CreatePackage(packageName);

            if (package.InitializeStatus != EOperationStatus.Succeeded)
            {
                var initOp = package.InitializePackageAsync(CreateInitOptions(packageName, mode, config));
                await WaitOp(initOp, ct);
                ct.ThrowIfCancellationRequested();

                if (initOp.Status != EOperationStatus.Succeeded)
                    throw new InvalidOperationException($"[YooAssetProvider] Package '{packageName}' initialize failed: {initOp.Error}");
            }

            if (!package.PackageValid)
                await UpdateManifestAsync(packageName, package, config.CdnUrls, ct);

            _packages[packageName] = package;
        }

        public bool IsPackageReady(string packageName)
            => !_disposed && !string.IsNullOrEmpty(packageName) &&
               _packages.TryGetValue(packageName, out var package) &&
               package.InitializeStatus == EOperationStatus.Succeeded &&
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
                ? package.GetAssetInfoByGuid(locationOrGuid, loadType)
                : package.GetAssetInfo(locationOrGuid, loadType);

            if (assetInfo == null || !assetInfo.IsValid)
            {
                Debug.LogError($"[YooAssetProvider] Asset not found in package '{packageName}': {locationOrGuid}. {assetInfo?.Error}");
                return null;
            }

            var handle = package.LoadAssetAsync(assetInfo);
            try
            {
                await WaitHandle(handle, ct);
            }
            catch
            {
                handle.Release();
                throw;
            }

            if (handle.Status != EOperationStatus.Succeeded)
            {
                Debug.LogError($"[YooAssetProvider] Load failed in package '{packageName}': {locationOrGuid}, {handle.Error}");
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
                await WaitHandle(handle, ct);
            }
            catch
            {
                _ = handle.UnloadSceneAsync(); // fire-and-forget 清理，随后抛出
                throw;
            }

            if (handle.Status != EOperationStatus.Succeeded)
            {
                Debug.LogError($"[YooAssetProvider] Load scene failed in package '{packageName}': {location}, {handle.Error}");
                _ = handle.UnloadSceneAsync();
                return null;
            }

            return new YooSceneHandle(handle);
        }

        public async UniTask<string> LoadTextAsync(string packageName, string location, CancellationToken ct)
        {
            if (IsRawFilePackage(packageName))
            {
                var (handle, raw) = await LoadRawFileObjectAsync(packageName, location, ct);
                if (handle == null) return null;
                try { return raw.GetText(); }
                finally { handle.Release(); }
            }
            var (taHandle, ta) = await LoadTextAssetAsync(packageName, location, ct);
            if (taHandle == null) return null;
            try { return ta.text; }
            finally { taHandle.Release(); }
        }

        public async UniTask<byte[]> LoadBytesAsync(string packageName, string location, CancellationToken ct)
        {
            if (IsRawFilePackage(packageName))
            {
                var (handle, raw) = await LoadRawFileObjectAsync(packageName, location, ct);
                if (handle == null) return null;
                try { return raw.GetBytes(); }
                finally { handle.Release(); }
            }
            var (taHandle, ta) = await LoadTextAssetAsync(packageName, location, ct);
            if (taHandle == null) return null;
            try { return ta.bytes; }
            finally { taHandle.Release(); }
        }

        public bool CheckLocationValid(string packageName, string location)
        {
            if (!IsPackageReady(packageName) || string.IsNullOrEmpty(location)) return false;
            return _packages[packageName].IsLocationValid(location);
        }

        public bool IsNeedDownload(string packageName, string location)
        {
            if (!IsPackageReady(packageName) || string.IsNullOrEmpty(location)) return false;
            // 3.0 用下载尺寸代替旧的 bool 查询：>0 即表示该资源仍需从远端拉取。
            return _packages[packageName].GetDownloadSize(location) > 0;
        }

        public string GetPackageVersion(string packageName)
        {
            if (!IsPackageReady(packageName)) return null;
            return _packages[packageName].GetPackageVersion();
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
            var op = package.CreateResourceDownloader(new ResourceDownloaderOptions(tagArray, maxConcurrent, retries));
            return new AssetDownloader(op);
        }

        public IAssetDownloader CreateAllDownloader(string packageName, int maxConcurrent, int retries)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            // 无 tag 的 ResourceDownloaderOptions = 下载该包当前清单下全部尚未缓存的 bundle（YooAsset: Tags=null → download all required）。
            var op = package.CreateResourceDownloader(new ResourceDownloaderOptions(maxConcurrent, retries));
            return new AssetDownloader(op);
        }

        public IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations, int maxConcurrent, int retries)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            if (locations == null || locations.Count == 0)
                throw new ArgumentException("At least one location is required.", nameof(locations));

            // 把每个 location 解析成 AssetInfo；解析不到的（拼错地址 / 传错包）跳过并 warning，不无声吞（同 ClearCacheByLocations）。
            var infos = new List<AssetInfo>(locations.Count);
            for (int i = 0; i < locations.Count; i++)
            {
                var info = package.GetAssetInfo(locations[i]);
                if (info == null || !info.IsValid)
                    Debug.LogWarning($"[YooAssetProvider] Create location downloader: '{locations[i]}' not found in package '{packageName}' manifest — skipped. {info?.Error}");
                else
                    infos.Add(info);
            }
            // downloadDependencies=true：下载这些资源所在 bundle 及其依赖 bundle（业务通常要「连带依赖一起下好」）。
            var op = package.CreateResourceDownloader(new BundleDownloaderOptions(infos.ToArray(), downloadDependencies: true, maxConcurrent, retries));
            return new AssetDownloader(op);
        }

        public async UniTask ClearCacheAsync(string packageName, AssetCacheClearMode mode, CancellationToken ct)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            // 框架的两档清理映射到 YooAsset 3.0 的清理方式：All=清全部缓存 bundle，Unused=清未被当前清单引用的 bundle。
            string method = mode == AssetCacheClearMode.All
                ? ClearCacheMethods.ClearAllBundleFiles
                : ClearCacheMethods.ClearUnusedBundleFiles;
            var op = package.ClearCacheAsync(new ClearCacheOptions(method));
            await WaitOp(op, ct);
            ct.ThrowIfCancellationRequested();

            if (op.Status != EOperationStatus.Succeeded)
                throw new InvalidOperationException($"[YooAssetProvider] Clear cache failed for '{packageName}': {op.Error}");
        }

        public async UniTask ClearCacheByTagsAsync(string packageName, IReadOnlyList<string> tags, CancellationToken ct)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            // 按 tag 清：只清这些 tag 标记的已下载 bundle（YooAsset ClearBundleFilesByTags，clearParam 收 string/数组/List，这里传 List）。
            var op = package.ClearCacheAsync(new ClearCacheOptions(ClearCacheMethods.ClearBundleFilesByTags, new List<string>(tags)));
            await WaitOp(op, ct);
            ct.ThrowIfCancellationRequested();

            if (op.Status != EOperationStatus.Succeeded)
                throw new InvalidOperationException($"[YooAssetProvider] Clear cache by tags failed for '{packageName}': {op.Error}");
        }

        public async UniTask ClearCacheByLocationsAsync(string packageName, IReadOnlyList<string> locations, CancellationToken ct)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            // 按地址清：YooAsset 把每个 location 解析到所属 bundle 后整份删（ClearBundleFilesByLocations，clearParam 收 string/数组/List，这里传 List）。
            // YooAsset 对 manifest 里解析不到的地址会「静默跳过」——但传进来一个解析不到的地址几乎一定是调用方拼错地址 / 传错包，
            // 无声跳过会让这种 bug 被掩盖，所以这里先逐个校验、对无效地址打 warning。
            // 注意只警告「manifest 里根本没有」的地址；地址有效但本就没缓存（没下载过）是正常 no-op，不警告。
            for (int i = 0; i < locations.Count; i++)
            {
                if (!package.IsLocationValid(locations[i]))
                    Debug.LogWarning($"[YooAssetProvider] Clear cache by locations: '{locations[i]}' not found in package '{packageName}' manifest — skipped.");
            }
            var op = package.ClearCacheAsync(new ClearCacheOptions(ClearCacheMethods.ClearBundleFilesByLocations, new List<string>(locations)));
            await WaitOp(op, ct);
            ct.ThrowIfCancellationRequested();

            if (op.Status != EOperationStatus.Succeeded)
                throw new InvalidOperationException($"[YooAssetProvider] Clear cache by locations failed for '{packageName}': {op.Error}");
        }

        public async UniTask UnloadUnusedAssetsAsync(string packageName, CancellationToken ct)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            // YooAsset 不会自动回收引用归零的 bundle，必须显式调；内部默认循环若干次以处理依赖链。
            var op = package.UnloadUnusedAssetsAsync();
            await WaitOp(op, ct);
            ct.ThrowIfCancellationRequested();

            if (op.Status != EOperationStatus.Succeeded)
                throw new InvalidOperationException($"[YooAssetProvider] Unload unused assets failed for '{packageName}': {op.Error}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 只丢弃本 provider 对各 ResourcePackage 的引用；刻意不 DestroyAsync/RemovePackage——
            // package 在 YooAssets 全局注册表里按「进程级单例」存活（见 IAssetUtility「不提供 UnloadPackage」的理由）。
            // 重建 provider（如 Context 重建）时 InitializeAsync 会经 YooAssets.TryGetPackage 复用已初始化的同名包。
            _packages.Clear();
        }

        // RawFile 在 3.0 作为 RawFileObject 资源经标准资源通道加载；调用方读完 text/bytes 后立即 Release handle。
        // 文本/字节直读的通道路由：RawFile 包（RawFileBuildPipeline 构建，bundle 类型包级二选一）只能走
        // RawFileObject 通道，普通 AB 包只能按 TextAsset 资产取内容——清单里记录了构建管线名，按它判定。
        // 结果按包缓存：包的构建管线类型不随热更清单变化。
        // 注：EditorSimulate 模式下管线名是 "EditorSimulateBuildPipeline"，会按普通 AB 包处理——当前唯一的
        // RawFile 包是代码包（不经本通道加载）；若未来支持业务 RawFile 包，需在此补 Simulate 判定。
        private readonly Dictionary<string, bool> _rawFilePackages = new();

        private bool IsRawFilePackage(string packageName)
        {
            if (_rawFilePackages.TryGetValue(packageName, out bool cached)) return cached;
            bool isRaw = GetReadyPackage(packageName).GetPackageDetails().BuildPipeline == "RawFileBuildPipeline";
            _rawFilePackages[packageName] = isRaw;
            return isRaw;
        }

        // 普通 AB 包的文本/字节直读：.bytes/.txt/.json 等文本类资产在 Unity 里导入为 TextAsset，
        // 加载后内容拷出、句柄立即由调用方释放——业务拿到的是与资源生命周期无关的纯数据。
        private async UniTask<(AssetHandle handle, TextAsset asset)> LoadTextAssetAsync(
            string packageName, string location, CancellationToken ct)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[YooAssetProvider] Text/bytes location is empty.");
                return (null, null);
            }

            var handle = package.LoadAssetAsync<TextAsset>(location);
            try { await WaitHandle(handle, ct); }
            catch { handle.Release(); throw; }

            if (handle.Status != EOperationStatus.Succeeded)
            {
                Debug.LogError($"[YooAssetProvider] Load text asset failed in package '{packageName}': {location}, {handle.Error}");
                handle.Release();
                return (null, null);
            }

            if (handle.AssetObject is not TextAsset ta)
            {
                Debug.LogError($"[YooAssetProvider] '{location}' in package '{packageName}' is not a TextAsset — " +
                               "only text-like assets (.bytes/.txt/.json...) can be read via LoadText/LoadBytes on an AssetBundle package.");
                handle.Release();
                return (null, null);
            }
            return (handle, ta);
        }

        private async UniTask<(AssetHandle handle, RawFileObject raw)> LoadRawFileObjectAsync(
            string packageName, string location, CancellationToken ct)
        {
            ThrowIfDisposed();
            var package = GetReadyPackage(packageName);
            if (string.IsNullOrEmpty(location))
            {
                Debug.LogWarning("[YooAssetProvider] RawFile location is empty.");
                return (null, null);
            }

            var handle = package.LoadAssetAsync<RawFileObject>(location);
            try { await WaitHandle(handle, ct); }
            catch { handle.Release(); throw; }

            if (handle.Status != EOperationStatus.Succeeded)
            {
                Debug.LogError($"[YooAssetProvider] Load raw file failed in package '{packageName}': {location}, {handle.Error}");
                handle.Release();
                return (null, null);
            }

            if (handle.AssetObject is not RawFileObject raw)
            {
                Debug.LogError($"[YooAssetProvider] '{location}' in package '{packageName}' is not a RawFile.");
                handle.Release();
                return (null, null);
            }
            return (handle, raw);
        }

        private ResourcePackage GetReadyPackage(string packageName)
        {
            if (IsPackageReady(packageName)) return _packages[packageName];
            throw new InvalidOperationException($"[YooAssetProvider] Package '{packageName}' is not initialized or manifest is unavailable.");
        }

        // cdnUrls = 配置的 CDN 地址列表。YooAsset 的默认 URL 策略一次请求只选一条候选地址，
        // 失败后递增计数、下一次请求轮到下一条；计数挂在 FileSystem 上，跨 operation 保留。
        // 因此这里对「版本号」和「清单」都循环跑满一圈：任一镜像成功即停，全部失败才算真失败。
        private static async UniTask UpdateManifestAsync(string packageName, ResourcePackage package, IReadOnlyList<string> cdnUrls, CancellationToken token)
        {
            int attempts = Math.Max(1, cdnUrls?.Count ?? 1);
            string version = null;
            string lastVersionError = null;
            bool versionOk = false;
            string configured = (cdnUrls == null || cdnUrls.Count == 0) ? "(未配置)" : string.Join(", ", cdnUrls);
            // 逐次尝试的诊断走 FrameworkLog.Verbose（开 Verbose 才打、不污染正常运行）。
            // 失败 Error 通常已含实际 URL + HttpCode，配合开头候选清单即可定位是哪条 CDN 出问题。
            Internal.FrameworkLog.LogVerbose(
                $"[YooAssetProvider] '{packageName}' 拉版本：候选 CDN {attempts} 条 [{configured}]");
            for (int i = 0; i < attempts; i++)
            {
                var versionOp = package.RequestPackageVersionAsync();
                await WaitOp(versionOp, token);
                token.ThrowIfCancellationRequested();

                if (versionOp.Status == EOperationStatus.Succeeded)
                {
                    version = versionOp.PackageVersion;
                    versionOk = true;
                    Internal.FrameworkLog.LogVerbose($"[YooAssetProvider] '{packageName}' 拉版本第 {i + 1}/{attempts} 次成功，版本 {version}。");
                    break;
                }
                lastVersionError = versionOp.Error;
                Internal.FrameworkLog.LogVerbose($"[YooAssetProvider] '{packageName}' 拉版本第 {i + 1}/{attempts} 次失败：{lastVersionError}");
            }

            if (!versionOk)
            {
                // 把「配置了哪些 CDN」「最终请求形如什么」一并写进异常，让 CDN 配错（端口 / 多余路径段 / 没部署）一眼可查，
                // 不必去翻底层日志拼凑。lastVersionError 通常已含实际 URL 与 HttpCode。
                throw new InvalidOperationException(
                    $"[YooAssetProvider] 拉包版本失败 '{packageName}'：已尝试配置的 {attempts} 个 CDN 地址 [{configured}]，" +
                    $"最终请求形如 <CDN地址>/{packageName}/{packageName}.version——请对照部署目录与端口核对（端口须等于 LocalServePort，服务伺服 Deploy 根、无多余路径段）。底层错误：{lastVersionError}");
            }

            // 3.0：UpdatePackageManifestAsync(version) → LoadPackageManifestAsync(options)。第二个参数为超时秒数。
            // 版本请求成功后，URL 策略会先粘在刚成功的候选地址上；若该镜像清单下载失败，失败计数递增，
            // 下一次 LoadPackageManifestAsync 就会轮到下一条 CDN。
            string lastManifestError = null;
            for (int i = 0; i < attempts; i++)
            {
                var manifestOp = package.LoadPackageManifestAsync(new LoadPackageManifestOptions(version, 60));
                await WaitOp(manifestOp, token);
                token.ThrowIfCancellationRequested();

                if (manifestOp.Status == EOperationStatus.Succeeded)
                {
                    Internal.FrameworkLog.LogVerbose($"[YooAssetProvider] '{packageName}' 拉清单第 {i + 1}/{attempts} 次成功。");
                    return;
                }

                lastManifestError = manifestOp.Error;
                Internal.FrameworkLog.LogVerbose($"[YooAssetProvider] '{packageName}' 拉清单第 {i + 1}/{attempts} 次失败：{lastManifestError}");
            }

            throw new InvalidOperationException(
                $"[YooAssetProvider] 拉包清单失败 '{packageName}'（版本 {version}）：已尝试配置的 {attempts} 个 CDN 地址 [{configured}]，" +
                $"最终请求形如 <CDN地址>/{packageName}/{packageName}_{version}.bytes。底层错误：{lastManifestError}");
        }

        // 按运行模式构建 3.0 初始化选项。解密器经 EFileSystemParameter 注入对应文件系统参数。
        // 非 static：Host/Web 分支要把实例上的「模拟断网」开关源（编辑器期）注入 GameRemoteService。
        private InitializePackageOptions CreateInitOptions(string packageName, AssetPlayMode mode, AssetProviderConfig config)
        {
            switch (mode)
            {
#if UNITY_EDITOR
                case AssetPlayMode.EditorSimulate:
                {
                    var buildResult = EditorSimulateBuildInvoker.Build(packageName, (int)EBundleType.VirtualAssetBundle);
                    return new EditorSimulateModeOptions
                    {
                        EditorFileSystemParameters =
                            FileSystemParameters.CreateDefaultEditorFileSystemParameters(buildResult.PackageRootDirectory)
                    };
                }
#endif
                case AssetPlayMode.Offline:
                {
                    var builtin = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters();
                    ApplyDecryptor(builtin, config);
                    return new OfflinePlayModeOptions { BuiltinFileSystemParameters = builtin };
                }

                case AssetPlayMode.Host:
                {
                    var remoteService = new GameRemoteService(packageName, config.CdnUrls);
#if UNITY_EDITOR
                    remoteService.SimulateOffline = SimulateOffline; // 注入模拟断网开关源（实时读取）
#endif
                    var builtin = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters();
#if UNITY_EDITOR
                    // 编辑器期把「下载缓存」和「内置解包/足迹」都收进 项目根/AssetBuild/Downloaded/<包>，
                    // 否则根目录又会冒出 YooAsset 默认的 yoo/（每次 Host Play 重生）。两处是分开的两套根、要分别设：
                    //   ① 下载缓存 → SandboxFileSystem 的 packageRoot 重载；
                    //   ② 内置文件系统的解包/足迹（ApplicationFootprint、UnpackManifestFiles）→ BuiltinFileSystem 的【独立】UnpackFileSystemRoot。
                    //      ⚠ 内置 FS 的 packageRoot 是「读 StreamingAssets 的根」，绝不能动；写出位置是另一个 UnpackFileSystemRoot（见 BuiltinFileSystem.OnCreate）。
                    // 两个 root 都是「每包根目录」：YooAsset 仅在默认分支追加包名，这里走重载分支按原样用，需自己拼 /<包>，否则多包撞同一目录。
                    // 这与 YooAsset 原生布局一致（默认下二者本就同在 <缓存根>/<包> 下，子目录名 Cache* / Unpack* 互不冲突）。
                    // 真机（#else）一律用 YooAsset 各平台默认 persistentDataPath，不改变设备行为。这是 YooAsset 特有能力，换后端各自处理（见类型注释）。
                    var editorPackageCacheRoot = Path.Combine(AssetBuildLayout.DownloadedRoot, packageName);
                    builtin.AddParameter(EFileSystemParameter.UnpackFileSystemRoot, editorPackageCacheRoot);
                    var cache = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(remoteService, editorPackageCacheRoot);
#else
                    var cache = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(remoteService);
#endif
                    ApplyDecryptor(builtin, config);
                    ApplyDecryptor(cache, config);
                    // 按包「启用按需下载」（默认启用）：关掉后 Load 未缓存的 bundle 直接失败（见 SFSLoadPackageBundleOperation），
                    // 不偷偷下载；强制业务先显式跑下载器。YooAsset 的参数是反向的 DownloadDisableOndemand，故对「是否启用」取反传入。仅缓存文件系统（Host）有此语义。
                    cache.AddParameter(EFileSystemParameter.DownloadDisableOndemand, !config.ShouldEnableOnDemandDownload(packageName));
                    return new HostPlayModeOptions
                    {
                        BuiltinFileSystemParameters = builtin,
                        CacheFileSystemParameters = cache
                    };
                }

                case AssetPlayMode.Web:
                {
                    var remoteService = new GameRemoteService(packageName, config.CdnUrls);
#if UNITY_EDITOR
                    remoteService.SimulateOffline = SimulateOffline; // 注入模拟断网开关源（实时读取）
#endif
                    return new WebPlayModeOptions
                    {
                        WebServerFileSystemParameters = FileSystemParameters.CreateDefaultWebServerFileSystemParameters(),
                        WebNetworkFileSystemParameters = FileSystemParameters.CreateDefaultWebNetworkFileSystemParameters(remoteService)
                    };
                }

                default:
                    throw new NotSupportedException($"Unsupported asset play mode: {mode}");
            }
        }

        // 给文件系统注入解密器。优先级：项目自定义解密器（GameAssetDecryption）> 偏移解密（FileOffset>0）> 不加密。
        // 同一个解密器同时登记为 AssetBundle / Raw 解密器；内存兜底解密器要求实现 IBundleMemoryDecryptor，实现了才登记。
        private static void ApplyDecryptor(FileSystemParameters fsParams, AssetProviderConfig config)
        {
            // ① 自定义解密器优先（项目用 XOR/AES 等时设了工厂）。与构建侧自定义加密器成对，见 docs/asset-encryption.md。
            if (GameAssetDecryption.BundleDecryptorFactory != null)
            {
                var custom = GameAssetDecryption.BundleDecryptorFactory();
                fsParams.AddParameter(EFileSystemParameter.AssetBundleDecryptor, custom);
                fsParams.AddParameter(EFileSystemParameter.RawBundleDecryptor, custom);
                // 兜底解密器（加载失败时回内存解密重试）必须是内存解密器；偏移/流式解密器不实现它就不登记。
                if (custom is IBundleMemoryDecryptor)
                    fsParams.AddParameter(EFileSystemParameter.AssetBundleFallbackDecryptor, custom);
                var manifest = GameAssetDecryption.ManifestDecryptorFactory?.Invoke();
                if (manifest != null)
                    fsParams.AddParameter(EFileSystemParameter.ManifestDecryptor, manifest);
                return;
            }

            // ② 内置偏移解密（FileOffset==0 不加密则跳过）。GameBundleOffsetDecryptor 同时实现偏移 + 内存兜底两接口。
            if (config.FileOffset == 0) return;
            var decryptor = new GameBundleOffsetDecryptor(config.FileOffset);
            fsParams.AddParameter(EFileSystemParameter.AssetBundleDecryptor, decryptor);
            fsParams.AddParameter(EFileSystemParameter.RawBundleDecryptor, decryptor);
            fsParams.AddParameter(EFileSystemParameter.AssetBundleFallbackDecryptor, decryptor);
        }

        private static UnityEngine.Object ResolveLoadedObject(UnityEngine.Object loaded, Type expectedType)
        {
            if (loaded == null || expectedType == null) return null;
            if (expectedType.IsInstanceOfType(loaded)) return loaded;
            if (loaded is GameObject go && typeof(Component).IsAssignableFrom(expectedType))
                return go.GetComponent(expectedType);
            return null;
        }

        // 操作（InitializePackageOperation / RequestPackageVersionOperation 等）是 AsyncOperationBase，
        // YooAsset 内部 PlayerLoop 驱动推进；用 IsDone 轮询桥接到 UniTask。取消只中断等待、不取消底层操作。
        private static UniTask WaitOp(AsyncOperationBase op, CancellationToken ct)
            => UniTask.WaitUntil(() => op.IsDone, cancellationToken: ct);

        // 资源/场景句柄不是 AsyncOperationBase，但同样有 IsDone；同样用轮询桥接（3.0 支持 await handle，
        // 但轮询能统一附带 CancellationToken，且不依赖底层 awaiter 实现）。
        private static UniTask WaitHandle(HandleBase handle, CancellationToken ct)
            => UniTask.WaitUntil(() => handle.IsDone, cancellationToken: ct);

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

        // 3.0：UnSuspend → AllowSceneActivation。保留框架 ISceneHandle.UnSuspend 语义（放行挂起的场景激活）。
        public bool UnSuspend()
        {
            if (_native == null) return false;
            _native.AllowSceneActivation();
            return true;
        }

        public async UniTask Unload()
        {
            if (_native == null || _unloading) return;
            _unloading = true;
            var native = _native;
            _native = null;

            var op = native.UnloadSceneAsync();
            await UniTask.WaitUntil(() => op.IsDone);
            if (op.Status != EOperationStatus.Succeeded)
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

            // 3.0：DownloadUpdateCallback → DownloadProgressChanged 事件。直接从 operation 读取实时计数，
            // 不依赖事件参数的具体字段形状。
            _operation.DownloadProgressChanged += OnProgressChanged;
        }

        public int TotalCount => _operation.TotalDownloadCount;
        public long TotalBytes => _operation.TotalDownloadBytes;
        public bool IsDone => _operation.Status == EOperationStatus.Succeeded || TotalCount == 0;
        public ReadOnlyReactiveProperty<DownloadProgressReport> Progress => _progress;

        public async UniTask Download(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (TotalCount == 0)
            {
                _progress.Value = new DownloadProgressReport(1f, 0, 0, 0, 0);
                return;
            }

            using var registration = ct.Register(_operation.CancelDownload);
            if (!_started)
            {
                _started = true;
                _operation.StartDownload();
            }

            await UniTask.WaitUntil(() => _operation.IsDone, cancellationToken: ct);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (_operation.Status != EOperationStatus.Succeeded)
                throw new InvalidOperationException($"[AssetDownloader] Download failed: {_operation.Error}");
        }

        private void OnProgressChanged(DownloadProgressChangedEventArgs _)
        {
            _progress.Value = new DownloadProgressReport(
                _operation.Progress,
                _operation.TotalDownloadCount,
                _operation.CurrentDownloadCount,
                _operation.TotalDownloadBytes,
                _operation.CurrentDownloadBytes);
        }
    }

    /// <summary>
    /// YooAsset 3.0 远端地址服务（<see cref="IRemoteService"/>）。
    /// 返回一个 CDN 地址列表（第一条主、其余备），由 YooAsset 自带的失败轮转机制按候选顺序切换。
    ///
    /// 按包分目录取址：每个包的远端文件都放在 <c>{CDN}/{包名}/</c> 子目录下，最终 URL =
    /// <c>{CDN}/{包名}/{fileName}</c>（fileName 是 YooAsset 逐个请求的 bundle 哈希名 / 版本清单 / 版本号文件）。
    /// 多包各自一个子目录、互不覆盖，部署结构直接镜像构建产物，比一锅烩平铺更易维护。
    /// 服务实例本就按包创建（见 <see cref="YooAssetProvider.CreateInitOptions"/>），所以包名在构造时一次性定下。
    /// </summary>
    internal sealed class GameRemoteService : IRemoteService
    {
        // 规范化并按包追加子目录后的候选基地址（至少一条）。空配置兜一条仅含子目录的相对址，
        // 既避免底层 SelectUrl 因空列表抛 YooInternalException，又让请求必然失败、由上层清晰报错。
        private readonly string[] _baseUrls;
#if UNITY_EDITOR
        /// <summary>编辑器模拟断网开关源（由 <see cref="YooAssetProvider"/> 注入，实时读取 AssetUtility 上的开关）。</summary>
        public Func<bool> SimulateOffline { get; set; }

        // 不可达地址：端口 1 无人监听，请求必然连接失败，从而让 YooAsset 的 init / 下载失败。
        private const string OfflineUrl = "http://127.0.0.1:1/__simulated_offline__/";
#endif

        public GameRemoteService(string packageName, IReadOnlyList<string> cdnUrls)
        {
            // 包名作为子目录前缀拼到规范化后的每个 CDN 根之后；包名为空则退回根目录（兼容无包名场景）。
            string sub = string.IsNullOrEmpty(packageName) ? string.Empty : packageName + "/";
            var list = new List<string>();
            if (cdnUrls != null)
            {
                foreach (var url in cdnUrls)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue; // 跳过空 / 纯空白条目
                    list.Add(Normalize(url) + sub);
                }
            }
            if (list.Count == 0) list.Add(sub); // 未配置远端：兜一条，请求必失败、上层报错
            _baseUrls = list.ToArray();
        }

        public IReadOnlyList<string> GetRemoteUrls(string fileName)
        {
            var urls = new string[_baseUrls.Length];
#if UNITY_EDITOR
            // 实时读开关：开着就把所有候选地址都换成不可达地址，本次（及后续每次）远端请求都失败。
            if (SimulateOffline != null && SimulateOffline())
            {
                for (int i = 0; i < urls.Length; i++) urls[i] = OfflineUrl + fileName;
                return urls;
            }
#endif
            for (int i = 0; i < _baseUrls.Length; i++) urls[i] = _baseUrls[i] + fileName;
            return urls;
        }

        // 先 Trim 掉首尾空白再去尾斜杠：避免配置里不小心多敲 / 粘贴出一个尾随空格（或换行）把 URL 拼成
        // "http://host:port/ /包名/..." 这种肉眼难察的坏地址。空白容错收口在这一处，所有候选地址统一受益。
        private static string Normalize(string url)
            => string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/') + "/";
    }

    /// <summary>
    /// YooAsset 3.0 偏移式解密器。
    /// 同时实现 <see cref="IBundleOffsetDecryptor"/>（跳过文件头偏移后直接加载 AssetBundle）
    /// 与 <see cref="IBundleMemoryDecryptor"/>（内存兜底：剥离偏移后从内存加载）。
    /// FileOffset 为 0 时不会被注册（见 <c>YooAssetProvider.ApplyDecryptor</c>）。
    /// </summary>
    internal sealed class GameBundleOffsetDecryptor : IBundleOffsetDecryptor, IBundleMemoryDecryptor
    {
        private readonly ulong _fileOffset;

        public GameBundleOffsetDecryptor(ulong fileOffset) => _fileOffset = fileOffset;

        public long GetFileOffset(BundleDecryptArgs args) => (long)_fileOffset;

        public byte[] GetDecryptedData(BundleDecryptArgs args)
        {
            var data = args.FileData;
            if (data == null || _fileOffset == 0) return data;

            var offset = (int)_fileOffset;
            if (offset <= 0 || offset >= data.Length)
            {
                Debug.LogError($"[GameBundleOffsetDecryptor] Invalid offset {offset} for a bundle of {data.Length} bytes.");
                return data;
            }

            var result = new byte[data.Length - offset];
            Buffer.BlockCopy(data, offset, result, 0, result.Length);
            return result;
        }
    }
}
