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
    ///
    /// <para><b>包模型</b>：<see cref="Packages"/> 是<b>全部</b>资源包的统一列表（每个 = 名字 + 包级策略）；
    /// <see cref="DefaultPackageName"/> 只是指向其中一个的「默认指针」，供不带 packageName 的便捷重载使用。
    /// 留空 = 无默认包，所有加载必须用带 packageName 的重载。这样默认包不再是独立特例、与其它包同构。</para>
    /// </summary>
    public class AssetSystemConfigModel : MonoModelBase
    {
        private const string DefaultPackage = "DefaultPackage";
        private const string DefaultLocalCdnUrl = "http://127.0.0.1:8080/";

        [Header("基础配置")]
        [Tooltip("全部资源包列表：每个包 = 名字 + 包级策略（是否启动自动初始化 / 是否启用按需下载）。\n" +
                 "默认包也在这里（用下面 Default Package 指定哪个是默认），与其它包同构、无特例。\n" +
                 "DLC 等包通常设「不自动初始化」，进副本时再由业务 Initialize 触发。\n" +
                 "⚠ 一个既没开自动初始化、也没被 Initialize 触发过的包，Load 它会直接报错（不是无限等待）。")]
        [FormerlySerializedAs("_extraPackages")]
        [SerializeField] private List<AssetPackageConfig> _packages = new() { new AssetPackageConfig(DefaultPackage) };

        [Tooltip("默认资源包：不带 packageName 的 Load(location) 等便捷重载用它。从上面包列表里选。\n" +
                 "留空 =（无默认包）：不带 packageName 的加载、以及未显式指定包的 AssetReference 都会报错——\n" +
                 "此时每个加载都要带 packageName，每个 AssetReference 都要用其字段右侧的包下拉显式选包。")]
#if UNITY_EDITOR
        [DefaultAssetPackageName]
#endif
        [FormerlySerializedAs("PackageName")]
        [SerializeField] private string _defaultPackageName = DefaultPackage;

        [Tooltip("编辑器运行模式（内置首包 = 随包体打进 StreamingAssets 的资源；远端 = CDN）：\n" +
                 "EditorSimulate = 编辑器直接读 AssetDatabase，免打包 / 免下载（开发期默认）；\n" +
                 "Offline = 仅内置首包（StreamingAssets），完全不联网；\n" +
                 "Host = 内置首包（StreamingAssets）+ 远端 CDN，缺的按需下载并缓存；\n" +
                 "Web = 纯远端 HTTP，不落地缓存。\n" +
                 "只在编辑器 Play 生效——玩家包用下面的「玩家包运行模式」（模拟模式是编辑器专属能力，进不了包）。")]
        [FormerlySerializedAs("PlayMode")]
        [SerializeField] private AssetPlayMode _playMode = AssetPlayMode.EditorSimulate;

        [Tooltip("玩家包运行模式（构建出的玩家端实际用的模式，编辑器 Play 不用它）：\n" +
                 "Offline = 仅内置首包（StreamingAssets），完全不联网（默认，无需任何部署即可出包）；\n" +
                 "Host = 内置首包 + 远端 CDN，缺的按需下载并缓存（资源热更的常规形态）；\n" +
                 "Web = 纯远端 HTTP（WebGL 构建会强制此模式）。\n" +
                 "⚠ 不能选 EditorSimulate——模拟模式依赖 AssetDatabase，只存在于编辑器（选了会在启动校验时报错）。")]
        [SerializeField] private AssetPlayMode _playerPlayMode = AssetPlayMode.Offline;

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
        [Tooltip("AssetBundle 文件头偏移加密：运行时加载时跳过的字节数。\n" +
                 "⚠ 必须与构建配置 FrameworkAssetBuildProfile.FileOffset【完全一致】——构建插几个字节、这里就跳几个字节，对不上会读坏所有 bundle。\n" +
                 "0 = 不加密。偏移是弱加密（仅防扫魔数）；内容加密（XOR/AES）经 GameAssetDecryption 接入，见 docs/asset-encryption.md。")]
        [FormerlySerializedAs("FileOffset")]
        [Min(0)] [SerializeField] private ulong _fileOffset = 0;

        /// <summary>默认资源包名：不带 packageName 的便捷重载用它。可能为空字符串（= 无默认包，加载须带 packageName）。</summary>
        public string DefaultPackageName => _defaultPackageName;

        public AssetPlayMode PlayMode => _playMode;

        /// <summary>
        /// 全部资源包配置（每个 = 名字 + 包级策略）。默认包也在其中，由 <see cref="DefaultPackageName"/> 指定哪个是默认。
        /// 自动初始化、启用按需下载等都按这里的每包配置生效；列表外的包不会自动初始化（需业务 <see cref="IAssetUtility.Initialize"/>）。
        /// </summary>
        public IReadOnlyList<AssetPackageConfig> Packages => _packages;

        public IReadOnlyList<string> CdnUrls => _cdnUrls;
        // 下载并发 / 重试 / 文件偏移没有公共属性：这些纯 provider 配置只经 ToProviderConfig() 流向加载层，
        // 运行期没有合法的外部读取方——需要读时从 DTO（AssetProviderConfig）拿，别在这里重新开洞。

        /// <summary>
        /// 运行期实际生效的模式：编辑器 Play 用「编辑器运行模式」（可选 EditorSimulate），玩家包用「玩家包运行模式」——
        /// 两个字段让同一份场景配置既能在编辑器免打包开发、又能出真实的 Offline / Host 玩家包。
        /// WebGL 平台强制 Web 模式（纯远端是该平台唯一形态）。
        /// </summary>
        public AssetPlayMode ActualPlayMode
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return AssetPlayMode.Web;
#elif UNITY_EDITOR
                return _playMode;
#else
                return _playerPlayMode;
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
            // 按包收集「启用按需下载」开关：只把显式列出的包放进表，未列出的包由 provider 按 true（默认启用）处理。
            var enableOnDemand = new Dictionary<string, bool>();
            if (_packages != null)
            {
                foreach (var package in _packages)
                {
                    if (package == null || string.IsNullOrWhiteSpace(package.Name)) continue;
                    enableOnDemand[package.Name] = package.EnableOnDemandDownload;
                }
            }

            return new AssetProviderConfig
            {
                CdnUrls = BuildProviderCdnUrls(),
                EnableOnDemandDownloadByPackage = enableOnDemand,
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

        /// <summary>枚举全部登记的包名（去重、跳过空名）。默认包也是其中之一，不再单独前置。</summary>
        public IEnumerable<string> EnumeratePackageNames()
        {
            if (_packages == null) yield break;
            var emitted = new HashSet<string>();
            foreach (var package in _packages)
            {
                var name = package?.Name;
                if (string.IsNullOrWhiteSpace(name) || !emitted.Add(name)) continue;
                yield return name;
            }
        }

        /// <summary>该包是否在启动时自动初始化：列表中存在该包且其 AutoInitialize 为 true 才算。未登记的包返回 false（需业务显式 Initialize）。</summary>
        public bool ShouldAutoInitialize(string packageName)
        {
            var cfg = FindPackage(packageName);
            return cfg != null && cfg.AutoInitialize;
        }

        /// <summary>
        /// 校验配置一致性，返回首个错误描述；无误返回 null。校验项：
        /// ① 默认包名非空时必须在包列表中（指向不存在的默认包会让所有便捷重载失效）；
        /// ② 玩家包运行模式不能是 EditorSimulate（模拟模式依赖 AssetDatabase，只存在于编辑器——
        /// 该错误配置在编辑器 Play 完全无症状、进玩家包才在 provider 处炸成 NotSupportedException，必须在启动校验清晰暴露）。
        /// </summary>
        public string GetConfigError()
        {
            if (!string.IsNullOrWhiteSpace(_defaultPackageName) && FindPackage(_defaultPackageName) == null)
                return $"默认包 '{_defaultPackageName}' 不在资源包列表中——请在列表里加一条同名包，或清空 Default Package Name" +
                       "（清空 = 无默认包，加载须用带 packageName 的重载）。";
            if (_playerPlayMode == AssetPlayMode.EditorSimulate)
                return "玩家包运行模式不能是 EditorSimulate（模拟模式只存在于编辑器）——单机包选 Offline，资源热更选 Host。";
            return null;
        }

        private AssetPackageConfig FindPackage(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName) || _packages == null) return null;
            foreach (var package in _packages)
                if (package != null && package.Name == packageName) return package;
            return null;
        }

    }

    /// <summary>
    /// 单个资源包配置：包名 + 包级策略（是否启动自动初始化、是否启用按需下载）。包级开关让
    /// 「基础包启动即就绪 / DLC 包用时再加载」「基础包自动下载 / DLC 包手动下载」这类差异按包区分，而不影响业务加载 API。
    /// 未来需要包级 CDN 等更多策略时继续在此扩展。
    /// </summary>
    [Serializable]
    public sealed class AssetPackageConfig
    {
        [Tooltip("资源包名称（须与构建收集器 AssetBundleCollector 里定义的包名一致）。可从下拉里选已定义的包名，也可手填。为空会被初始化流程忽略。")]
#if UNITY_EDITOR
        [BuildAssetPackageName]
#endif
        [SerializeField] private string _name;

        [Tooltip("进 Play 是否自动初始化本包（拉版本 / 清单，Host 会联网）。\n" +
                 "开（默认）= 启动即初始化；关 = 启动不初始化，需业务在合适时机显式调 IAssetUtility.Initialize(\"包名\") 触发。\n" +
                 "关用于：大型 DLC 包进副本再 init、或隐私同意 / 选区 / 流量确认后再联网拉清单——\n" +
                 "把全部要联网的包都设为关 = 启动前不发生任何网络连接（合规启动）。")]
        [SerializeField] private bool _autoInitialize = true;

        [Tooltip("启用「按需下载」（默认勾选）：Load 本包内尚未缓存的资源时，当场从远端按需下载（保持底层库默认行为）。\n" +
                 "取消勾选 = Load 未缓存资源直接失败（不自动下载），强制业务先显式跑下载器（带进度 UI）再加载——\n" +
                 "用于大型 DLC 包，避免误 Load 一个资源就拖下整批。仅 Host 模式有意义。")]
        [SerializeField] private bool _enableOnDemandDownload = true;

        public AssetPackageConfig() { }

        public AssetPackageConfig(string name, bool autoInitialize = true, bool enableOnDemandDownload = true)
        {
            _name = name;
            _autoInitialize = autoInitialize;
            _enableOnDemandDownload = enableOnDemandDownload;
        }

        public string Name => _name;

        /// <summary>本包是否在启动时自动初始化：true（默认）= 启动即 init；false = 需业务显式 <see cref="IAssetUtility.Initialize"/> 触发（DLC 懒加载 / 合规延迟联网）。</summary>
        public bool AutoInitialize => _autoInitialize;

        /// <summary>本包是否启用「按需下载」：true（默认）= Load 未缓存资源当场按需下载；false = 直接失败、强制先显式下载。仅 Host 模式生效。</summary>
        public bool EnableOnDemandDownload => _enableOnDemandDownload;

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器期「构建收集器包名」提供器：由构建编辑器模块（<c>Game.Framework.Build.Editor</c>，能访问 YooAsset 收集器）在
        /// <c>[InitializeOnLoad]</c> 时注入。运行时程序集不能引用 <c>YooAsset.Editor</c>，故用「运行时定义钩子、构建编辑器填充」的
        /// 反向依赖来给包名字段做下拉，既不破 asmdef 边界、也不用反射。
        /// </summary>
        public static Func<IEnumerable<string>> EditorBuildPackageNamesProvider;

        /// <summary>
        /// 返回编辑器当前已知的构建包名。原生 PropertyDrawer 用它提供候选列表，同时保留自由输入，
        /// 因此构建 Adapter 未安装或还没创建收集器时不会阻塞配置。
        /// </summary>
        internal static IEnumerable<string> EnumerateEditorPackageNames()
        {
            var provider = EditorBuildPackageNamesProvider;
            if (provider == null) yield break;
            foreach (var name in provider())
                if (!string.IsNullOrWhiteSpace(name))
                    yield return name;
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>标记“从同一资源配置的包列表选择默认包”的原生 Inspector 字段。</summary>
    internal sealed class DefaultAssetPackageNameAttribute : PropertyAttribute { }

    /// <summary>标记“可从构建收集器选择、也允许手填”的资源包名字段。</summary>
    internal sealed class BuildAssetPackageNameAttribute : PropertyAttribute { }
#endif
}
