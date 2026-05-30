using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace Game.Framework
{
    /// <summary>
    /// 资源 provider 抽象边界。
    ///
    /// 把“底层资源库实际怎么加载”的细节关进单一接口，
    /// 上层 <see cref="AssetUtility"/> 仅负责多包状态管理 + 类型化 handle 包装。
    /// 切换底层资源库时只需新加一个实现，不动 Utility / Settings / InitSystem。
    ///
    /// 设计要点：
    /// <list type="bullet">
    ///   <item><b>非泛型 + Type 参数</b>：泛型留在 <see cref="IAssetUtility.Load{T}"/> 公开 API 层；
    ///         provider 不重复实现 Component → GameObject 解析（这是框架共性逻辑）。</item>
    ///   <item><b>每个 Load 调用返回独立 handle</b>：handle 是 ref-count token，provider 不能跨调用共享实例。</item>
    ///   <item><b>每个 package 独立 init</b>：单包失败不影响其他包，由调用方按 packageName 串行/并行调度。</item>
    /// </list>
    /// </summary>
    internal interface IAssetProvider : IDisposable
    {
        /// <summary>初始化指定包。重复调用应是 idempotent（已 ready 则立即返回）。</summary>
        UniTask InitializeAsync(string packageName, AssetPlayMode mode, AssetProviderConfig config, CancellationToken ct);

        /// <summary>包是否已就绪可以发起加载。</summary>
        bool IsPackageReady(string packageName);

        /// <summary>
        /// 按 location 或 GUID 加载一个 UnityEngine.Object。
        /// 返回的 handle 是独立的引用计数 token；Dispose 时释放一份引用。
        /// 加载失败、参数无效或资源类型不匹配（Provider 自行判断）时返回 null。
        /// </summary>
        UniTask<IAssetHandle<UnityEngine.Object>> LoadAssetAsync(
            string packageName, string locationOrGuid, bool byGuid, Type type, CancellationToken ct);

        /// <summary>加载场景。返回的 handle 负责 Activate/UnSuspend/Unload。</summary>
        UniTask<ISceneHandle> LoadSceneAsync(
            string packageName, string location, LoadSceneMode mode, bool suspendLoad, CancellationToken ct);

        /// <summary>加载 RawFile 文本。Provider 内部管理临时 handle 释放，调用方只拿到值。</summary>
        UniTask<string> LoadTextAsync(string packageName, string location, CancellationToken ct);

        /// <summary>加载 RawFile 二进制。语义同 <see cref="LoadTextAsync"/>。</summary>
        UniTask<byte[]> LoadBytesAsync(string packageName, string location, CancellationToken ct);

        /// <summary>检查 location 是否能在指定包的 manifest 中解析。未初始化时返回 false。</summary>
        bool CheckLocationValid(string packageName, string location);

        /// <summary>检查指定资源是否需要从远端下载。未初始化时返回 false。</summary>
        bool IsNeedDownload(string packageName, string location);

        /// <summary>
        /// 创建按 tag 的下载器。
        /// 进度通过 <see cref="IAssetDownloader.Progress"/> 暴露，
        /// provider 实现需保证回调在 Unity 主线程触发（参见 <see cref="IAssetDownloader"/> 文档）。
        /// </summary>
        IAssetDownloader CreateTagDownloader(
            string packageName, IReadOnlyList<string> tags, int maxConcurrent, int retries);
    }
}
