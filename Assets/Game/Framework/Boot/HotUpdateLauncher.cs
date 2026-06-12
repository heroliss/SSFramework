using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using HybridCLR;
using UnityEngine;
using UnityEngine.Events;
using YooAsset;

namespace Game.Framework.Boot
{
    /// <summary>Boot 层运行模式。比框架资源系统的 AssetPlayMode 少：编辑器恒走旁路（无模拟模式），Web 暂不支持。</summary>
    public enum BootPlayMode
    {
        /// <summary>联机：远端检查代码更新，CDN 轮转；取不到远端版本时回退本地已有清单（内置/上次缓存）。</summary>
        Host,

        /// <summary>单机/离线分发：只用随包内置的代码，永不联网。</summary>
        Offline,
    }

    /// <summary>
    /// 热更引导器——**唯一随包场景**（Boot 场景）上的入口组件，整个 AOT 侧引导自举的全部逻辑（ADR-0008 §4）：
    ///
    /// <para><b>玩家包流程</b>：初始化代码包（专用 RawFile 包，与业务资源包彻底分家）→ 检查/下载代码更新 →
    /// 读 <see cref="HotUpdateManifest"/> → 逐个补 AOT 泛型元数据（<c>LoadMetadataForAOTAssembly</c>，SuperSet）→
    /// 按清单拓扑序 <c>Assembly.Load</c> 热更 DLL → 反射调入口静态方法。入口之后就是热更世界：
    /// 由入口创建全局 Context、初始化框架资源系统、从 bundle 加载真正的首场景。</para>
    ///
    /// <para><b>编辑器旁路</b>：编辑器下所有程序集本就在 AppDomain 里，跳过包初始化与 DLL 加载、直接反射进入口——
    /// 同一份代码运行时判断，无 define 双路径，日常开发零负担。</para>
    ///
    /// <para><b>刻意的边界</b>：本类不引用框架任何部分（Boot 永不引用框架，否则框架没法热更）；
    /// 不用 R3（进度走朴素 UnityEvent，Inspector 直连 Text/Slider）；失败时状态回调 + 完整异常日志，不静默。
    /// 纯 AOT 项目（不启用热更）不需要本组件——直接用 MonoGlobalContext 老启动方式。</para>
    /// </summary>
    public sealed class HotUpdateLauncher : MonoBehaviour
    {
        [Header("代码包")]
        [Tooltip("代码包名，须与热更构建配置（FrameworkHotUpdateProfile）的 CodePackageName 一致。")]
        [SerializeField] private string _codePackageName = "CodePackage";

        [Tooltip("Host = 远端检查代码更新（CDN 轮转，失败回退本地已有清单）；Offline = 只用随包内置代码。")]
        [SerializeField] private BootPlayMode _playMode = BootPlayMode.Host;

        [Tooltip("CDN 根地址列表（第一条主、其余备）。实际取址 {CDN}/{包名}/{文件}，与资源包同一套部署结构。")]
        [SerializeField] private string[] _cdnUrls = Array.Empty<string>();

        [Header("热更入口")]
        [Tooltip("入口类型的程序集限定名，如 \"Game.Main.GameEntry, Game.Main\"。\n" +
                 "约定一个公共静态无参方法作为入口（默认 Enter），DLL 全部加载完后反射调用。")]
        [SerializeField] private string _entryTypeName = "Game.Main.GameEntry, Game.Main";

        [Tooltip("入口静态方法名。")]
        [SerializeField] private string _entryMethodName = "Enter";

        [Header("进度回调（可选，Inspector 直连启动 UI）")]
        [Tooltip("阶段文案：初始化/检查更新/下载/加载程序集/进入游戏/失败原因。")]
        [SerializeField] private UnityEvent<string> _onStatus = new();

        [Tooltip("下载进度 0~1（只在确有下载量时回调）。")]
        [SerializeField] private UnityEvent<float> _onDownloadProgress = new();

        [Header("调试")]
        [Tooltip("OnGUI 状态覆盖层（零依赖，Boot 场景不接 UI 也能看到引导阶段与失败原因）。接了正式启动 UI 后关掉。")]
        [SerializeField] private bool _debugOverlay = true;

        private string _lastStatus = "";
        private float _lastProgress = -1f; // <0 = 无下载量，不画进度条

        private void OnGUI()
        {
            if (!_debugOverlay || string.IsNullOrEmpty(_lastStatus)) return;
            // 朴素覆盖层：左上角状态一行 + 可选进度条。只为引导期诊断，不做样式。
            GUI.Label(new Rect(10, 10, Screen.width - 20, 24), $"[Boot] {_lastStatus}");
            if (_lastProgress >= 0f)
            {
                GUI.Box(new Rect(10, 38, 300, 18), GUIContent.none);
                GUI.Box(new Rect(10, 38, 300 * Mathf.Clamp01(_lastProgress), 18), GUIContent.none);
                GUI.Label(new Rect(316, 36, 100, 22), $"{_lastProgress:P0}");
            }
        }

        private void Start()
        {
            Run(this.GetCancellationTokenOnDestroy())
                .Forget(static ex => Debug.LogException(ex));
        }

        private async UniTask Run(System.Threading.CancellationToken ct)
        {
            try
            {
#if UNITY_EDITOR
                // 编辑器旁路：程序集已全部在 AppDomain，包初始化 / 补元数据 / Assembly.Load 全部多余。
                Status("编辑器旁路：直接进入入口");
                InvokeEntry();
                return;
#else
                await RunPlayer(ct);
#endif
            }
            catch (OperationCanceledException)
            {
                // 引导中销毁（退出/切场景）：正常生命周期，静默。
            }
            catch (Exception e)
            {
                Status("启动失败：" + e.Message);
                Debug.LogException(e);
            }
        }

#if !UNITY_EDITOR
        private async UniTask RunPlayer(System.Threading.CancellationToken ct)
        {
            // ── 1. 初始化代码包 ──────────────────────────────────────────────
            Status("初始化代码包…");
            if (!YooAssets.IsInitialized)
                YooAssets.Initialize();
            if (!YooAssets.TryGetPackage(_codePackageName, out var package))
                package = YooAssets.CreatePackage(_codePackageName);

            if (package.InitializeStatus != EOperationStatus.Succeeded)
            {
                var initOp = package.InitializePackageAsync(CreateInitOptions());
                await WaitOp(initOp, ct);
                if (initOp.Status != EOperationStatus.Succeeded)
                    throw new InvalidOperationException($"代码包初始化失败：{initOp.Error}");
            }

            // ── 2. 更新清单（Host 远端轮转；失败回退本地已有清单）────────────────
            Status("检查代码更新…");
            await UpdateManifest(package, ct);

            // ── 3. 下载（内置全量拷贝下通常为 0；有更新时才有量）────────────────
            var downloader = package.CreateResourceDownloader(new ResourceDownloaderOptions(maximumConcurrency: 8, retryCount: 2));
            if (downloader.TotalDownloadCount > 0)
            {
                Status($"下载代码更新（{downloader.TotalDownloadCount} 个文件）…");
                downloader.DownloadProgressChanged += _ =>
                {
                    _lastProgress = downloader.Progress;
                    _onDownloadProgress.Invoke(downloader.Progress);
                };
                downloader.StartDownload();
                await UniTask.WaitUntil(() => downloader.IsDone, cancellationToken: ct);
                if (downloader.Status != EOperationStatus.Succeeded)
                    throw new InvalidOperationException($"代码下载失败：{downloader.Error}");
            }

            // ── 4. 读清单 ───────────────────────────────────────────────────
            var manifestJson = await LoadRawText(package, HotUpdateManifest.Location, ct);
            var manifest = HotUpdateManifest.FromJson(manifestJson)
                           ?? throw new InvalidOperationException("热更清单解析失败（JSON 损坏？）。");
            Debug.Log($"[HotUpdateLauncher] 清单版本 {manifest.Version}：热更 DLL {manifest.HotUpdateDlls.Count} 个，AOT 补元数据 {manifest.AotMetadataDlls.Count} 个。");

            // ── 5. 补 AOT 泛型元数据（必须在跑任何热更代码之前）──────────────────
            if (manifest.AotMetadataDlls.Count > 0)
            {
                Status("加载补充元数据…");
                foreach (var dll in manifest.AotMetadataDlls)
                {
                    var bytes = await LoadRawBytes(package, dll, ct);
                    // SuperSet：补充元数据是裁剪后 AOT DLL 的超集语义，HybridCLR 推荐模式。
                    var err = RuntimeApi.LoadMetadataForAOTAssembly(bytes, HomologousImageMode.SuperSet);
                    if (err != LoadImageErrorCode.OK)
                        throw new InvalidOperationException($"补元数据失败：{dll} → {err}");
                }
            }

            // ── 6. 按拓扑序加载热更 DLL ──────────────────────────────────────
            Status("加载热更程序集…");
            foreach (var dll in manifest.HotUpdateDlls)
            {
                var bytes = await LoadRawBytes(package, dll, ct);
                Assembly.Load(bytes);
                Debug.Log($"[HotUpdateLauncher] 已加载 {dll}");
            }

            // ── 7. 进入热更世界 ─────────────────────────────────────────────
            Status("进入游戏");
            InvokeEntry();
        }

        private InitializePackageOptions CreateInitOptions()
        {
            switch (_playMode)
            {
                case BootPlayMode.Offline:
                {
                    return new OfflinePlayModeOptions
                    {
                        BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters()
                    };
                }
                case BootPlayMode.Host:
                {
                    var remote = new BootRemoteService(_codePackageName, _cdnUrls);
                    return new HostPlayModeOptions
                    {
                        BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
                        CacheFileSystemParameters = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(remote)
                    };
                }
                default:
                    throw new NotSupportedException($"未支持的 Boot 模式：{_playMode}");
            }
        }

        // 远端版本 + 清单各自按候选 CDN 数轮转一圈（YooAsset 的 URL 失败计数跨请求保留，循环即轮换镜像）。
        // Host 拉不到远端版本时：本地已有有效清单（内置/上次缓存）→ 警告 + 降级继续；连本地都没有才真失败。
        private async UniTask UpdateManifest(ResourcePackage package, System.Threading.CancellationToken ct)
        {
            int attempts = _playMode == BootPlayMode.Host ? Math.Max(1, _cdnUrls?.Length ?? 1) : 1;

            string version = null;
            string lastError = null;
            for (int i = 0; i < attempts; i++)
            {
                var versionOp = package.RequestPackageVersionAsync();
                await WaitOp(versionOp, ct);
                if (versionOp.Status == EOperationStatus.Succeeded)
                {
                    version = versionOp.PackageVersion;
                    break;
                }
                lastError = versionOp.Error;
            }

            if (version == null)
            {
                if (package.PackageValid)
                {
                    Debug.LogWarning($"[HotUpdateLauncher] 取远端代码版本失败（{lastError}），降级使用本地已有代码清单。");
                    return;
                }
                throw new InvalidOperationException(
                    $"取代码包版本失败且本地无可用清单：已尝试 {attempts} 个 CDN。底层错误：{lastError}");
            }

            string lastManifestError = null;
            for (int i = 0; i < attempts; i++)
            {
                var manifestOp = package.LoadPackageManifestAsync(new LoadPackageManifestOptions(version, 60));
                await WaitOp(manifestOp, ct);
                if (manifestOp.Status == EOperationStatus.Succeeded) return;
                lastManifestError = manifestOp.Error;
            }
            throw new InvalidOperationException($"取代码包清单失败（版本 {version}）：{lastManifestError}");
        }

        private static async UniTask<string> LoadRawText(ResourcePackage package, string location, System.Threading.CancellationToken ct)
        {
            var (handle, raw) = await LoadRawObject(package, location, ct);
            try { return raw.GetText(); }
            finally { handle.Release(); }
        }

        private static async UniTask<byte[]> LoadRawBytes(ResourcePackage package, string location, System.Threading.CancellationToken ct)
        {
            var (handle, raw) = await LoadRawObject(package, location, ct);
            try { return raw.GetBytes(); }
            finally { handle.Release(); }
        }

        private static async UniTask<(AssetHandle handle, RawFileObject raw)> LoadRawObject(
            ResourcePackage package, string location, System.Threading.CancellationToken ct)
        {
            var handle = package.LoadAssetAsync<RawFileObject>(location);
            try { await UniTask.WaitUntil(() => handle.IsDone, cancellationToken: ct); }
            catch { handle.Release(); throw; }

            if (handle.Status != EOperationStatus.Succeeded || handle.AssetObject is not RawFileObject raw)
            {
                string error = handle.Error;
                handle.Release();
                throw new InvalidOperationException($"代码包读取 '{location}' 失败：{error}");
            }
            return (handle, raw);
        }

        private static UniTask WaitOp(AsyncOperationBase op, System.Threading.CancellationToken ct)
            => UniTask.WaitUntil(() => op.IsDone, cancellationToken: ct);
#endif

        // 入口反射调用——编辑器旁路与玩家包共用：约定公共静态无参方法，找不到就把「类型名/方法名/检查点」说全（fail-fast）。
        private void InvokeEntry()
        {
            if (string.IsNullOrWhiteSpace(_entryTypeName))
                throw new InvalidOperationException("[HotUpdateLauncher] 未配置热更入口类型名（Inspector：Entry Type Name）。");

            var type = Type.GetType(_entryTypeName, throwOnError: false);
            if (type == null)
                throw new InvalidOperationException(
                    $"[HotUpdateLauncher] 找不到热更入口类型 '{_entryTypeName}'。检查：① 程序集限定名拼写；" +
                    "② 玩家包下该 DLL 是否在热更清单里（或为 AOT 程序集）；③ 编辑器下对应程序集是否编译通过。");

            var method = type.GetMethod(_entryMethodName, BindingFlags.Public | BindingFlags.Static, binder: null, types: Type.EmptyTypes, modifiers: null);
            if (method == null)
                throw new InvalidOperationException(
                    $"[HotUpdateLauncher] 入口类型 '{type.FullName}' 上没有公共静态无参方法 '{_entryMethodName}'。");

            method.Invoke(null, null);
        }

        private void Status(string text)
        {
            _lastStatus = text;
            Debug.Log("[HotUpdateLauncher] " + text);
            _onStatus.Invoke(text);
        }
    }

#if !UNITY_EDITOR
    /// <summary>
    /// Boot 版远端地址服务：{CDN}/{包名}/{文件}，与资源包的 GameRemoteService 同一套部署结构，
    /// 但刻意独立实现（Boot 永不引用框架/模块）。地址规范化（去尾斜杠/空白）同源。
    /// </summary>
    internal sealed class BootRemoteService : IRemoteService
    {
        private readonly string[] _baseUrls;

        public BootRemoteService(string packageName, IReadOnlyList<string> cdnUrls)
        {
            string sub = string.IsNullOrEmpty(packageName) ? string.Empty : packageName + "/";
            var list = new List<string>();
            if (cdnUrls != null)
            {
                foreach (var url in cdnUrls)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    list.Add(url.Trim().TrimEnd('/') + "/" + sub);
                }
            }
            if (list.Count == 0) list.Add(sub); // 未配置远端：请求必然失败，由引导器报清晰错误
            _baseUrls = list.ToArray();
        }

        public IReadOnlyList<string> GetRemoteUrls(string fileName)
        {
            var urls = new string[_baseUrls.Length];
            for (int i = 0; i < _baseUrls.Length; i++)
                urls[i] = _baseUrls[i] + fileName;
            return urls;
        }
    }
#endif
}
