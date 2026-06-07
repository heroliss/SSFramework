using System;
using System.Collections.Generic;
using Game.Framework.Model;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Framework
{
    /// <summary>
    /// 资源系统配置 Model。挂在场景的 Context 节点上，由 <see cref="AssetInitSystem"/> 读取并驱动初始化流程。
    ///
    /// 配置本身属于数据层：Inspector 可视化、字段直接序列化，初始化顺序和资源库适配细节由 System / provider 负责。
    /// 同 Context 下只允许一个 AssetSystemConfigModel（重复注册会被 Container 拒绝）。
    /// </summary>
    public class AssetSystemConfigModel : MonoModelBase
    {
        private const string DefaultLocalCdnUrl = "http://127.0.0.1:8080/";

        [Header("基础配置")]
        [Tooltip("默认资源包名称；未显式指定 package 的加载请求都会使用它。")]
        [FormerlySerializedAs("PackageName")]
        [SerializeField] private string _defaultPackageName = "DefaultPackage";

        [Tooltip("全局运行模式（内置首包 = 随包体打进 StreamingAssets 的资源；远端 = CDN）：\n" +
                 "EditorSimulate = 编辑器直接读 AssetDatabase，免打包 / 免下载（开发期默认）；\n" +
                 "Offline = 仅内置首包（StreamingAssets），完全不联网；\n" +
                 "Host = 内置首包（StreamingAssets）+ 远端 CDN，缺的按需下载并缓存；\n" +
                 "Web = 纯远端 HTTP（WebGL），不落地缓存。\n" +
                 "WebGL 构建会强制 Web 模式。")]
        [FormerlySerializedAs("PlayMode")]
        [SerializeField] private AssetPlayMode _playMode = AssetPlayMode.EditorSimulate;

        [Tooltip("默认包之外要在【启动时自动初始化】的资源包。默认包（上面 Default Package Name）总会自动初始化，不必在此列出。\n" +
                 "两种情况才在此加一条：①额外初始化一个非默认包（如 DLC 包）；②给某个包单独设包级策略（如「禁用按需下载」）。\n" +
                 "给默认包配策略也是在此按它的名字加一条——按名去重、不会重复初始化。\n" +
                 "留空 = 只初始化默认包、所有包用默认策略。\n" +
                 "未列入的包不会被自动初始化，但仍可用：业务用前先调 IAssetUtility.RetryInitialize(\"包名\") 冷启动它（按默认策略）。\n" +
                 "⚠ 直接 Load 一个从未触发初始化的包会一直等待（不报错）——要加载的包请列在此处或先 RetryInitialize。")]
        [FormerlySerializedAs("_packages")]
        [SerializeField] private List<AssetPackageConfig> _extraPackages = new();

        [Tooltip("进 Play 是否自动初始化所有配置的包。\n" +
                 "开（默认）= Awake 即初始化（拉版本 / 清单，Host 模式会联网）；\n" +
                 "关 = 启动不初始化，需业务在合适时机显式调 IAssetUtility.RetryInitialize() 触发——\n" +
                 "用于隐私同意 / 选区 / 流量确认后再联网拉清单的启动流程。")]
        [SerializeField] private bool _autoInitializeOnStartup = true;

        [Header("CDN 配置")]
        [Tooltip("CDN 地址列表（远端模式查找版本文件 / 资源包）。第一条为主地址，其余为备用。\n" +
                 "以 / 结尾或不结尾都行，provider 内部自动规范化、并按包名追加子目录（最终 = 地址/包名/文件）。\n" +
                 "初始化会逐条尝试，全部失败才算失败；备用项必须是与主地址等价的可用镜像（否则只是徒增一次失败尝试）。\n" +
                 "本地联调：填 http://127.0.0.1:<端口>/（端口须等于构建 profile 的 LocalServePort，默认 8080；服务伺服 AssetBuild/Deploy 根目录，无 /CDN/ 之类前缀）。留空表示未配置远端。")]
        [SerializeField] private List<string> _cdnUrls = new() { DefaultLocalCdnUrl };

        [Header("下载器配置")]
        [Tooltip("同时下载的最大文件数。值越大并发越高，但占用带宽和系统资源也越多。建议 4-16。")]
        [FormerlySerializedAs("DownloadingMaxNumber")]
        [Min(1)] [SerializeField] private int _downloadingMaxNumber = 10;

        [Tooltip("单个文件下载失败后的重试次数。设为 0 则失败立即放弃。")]
        [FormerlySerializedAs("FailedTryAgain")]
        [Min(0)] [SerializeField] private int _failedTryAgain = 3;

        [Header("加密")]
        [Tooltip("AssetBundle 文件头偏移字节数。构建时若启用偏移加密，这里填相同的偏移值；未启用时保持 0。")]
        [FormerlySerializedAs("FileOffset")]
        [Min(0)] [SerializeField] private ulong _fileOffset = 0;

#if UNITY_EDITOR
        [Header("编辑器调试 · 模拟下载")]
        [Tooltip("EditorSimulate 模式下所有资源都在本地、不会真的下载；框架用「模拟大小 + 速度」造一段进度供你验证下载 UI。\n"
               + "模拟总大小（KB）：会作为下载总量显示给 UI（总大小 / 已下载）。设 0 关闭模拟。")]
        [Min(0)]
        [SerializeField] private int _editorSimulateDownloadSizeKB = 8192;   // 8 MB

        [Tooltip("模拟下载速度（KB/秒）。下载时长 = 大小 ÷ 速度（如 8192KB ÷ 2048KB/s = 4 秒）。设 0 关闭模拟。\n"
               + "仅 Unity Editor 生效，构建版本此字段不存在，框架不会执行任何模拟。")]
        [Min(0)]
        [SerializeField] private int _editorSimulateDownloadSpeedKBps = 2048; // 2 MB/s
#endif

        public string DefaultPackageName => _defaultPackageName;
        public AssetPlayMode PlayMode => _playMode;
        /// <summary>
        /// 默认包之外要在【启动时自动初始化】的包及其包级策略（如禁用按需下载）。默认包总会初始化、无需在此列出；
        /// 在此列默认包仅用于覆盖它的包级策略（按名去重、不会重复初始化）。空 = 只初始化默认包、全用默认策略。
        /// <para>未列入的包不会被自动初始化，但仍可用：业务在用之前显式调 <see cref="IAssetUtility.RetryInitialize"/>("包名")
        /// 冷启动它（未列入的包按默认策略 = 自动下载）。⚠ 直接 Load 一个从未触发初始化的包会一直等待（与延迟初始化未触发前同义），
        /// 不会报错——所以要加载的包，要么列在这里（启动即初始化），要么先 RetryInitialize。</para>
        /// </summary>
        public IReadOnlyList<AssetPackageConfig> ExtraPackages => _extraPackages;
        public IReadOnlyList<string> CdnUrls => _cdnUrls;
        public int DownloadingMaxNumber => _downloadingMaxNumber;
        public int FailedTryAgain => _failedTryAgain;
        public ulong FileOffset => _fileOffset;
        public bool AutoInitializeOnStartup => _autoInitializeOnStartup;

        /// <summary>
        /// EditorSimulate 模拟下载的总大小（字节）。0 = 不模拟。作为下载总量暴露给 UI 显示。
        /// 生产构建始终返回 0（字段由 <c>#if UNITY_EDITOR</c> 包裹，零运行时代价）。
        /// </summary>
        public long EditorSimulateDownloadSizeBytes
        {
#if UNITY_EDITOR
            get => (long)_editorSimulateDownloadSizeKB * 1024L;
#else
            get => 0L;
#endif
        }

        /// <summary>
        /// EditorSimulate 模拟下载的速度（字节/秒）。0 = 不模拟。下载时长 = 大小 ÷ 速度。
        /// 生产构建始终返回 0（字段由 <c>#if UNITY_EDITOR</c> 包裹，零运行时代价）。
        /// </summary>
        public long EditorSimulateDownloadSpeedBytesPerSec
        {
#if UNITY_EDITOR
            get => (long)_editorSimulateDownloadSpeedKBps * 1024L;
#else
            get => 0L;
#endif
        }

        /// <summary>
        /// 运行期实际生效的模式。WebGL 平台强制远端 Web 模式，避免构建后误用本地或编辑器模式。
        /// </summary>
        public AssetPlayMode ActualPlayMode
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return AssetPlayMode.Web;
#else
                return _playMode;
#endif
            }
        }

        /// <summary>
        /// 导出供加载层使用的运行时配置 DTO（<see cref="AssetProviderConfig"/>，框架自有、无 Unity 依赖）。
        /// 把「Model 字段 → provider 配置」的映射<b>收口在一处</b>：新增配置项时只改这里 + 对应字段，
        /// 不必再去 <see cref="AssetInitSystem"/> 同步手抄。
        /// <para>注意：默认包名 / 运行模式<b>不在</b>此 DTO 里——它们是「初始化身份 / 模式」参数（标识初始化哪个包、用哪种模式），
        /// 由 <see cref="AssetInitSystem"/> 直接传给 <c>Configure</c> / <c>InitializePackageAsync</c>，与「provider 跑起来需要的配置」是两类东西。</para>
        /// </summary>
        public AssetProviderConfig ToProviderConfig()
        {
            // 按包收集「禁用按需下载」开关：只把显式列出的包放进表，未列出的包由 provider 按 false（保持自动下载）处理。
            var disableOnDemand = new Dictionary<string, bool>();
            if (_extraPackages != null)
            {
                foreach (var package in _extraPackages)
                {
                    if (package == null || string.IsNullOrWhiteSpace(package.Name)) continue;
                    disableOnDemand[package.Name] = package.DisableOnDemandDownload;
                }
            }

            return new AssetProviderConfig
            {
                CdnUrls = BuildProviderCdnUrls(),
                DisableOnDemandDownloadByPackage = disableOnDemand,
                FileOffset = _fileOffset,
                DownloadingMaxNumber = _downloadingMaxNumber,
                FailedTryAgain = _failedTryAgain,
            };
        }

        // 规范化每条 CDN 根（去首尾空白 + 统一尾斜杠）并按规范化结果去重后交给 provider。
        // 去重的意义：重复地址在 provider 端「逐条尝试」时只会徒增一次必然相同的失败、拉长初始化耗时；
        // 去重后一圈尝试不含冗余。空 / 纯空白条目直接丢弃。
        private string[] BuildProviderCdnUrls()
        {
            if (_cdnUrls == null || _cdnUrls.Count == 0)
                return Array.Empty<string>();

            var urls = new List<string>(_cdnUrls.Count);
            var emitted = new HashSet<string>();
            foreach (var raw in _cdnUrls)
            {
                var url = NormalizeCdnRoot(raw);
                if (url.Length == 0 || !emitted.Add(url)) continue;
                urls.Add(url);
            }
            return urls.ToArray();
        }

        // 与 GameRemoteService.Normalize 同规则：先 Trim 掉首尾空白（防配置里误粘空格 / 换行），再统一成单个尾斜杠。
        private static string NormalizeCdnRoot(string url)
            => string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/') + "/";

        /// <summary>
        /// 枚举需要初始化的包名。默认包优先，后续显式包去重；空配置会被跳过。
        /// </summary>
        public IEnumerable<string> EnumeratePackageNames()
        {
            var emitted = new HashSet<string>();
            if (!string.IsNullOrWhiteSpace(_defaultPackageName) && emitted.Add(_defaultPackageName))
                yield return _defaultPackageName;

            if (_extraPackages == null) yield break;
            foreach (var package in _extraPackages)
            {
                var name = package?.Name;
                if (string.IsNullOrWhiteSpace(name) || !emitted.Add(name)) continue;
                yield return name;
            }
        }
    }

    /// <summary>
    /// 单个资源包配置：包名 + 包级策略。包级开关让「基础包自动下载 / DLC 包手动下载」这类差异按包区分，
    /// 而不影响业务加载 API。未来需要包级 CDN 等更多策略时继续在此扩展。
    /// </summary>
    [Serializable]
    public sealed class AssetPackageConfig
    {
        [Tooltip("资源包名称。为空会被初始化流程忽略。")]
        [SerializeField] private string _name;

        [Tooltip("禁用「按需下载」：开启后，Load 本包内尚未缓存的资源会直接失败（不自动下载），\n" +
                 "强制业务先显式跑下载器（带进度 UI）再加载——用于大型 DLC 包，避免误 Load 一个资源就拖下整批。\n" +
                 "关闭（默认）= 保持底层库行为：Load 未缓存资源时当场按需下载。仅 Host 模式有意义。")]
        [SerializeField] private bool _disableOnDemandDownload = false;

        public string Name => _name;

        /// <summary>是否禁用本包的「按需下载」：true = Load 未缓存资源直接失败、强制先显式下载。仅 Host 模式生效。</summary>
        public bool DisableOnDemandDownload => _disableOnDemandDownload;
    }
}
