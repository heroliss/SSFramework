using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Framework
{
    /// <summary>
    /// <see cref="AssetUtility"/> 的序列化运行配置。它描述资源基础设施如何启动，不是游戏业务状态，
    /// 因此作为 Utility 的内嵌设置存在，而不注册为 Model。
    /// </summary>
    /// <remarks>
    /// <para><see cref="Packages"/> 是全部资源包的统一列表；<see cref="DefaultPackageName"/> 只是
    /// 不带 <c>packageName</c> 的便捷重载所使用的默认指针。留空表示没有默认包。</para>
    /// <para>场景路径由 <see cref="AssetUtility"/> 在 <c>Start</c> 自动应用这些设置；代码引导路径则在
    /// <c>Start</c> 前调用 <see cref="AssetUtility.Configure"/>，显式配置会抑制场景自动启动。</para>
    /// </remarks>
    [Serializable]
    public sealed class AssetRuntimeSettings
    {
        private const string DefaultPackage = "DefaultPackage";
        private const string DefaultLocalCdnUrl = "http://127.0.0.1:8080/";

        [Header("基础配置")]
        [Tooltip("全部资源包列表：每个包 = 名字 + 包级策略（是否启动自动初始化 / 是否启用按需下载）。\n" +
                 "默认包也在这里（用下面“默认资源包”指定哪个是默认），与其它包同构、无特例。\n" +
                 "DLC 等包通常设“不自动初始化”，进副本时再由业务 Initialize 触发。\n" +
                 "⚠ 一个既没开自动初始化、也没被 Initialize 触发过的包，Load 它会直接报错（不是无限等待）。")]
        [InspectorName("资源包列表（Packages）")]
        [FormerlySerializedAs("_extraPackages")]
        [SerializeField] private List<AssetPackageConfig> _packages = new() { new AssetPackageConfig(DefaultPackage) };

        [Tooltip("默认资源包：不带 packageName 的 Load(location) 等便捷重载用它。从上面包列表里选。\n" +
                 "留空 = 无默认包：不带 packageName 的加载、以及未显式指定包的 AssetReference 都会报错；\n" +
                 "此时每次加载都要带 packageName，每个 AssetReference 都要显式选包。")]
#if UNITY_EDITOR
        [DefaultAssetPackageName]
#endif
        [InspectorName("默认资源包")]
        [FormerlySerializedAs("PackageName")]
        [SerializeField] private string _defaultPackageName = DefaultPackage;

        [Tooltip("编辑器运行模式（内置首包 = 随包体打进 StreamingAssets 的资源；远端 = CDN）：\n" +
                 "EditorSimulate = 编辑器直接读 AssetDatabase，免打包 / 免下载（开发期默认）；\n" +
                 "Offline = 仅内置首包（StreamingAssets），完全不联网；\n" +
                 "Host = 内置首包 + 远端 CDN，缺的按需下载并缓存；\n" +
                 "Web = 纯远端 HTTP，不落地缓存。\n" +
                 "只在编辑器 Play 生效；玩家包使用下面的“玩家包运行模式”。")]
        [InspectorName("编辑器运行模式")]
        [FormerlySerializedAs("PlayMode")]
        [SerializeField] private AssetPlayMode _playMode = AssetPlayMode.EditorSimulate;

        [Tooltip("玩家包运行模式（构建出的玩家端实际用的模式，编辑器 Play 不用它）：\n" +
                 "Offline = 仅内置首包，完全不联网（默认）；\n" +
                 "Host = 内置首包 + 远端 CDN，缺的按需下载并缓存；\n" +
                 "Web = 纯远端 HTTP（WebGL 构建会强制此模式）。\n" +
                 "⚠ 不能选 EditorSimulate；模拟模式依赖 AssetDatabase，只存在于编辑器。")]
        [InspectorName("玩家包运行模式")]
        [SerializeField] private AssetPlayMode _playerPlayMode = AssetPlayMode.Offline;

        [Header("CDN 配置")]
        [Tooltip("CDN 地址列表（远端模式查找版本文件 / 资源包）。第一条为主地址，其余为备用。\n" +
                 "provider 会规范化尾斜杠，并按包名追加子目录。\n" +
                 "本地联调端口须等于构建 profile 的 LocalServePort（默认 8080）。留空表示未配置远端。")]
        [InspectorName("CDN 地址列表")]
        [SerializeField] private List<string> _cdnUrls = new() { DefaultLocalCdnUrl };

        [Header("下载器配置")]
        [Tooltip("同时下载的最大文件数。值越大并发越高，但占用带宽和系统资源也越多。建议 4-16。")]
        [InspectorName("最大并发下载数")]
        [FormerlySerializedAs("DownloadingMaxNumber")]
        [Min(1)] [SerializeField] private int _downloadingMaxNumber = 10;

        [Tooltip("单个文件下载失败后的重试次数。设为 0 则失败立即放弃。")]
        [InspectorName("失败重试次数")]
        [FormerlySerializedAs("FailedTryAgain")]
        [Min(0)] [SerializeField] private int _failedTryAgain = 3;

        [Header("加密")]
        [Tooltip("AssetBundle 文件头偏移加密：运行时加载时跳过的字节数。\n" +
                 "⚠ 必须与构建配置 FrameworkAssetBuildProfile.FileOffset 完全一致。\n" +
                 "0 = 不加密；内容加密经 GameAssetDecryption 接入，见 docs/asset-encryption.md。")]
        [InspectorName("文件头偏移字节数")]
        [FormerlySerializedAs("FileOffset")]
        [Min(0)] [SerializeField] private ulong _fileOffset;

        public AssetRuntimeSettings() { }

        internal AssetRuntimeSettings(
            IReadOnlyList<AssetPackageConfig> packages,
            string defaultPackageName,
            AssetPlayMode playMode,
            AssetPlayMode playerPlayMode,
            IReadOnlyList<string> cdnUrls,
            int downloadingMaxNumber,
            int failedTryAgain,
            ulong fileOffset)
        {
            _packages = new List<AssetPackageConfig>();
            if (packages != null)
                for (int i = 0; i < packages.Count; i++)
                    if (packages[i] != null) _packages.Add(packages[i].Clone());
            _defaultPackageName = defaultPackageName ?? string.Empty;
            _playMode = playMode;
            _playerPlayMode = playerPlayMode;
            _cdnUrls = cdnUrls == null ? new List<string>() : new List<string>(cdnUrls);
            _downloadingMaxNumber = downloadingMaxNumber;
            _failedTryAgain = failedTryAgain;
            _fileOffset = fileOffset;
        }

        /// <summary>不带 packageName 的便捷重载所使用的默认包；可能为空。</summary>
        public string DefaultPackageName => _defaultPackageName;

        /// <summary>编辑器 Play 使用的显式模式。</summary>
        public AssetPlayMode PlayMode => _playMode;

        /// <summary>全部已登记资源包及其启动、按需下载策略。</summary>
        public IReadOnlyList<AssetPackageConfig> Packages => _packages;

        /// <summary>远端模式依次尝试的 CDN 根地址。</summary>
        public IReadOnlyList<string> CdnUrls => _cdnUrls;

        /// <summary>当前运行环境实际生效的模式。</summary>
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

        /// <summary>导出 provider 使用的纯运行时配置 DTO。</summary>
        public AssetProviderConfig ToProviderConfig()
        {
            var enableOnDemand = new Dictionary<string, bool>();
            if (_packages != null)
            {
                foreach (AssetPackageConfig package in _packages)
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

        /// <summary>枚举全部登记的包名（去重、跳过空名）。</summary>
        public IEnumerable<string> EnumeratePackageNames()
        {
            if (_packages == null) yield break;
            var emitted = new HashSet<string>();
            foreach (AssetPackageConfig package in _packages)
            {
                string name = package?.Name;
                if (string.IsNullOrWhiteSpace(name) || !emitted.Add(name)) continue;
                yield return name;
            }
        }

        /// <summary>该包是否登记为启动时自动初始化。</summary>
        public bool ShouldAutoInitialize(string packageName)
        {
            AssetPackageConfig config = FindPackage(packageName);
            return config != null && config.AutoInitialize;
        }

        /// <summary>返回首个配置一致性错误；配置有效时返回 null。</summary>
        public string GetConfigError()
        {
            if (!string.IsNullOrWhiteSpace(_defaultPackageName) && FindPackage(_defaultPackageName) == null)
                return $"默认包 '{_defaultPackageName}' 不在资源包列表中——请在列表里加一条同名包，或清空“默认资源包”" +
                       "（清空 = 无默认包，加载须用带 packageName 的重载）。";
            if (_playerPlayMode == AssetPlayMode.EditorSimulate)
                return "玩家包运行模式不能是 EditorSimulate（模拟模式只存在于编辑器）——单机包选 Offline，资源热更选 Host。";
            return null;
        }

        private string[] BuildProviderCdnUrls()
        {
            if (_cdnUrls == null || _cdnUrls.Count == 0) return Array.Empty<string>();
            var urls = new List<string>(_cdnUrls.Count);
            var emitted = new HashSet<string>();
            foreach (string raw in _cdnUrls)
            {
                string url = string.IsNullOrWhiteSpace(raw)
                    ? string.Empty
                    : raw.Trim().TrimEnd('/') + "/";
                if (url.Length == 0 || !emitted.Add(url)) continue;
                urls.Add(url);
            }
            return urls.ToArray();
        }

        private AssetPackageConfig FindPackage(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName) || _packages == null) return null;
            foreach (AssetPackageConfig package in _packages)
                if (package != null && package.Name == packageName) return package;
            return null;
        }
    }

    /// <summary>单个资源包的名称、启动策略与按需下载策略。</summary>
    [Serializable]
    public sealed class AssetPackageConfig
    {
        [Tooltip("资源包名称（须与构建收集器 AssetBundleCollector 中定义的包名一致）。")]
#if UNITY_EDITOR
        [BuildAssetPackageName]
#endif
        [InspectorName("资源包名")]
        [SerializeField] private string _name;

        [Tooltip("进 Play 是否自动初始化本包。关闭后需由业务在合适时机显式调用 IAssetUtility.Initialize。")]
        [InspectorName("启动时自动初始化")]
        [SerializeField] private bool _autoInitialize = true;

        [Tooltip("Load 尚未缓存的资源时是否允许当场下载。关闭后必须先显式运行下载器；仅 Host 模式有意义。")]
        [InspectorName("允许按需下载")]
        [SerializeField] private bool _enableOnDemandDownload = true;

        public AssetPackageConfig() { }

        public AssetPackageConfig(string name, bool autoInitialize = true, bool enableOnDemandDownload = true)
        {
            _name = name;
            _autoInitialize = autoInitialize;
            _enableOnDemandDownload = enableOnDemandDownload;
        }

        /// <summary>资源包名称。</summary>
        public string Name => _name;
        /// <summary>是否在场景入口启动时自动初始化。</summary>
        public bool AutoInitialize => _autoInitialize;
        /// <summary>Host 模式下是否允许 Load 对未缓存内容按需下载。</summary>
        public bool EnableOnDemandDownload => _enableOnDemandDownload;

        internal AssetPackageConfig Clone() =>
            new(_name, _autoInitialize, _enableOnDemandDownload);

#if UNITY_EDITOR
        /// <summary>由构建编辑器模块注入的已知资源包名提供器。</summary>
        public static Func<IEnumerable<string>> EditorBuildPackageNamesProvider;

        internal static IEnumerable<string> EnumerateEditorPackageNames()
        {
            Func<IEnumerable<string>> provider = EditorBuildPackageNamesProvider;
            if (provider == null) yield break;
            foreach (string name in provider())
                if (!string.IsNullOrWhiteSpace(name)) yield return name;
        }
#endif
    }

#if UNITY_EDITOR
    internal sealed class DefaultAssetPackageNameAttribute : PropertyAttribute { }
    internal sealed class BuildAssetPackageNameAttribute : PropertyAttribute { }
#endif
}
