using System;
using System.Collections.Generic;
using Game.Framework.Model;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Framework
{
    /// <summary>
    /// 旧版资源配置组件，仅用于读取尚未迁移的场景数据。
    /// 新场景只需添加 <see cref="AssetUtility"/>，并在其“资源运行配置”中完成设置。
    /// </summary>
    [Obsolete("请迁移为 AssetUtility 单组件入口；配置已内嵌到 AssetUtility.Settings。", false)]
    [AddComponentMenu("")]
    public class AssetSystemConfigModel : MonoModelBase
    {
        private const string DefaultPackage = "DefaultPackage";
        private const string DefaultLocalCdnUrl = "http://127.0.0.1:8080/";

        // 这些字段刻意保持旧名称、类型与 FormerlySerializedAs：外部项目升级包后仍可先无损读取旧场景，
        // 再由 Editor 迁移器复制进 AssetUtility._settings。兼容组件不再作为新设计扩展点。
        [Header("基础配置")]
        [Tooltip("旧版兼容数据。请使用组件菜单迁移为 AssetUtility 单组件入口。")]
        [InspectorName("资源包列表（Packages）")]
        [FormerlySerializedAs("_extraPackages")]
        [SerializeField] private List<AssetPackageConfig> _packages = new() { new AssetPackageConfig(DefaultPackage) };

#if UNITY_EDITOR
        [DefaultAssetPackageName]
#endif
        [InspectorName("默认资源包")]
        [FormerlySerializedAs("PackageName")]
        [SerializeField] private string _defaultPackageName = DefaultPackage;

        [InspectorName("编辑器运行模式")]
        [FormerlySerializedAs("PlayMode")]
        [SerializeField] private AssetPlayMode _playMode = AssetPlayMode.EditorSimulate;

        [InspectorName("玩家包运行模式")]
        [SerializeField] private AssetPlayMode _playerPlayMode = AssetPlayMode.Offline;

        [Header("CDN 配置")]
        [InspectorName("CDN 地址列表")]
        [SerializeField] private List<string> _cdnUrls = new() { DefaultLocalCdnUrl };

        [Header("下载器配置")]
        [InspectorName("最大并发下载数")]
        [FormerlySerializedAs("DownloadingMaxNumber")]
        [Min(1)] [SerializeField] private int _downloadingMaxNumber = 10;

        [InspectorName("失败重试次数")]
        [FormerlySerializedAs("FailedTryAgain")]
        [Min(0)] [SerializeField] private int _failedTryAgain = 3;

        [Header("加密")]
        [InspectorName("文件头偏移字节数")]
        [FormerlySerializedAs("FileOffset")]
        [Min(0)] [SerializeField] private ulong _fileOffset;

        /// <summary>旧版默认包字段；仅供迁移期读取。</summary>
        public string DefaultPackageName => _defaultPackageName;
        /// <summary>旧版编辑器运行模式；仅供迁移期读取。</summary>
        public AssetPlayMode PlayMode => _playMode;
        /// <summary>旧版包列表；仅供迁移期读取。</summary>
        public IReadOnlyList<AssetPackageConfig> Packages => _packages;
        /// <summary>旧版 CDN 列表；仅供迁移期读取。</summary>
        public IReadOnlyList<string> CdnUrls => _cdnUrls;

        /// <summary>旧版环境模式计算；仅供迁移期读取。</summary>
        public AssetPlayMode ActualPlayMode => ToRuntimeSettings().ActualPlayMode;
        /// <summary>把旧字段导出为 provider DTO。</summary>
        public AssetProviderConfig ToProviderConfig() => ToRuntimeSettings().ToProviderConfig();
        /// <summary>枚举旧列表中的有效包名。</summary>
        public IEnumerable<string> EnumeratePackageNames() => ToRuntimeSettings().EnumeratePackageNames();
        /// <summary>读取旧列表中的自动初始化策略。</summary>
        public bool ShouldAutoInitialize(string packageName) => ToRuntimeSettings().ShouldAutoInitialize(packageName);
        /// <summary>校验旧配置并返回首个错误。</summary>
        public string GetConfigError() => ToRuntimeSettings().GetConfigError();

        /// <summary>深拷贝旧序列化字段，避免迁移后删除本组件影响新配置。</summary>
        internal AssetRuntimeSettings ToRuntimeSettings() => new(
            _packages,
            _defaultPackageName,
            _playMode,
            _playerPlayMode,
            _cdnUrls,
            _downloadingMaxNumber,
            _failedTryAgain,
            _fileOffset);
    }
}
