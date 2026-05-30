namespace Game.Framework
{
    /// <summary>
    /// 传递给 <see cref="IAssetProvider.InitializeAsync"/> 的运行时配置。
    ///
    /// 框架自有类型，不依赖具体资源库。字段按 <see cref="AssetPlayMode"/> 选择性使用：
    /// <list type="bullet">
    ///   <item><b>EditorSimulate</b>：忽略本配置（除并发/重试外）。</item>
    ///   <item><b>Offline</b>：用 <see cref="FileOffset"/>。</item>
    ///   <item><b>Host</b>：用 <see cref="MainCdnUrl"/> / <see cref="FallbackCdnUrl"/> / <see cref="FileOffset"/>。</item>
    ///   <item><b>Web</b>：用 <see cref="MainCdnUrl"/> / <see cref="FallbackCdnUrl"/>。</item>
    /// </list>
    /// 单个 provider 实现应在初始化时校验自身需要的字段并清晰报错（而不是静默忽略）。
    /// </summary>
    public sealed class AssetProviderConfig
    {
        /// <summary>主 CDN 地址（Host / Web 模式）。Provider 内部自动规范化结尾斜杠。</summary>
        public string MainCdnUrl { get; set; }

        /// <summary>备用 CDN 地址（Host 模式）；主地址失败时回退。</summary>
        public string FallbackCdnUrl { get; set; }

        /// <summary>AssetBundle 文件头偏移字节数（Offline / Host 模式的偏移加密）。0 表示不加密。</summary>
        public ulong FileOffset { get; set; }

        /// <summary>下载器最大并发数。</summary>
        public int DownloadingMaxNumber { get; set; } = 10;

        /// <summary>下载失败重试次数。</summary>
        public int FailedTryAgain { get; set; } = 3;
    }
}
