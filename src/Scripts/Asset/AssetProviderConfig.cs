using System.Collections.Generic;

namespace Game.Framework
{
    /// <summary>
    /// 传递给 <see cref="IAssetProvider.InitializeAsync"/> 的运行时配置。
    ///
    /// 框架自有类型，不依赖具体资源库。字段按 <see cref="AssetPlayMode"/> 选择性使用：
    /// <list type="bullet">
    ///   <item><b>EditorSimulate</b>：忽略本配置（除并发/重试外）。</item>
    ///   <item><b>Offline</b>：用 <see cref="FileOffset"/>。</item>
    ///   <item><b>Host</b>：用 <see cref="CdnUrls"/> / <see cref="FileOffset"/> / 按包的 <see cref="ShouldDisableOnDemandDownload"/>。</item>
    ///   <item><b>Web</b>：用 <see cref="CdnUrls"/>。</item>
    /// </list>
    /// 单个 provider 实现应在初始化时校验自身需要的字段并清晰报错（而不是静默忽略）。
    /// </summary>
    public sealed class AssetProviderConfig
    {
        /// <summary>
        /// CDN 地址列表（Host / Web 模式）。第一条为主地址，其余为备用——底层库按失败计数在其间轮转。
        /// Provider 内部自动规范化结尾斜杠、并按包名追加子目录。空列表表示未配置远端。
        /// </summary>
        public IReadOnlyList<string> CdnUrls { get; set; }

        /// <summary>
        /// 按包名查的「禁用按需下载」开关（Host 模式）。命中且为 true 时，Load 未缓存资源直接失败（不自动下载）；
        /// 未命中的包按 false 处理（保持自动下载）。由 <see cref="ShouldDisableOnDemandDownload"/> 读取。
        /// </summary>
        public IReadOnlyDictionary<string, bool> DisableOnDemandDownloadByPackage { get; set; }

        /// <summary>AssetBundle 文件头偏移字节数（Offline / Host 模式的偏移加密）。0 表示不加密。</summary>
        public ulong FileOffset { get; set; }

        /// <summary>下载器最大并发数。</summary>
        public int DownloadingMaxNumber { get; set; } = 10;

        /// <summary>下载失败重试次数。</summary>
        public int FailedTryAgain { get; set; } = 3;

        /// <summary>该包是否禁用「按需下载」：true = Load 未缓存资源直接失败、不自动下载。未配置的包返回 false。</summary>
        public bool ShouldDisableOnDemandDownload(string packageName)
            => packageName != null
               && DisableOnDemandDownloadByPackage != null
               && DisableOnDemandDownloadByPackage.TryGetValue(packageName, out var disable)
               && disable;
    }
}
