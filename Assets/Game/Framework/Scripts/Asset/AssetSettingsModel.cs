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
    /// 同 Context 下只允许一个 AssetSettingsModel（重复注册会被 Container 拒绝）。
    /// </summary>
    public class AssetSettingsModel : MonoModelBase
    {
        [Header("基础配置")]
        [Tooltip("默认资源包名称；未显式指定 package 的加载请求都会使用它。")]
        [FormerlySerializedAs("PackageName")]
        [SerializeField] private string _defaultPackageName = "DefaultPackage";

        [Tooltip("全局运行模式。WebGL 构建会强制使用 Web 模式。")]
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
        [Header("编辑器调试")]
        [Tooltip("EditorSimulate 模式下，当所有资源已就绪（无需真实下载）时，模拟下载进度动画的持续时长（秒）。\n"
               + "0 = 关闭模拟，下载器立即完成；>0 = 在此时长内推进进度从 0 到 1。\n"
               + "仅 Unity Editor 生效，构建版本此字段不存在，框架不会执行任何模拟。")]
        [Min(0f)]
        [SerializeField] private float _editorSimulateDownloadSeconds = 2f;
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
        /// EditorSimulate 模式下模拟下载进度的时长（秒）。0 = 不模拟。
        /// 生产构建始终返回 0（字段由 <c>#if UNITY_EDITOR</c> 包裹，零运行时代价）。
        /// </summary>
        public float EditorSimulateDownloadSeconds
        {
#if UNITY_EDITOR
            get => _editorSimulateDownloadSeconds;
#else
            get => 0f;
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
