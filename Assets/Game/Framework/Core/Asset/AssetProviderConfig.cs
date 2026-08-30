using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Game.Framework
{
    /// <summary>
    /// 传递给 <see cref="IAssetProvider.InitializeAsync"/> 的运行时配置。
    ///
    /// 框架自有类型，不依赖具体资源库。字段按 <see cref="AssetPlayMode"/> 选择性使用：
    /// <list type="bullet">
    ///   <item><b>EditorSimulate</b>：忽略本配置（除并发/重试外）。</item>
    ///   <item><b>Offline</b>：用 <see cref="FileOffset"/>。</item>
    ///   <item><b>Host</b>：用 <see cref="CdnUrls"/> / <see cref="FileOffset"/> / 按包的 <see cref="ShouldEnableOnDemandDownload"/>。</item>
    ///   <item><b>Web</b>：用 <see cref="CdnUrls"/> / <see cref="FileOffset"/>；Web 文件系统以内存解密读取偏移包。</item>
    /// </list>
    /// 单个 provider 实现应在初始化时校验自身需要的字段并清晰报错（而不是静默忽略）。
    /// 调用方可用对象初始化器组装本 DTO；<see cref="AssetUtility.Configure"/> 会接管深拷贝快照，
    /// 因此之后修改原 DTO 或它引用的集合不会热换已经配置的 Utility。
    /// </summary>
    public sealed class AssetProviderConfig
    {
        /// <summary>
        /// 内置弱偏移加/解密的现实上限（1 MiB）。偏移只用于破坏文件魔数，继续增大不会提升实际安全性，
        /// 只会按 bundle 放大磁盘、网络与内存成本；强加密应改用项目侧流式 Encryptor / Decryptor。
        /// </summary>
        public const ulong MaxBuiltInFileOffset = 1024UL * 1024UL;

        /// <summary>
        /// CDN 地址列表（Host / Web 模式）。第一条为主地址，其余为备用——底层库按失败计数在其间轮转。
        /// Provider 内部自动规范化结尾斜杠、并按包名追加子目录。空列表表示未配置远端。
        /// </summary>
        public IReadOnlyList<string> CdnUrls { get; set; }

        /// <summary>
        /// 按包名查的「启用按需下载」开关（Host 模式）。命中且为 false 时，Load 未缓存资源直接失败（不自动下载）；
        /// 未命中的包按 true 处理（默认启用按需下载）。由 <see cref="ShouldEnableOnDemandDownload"/> 读取。
        /// </summary>
        public IReadOnlyDictionary<string, bool> EnableOnDemandDownloadByPackage { get; set; }

        /// <summary>AssetBundle 文件头偏移字节数（Offline / Host / Web 模式）。0 表示不加密，内置实现最大为 <see cref="MaxBuiltInFileOffset"/>。</summary>
        public ulong FileOffset { get; set; }

        /// <summary>下载器最大并发数。</summary>
        public int DownloadingMaxNumber { get; set; } = 10;

        /// <summary>下载失败重试次数。</summary>
        public int FailedTryAgain { get; set; } = 3;

        /// <summary>该包是否启用「按需下载」：false = Load 未缓存资源直接失败、不自动下载。未配置的包返回 true（默认启用）。</summary>
        public bool ShouldEnableOnDemandDownload(string packageName)
        {
            if (packageName != null
                && EnableOnDemandDownloadByPackage != null
                && EnableOnDemandDownloadByPackage.TryGetValue(packageName, out var enable))
                return enable;
            return true;
        }

        /// <summary>
        /// 冻结标量并复制集合。Utility 用它隔离调用方与 Provider Adapter 的可变 DTO 所有权，
        /// 避免任一侧在初始化前后静默改写另一侧观察到的运行配置。
        /// </summary>
        internal AssetProviderConfig Snapshot() => new()
        {
            CdnUrls = SnapshotList(CdnUrls),
            EnableOnDemandDownloadByPackage = SnapshotDictionary(EnableOnDemandDownloadByPackage),
            FileOffset = FileOffset,
            DownloadingMaxNumber = DownloadingMaxNumber,
            FailedTryAgain = FailedTryAgain,
        };

        private static IReadOnlyList<T> SnapshotList<T>(IReadOnlyList<T> source)
        {
            if (source == null) return null;
            var copy = new List<T>(source.Count);
            for (int i = 0; i < source.Count; i++) copy.Add(source[i]);
            return copy.AsReadOnly();
        }

        private static IReadOnlyDictionary<TKey, TValue> SnapshotDictionary<TKey, TValue>(
            IReadOnlyDictionary<TKey, TValue> source)
        {
            if (source == null) return null;
            var copy = new Dictionary<TKey, TValue>(source.Count);
            foreach (KeyValuePair<TKey, TValue> pair in source)
                copy.Add(pair.Key, pair.Value);
            return new ReadOnlyDictionary<TKey, TValue>(copy);
        }
    }
}
