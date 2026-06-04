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

        [Tooltip("额外资源包列表。默认包会自动加入，不需要在这里重复配置。")]
        [SerializeField] private List<AssetPackageConfig> _packages = new();

        [Header("CDN 配置")]
        [Tooltip("主 CDN 地址，远端模式会在此地址下查找版本文件和资源包。以 / 结尾或不结尾都行，provider 内部自动规范化。")]
        [FormerlySerializedAs("MainCDNUrl")]
        [SerializeField] private string _mainCdnUrl = "http://127.0.0.1/CDN/";

        [Tooltip("备用 CDN 地址；主地址下载失败时自动回退到此。")]
        [FormerlySerializedAs("FallbackCDNUrl")]
        [SerializeField] private string _fallbackCdnUrl = "http://127.0.0.1/CDN_Fallback/";

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
        public IReadOnlyList<AssetPackageConfig> Packages => _packages;
        public string MainCdnUrl => _mainCdnUrl;
        public string FallbackCdnUrl => _fallbackCdnUrl;
        public int DownloadingMaxNumber => _downloadingMaxNumber;
        public int FailedTryAgain => _failedTryAgain;
        public ulong FileOffset => _fileOffset;

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
        /// 枚举需要初始化的包名。默认包优先，后续显式包去重；空配置会被跳过。
        /// </summary>
        public IEnumerable<string> EnumeratePackageNames()
        {
            var emitted = new HashSet<string>();
            if (!string.IsNullOrWhiteSpace(_defaultPackageName) && emitted.Add(_defaultPackageName))
                yield return _defaultPackageName;

            if (_packages == null) yield break;
            foreach (var package in _packages)
            {
                var name = package?.Name;
                if (string.IsNullOrWhiteSpace(name) || !emitted.Add(name)) continue;
                yield return name;
            }
        }
    }

    /// <summary>
    /// 单个资源包配置。当前只保存名称；未来如果需要包级 CDN 或策略，可以在这里扩展而不影响业务加载 API。
    /// </summary>
    [Serializable]
    public sealed class AssetPackageConfig
    {
        [Tooltip("资源包名称。为空会被初始化流程忽略。")]
        [SerializeField] private string _name;

        public string Name => _name;
    }
}
