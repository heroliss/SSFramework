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
    ///   <item><b>取消等待不等于强停原生操作</b>：<see cref="AssetUtility"/> 已为初始化与维护拆分短命调用者和 utility owner；
    ///         普通加载 API 收到调用者与 utility 生命周期合并后的 token。实现应在物理操作开始前响应取消；若第三方操作一旦启动就无法安全停止，
    ///         则由 Adapter 继续持有到真实终态，并回收无人接收的成功结果。等待者仍收到 <see cref="OperationCanceledException"/>，不要包装成普通失败。</item>
    ///   <item><b>共享原生包要跨 provider 协调</b>：若多个 provider 实例会复用同一个进程级 package，Adapter 必须让按需加载 / 显式下载
    ///         与清缓存 / 内存维护互斥，不能只依赖单个 <see cref="AssetUtility"/> 的局部 lane。</item>
    /// </list>
    ///
    /// <para>
    /// <b>可见性：</b>public 是因为它是跨程序集 SPI——具体实现住在独立模块程序集
    /// （如 <c>Game.Framework.Asset.Yoo</c>，内核按纪律不引用模块），第三方后端也按此接口自写模块。
    /// 业务<b>消费</b>资源一律走 <see cref="IAssetUtility"/> / Bag，不要直接实现或持有 provider。
    /// </para>
    /// </summary>
    public interface IAssetProvider : IDisposable
    {
        /// <summary>
        /// 初始化指定包。重复调用应是 idempotent（已 ready 则立即返回）。
        /// <paramref name="ct"/> 是当前 utility owner 的生命周期令牌，不是某个 UI 调用者的短期令牌；
        /// 复用进程级原生包的实现仍需把已经开始的不可取消操作持有到真实终态。
        /// </summary>
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

        /// <summary>
        /// 加载场景。返回的 handle 负责 Activate/UnSuspend/Unload；<c>suspendLoad=true</c> 时在内容到达激活门后返回，
        /// 不得继续等待一个必须由调用方拿到 handle 才能解除的 <c>IsDone</c> 状态。
        /// </summary>
        UniTask<ISceneHandle> LoadSceneAsync(
            string packageName, string location, LoadSceneMode mode, bool suspendLoad, CancellationToken ct);

        /// <summary>
        /// 直读文本内容：内容拷出后 Provider 立即释放内部临时 handle，调用方只拿到值。
        /// 对任何包类型都成立（Provider 自行按包的构建类型选择通道，如 RawFile 原生文件 vs 普通 AB 包的文本类资产）；
        /// 普通 AB 包要求该 location 是文本类资产（.bytes/.txt/.json 等）。失败返回 null。
        /// </summary>
        UniTask<string> LoadTextAsync(string packageName, string location, CancellationToken ct);

        /// <summary>直读二进制内容。语义同 <see cref="LoadTextAsync"/>。</summary>
        UniTask<byte[]> LoadBytesAsync(string packageName, string location, CancellationToken ct);

        /// <summary>检查 location 是否能在指定包的 manifest 中解析。未初始化时返回 false。</summary>
        bool CheckLocationValid(string packageName, string location);

        /// <summary>
        /// 检查指定资源是否需要从远端下载。未初始化时返回 false。
        /// 这是同步缓存快照：共享 package 有维护 Writer 活跃或排队时应立即抛 <see cref="InvalidOperationException"/>，
        /// 由调用方在维护完成后重试；实现不得阻塞 Unity 主线程或越过 Writer。
        /// </summary>
        bool IsNeedDownload(string packageName, string location);

        /// <summary>指定包当前生效清单的版本号（初始化时选定的那份）。包未就绪时返回 null。</summary>
        string GetPackageVersion(string packageName);

        /// <summary>
        /// 创建按 tag 的下载器。
        /// 进度通过 <see cref="IAssetDownloader.Progress"/> 暴露，
        /// provider 实现需保证回调在 Unity 主线程触发（参见 <see cref="IAssetDownloader"/> 文档）。
        /// 这是同步缓存快照：共享 package 有维护 Writer 活跃或排队时应 fail-fast，不能阻塞主线程或越过 Writer。
        /// </summary>
        IAssetDownloader CreateTagDownloader(
            string packageName, IReadOnlyList<string> tags, int maxConcurrent, int retries);

        /// <summary>创建「下载该包当前清单下全部尚未缓存 bundle」的下载器（无 tag 过滤）。</summary>
        IAssetDownloader CreateAllDownloader(string packageName, int maxConcurrent, int retries);

        /// <summary>
        /// 创建「下载这些 location 资源所需 bundle（含依赖）」的下载器。
        /// manifest 里解析不到的 location 跳过并打 warning（同 <see cref="ClearCacheByLocationsAsync"/> 的理由）。
        /// </summary>
        IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations, int maxConcurrent, int retries);

        /// <summary>
        /// 清理指定包已下载到本地的 bundle 缓存（删盘 + 同步更新内存缓存记录）。
        /// 要求包已就绪；清理失败应抛异常由上层处理。AssetUtility 会把自身发起的同包清理 / 卸载串行化；
        /// 复用进程级原生包的 Adapter 还必须覆盖其他 utility/provider 发起的加载、下载与维护。
        /// 这里收到的 <paramref name="ct"/> 是当前 utility owner token。
        /// </summary>
        UniTask ClearCacheAsync(string packageName, AssetCacheClearMode mode, CancellationToken ct);

        /// <summary>按 tag 清理指定包中这些 tag 标记的已下载 bundle 缓存（命中任意一个 tag 即清，并集）。要求包已就绪；清理失败应抛异常由上层处理。</summary>
        UniTask ClearCacheByTagsAsync(string packageName, IReadOnlyList<string> tags, CancellationToken ct);

        /// <summary>
        /// 按精确 location 清理指定包中这些资源所在的 bundle 缓存：每个 location 解析到其所属 bundle 后整份删，
        /// 同 bundle 的其他资源会被连带清掉。要求包已就绪；清理失败应抛异常由上层处理。
        /// </summary>
        UniTask ClearCacheByLocationsAsync(string packageName, IReadOnlyList<string> locations, CancellationToken ct);

        /// <summary>
        /// 卸载指定包内引用计数已归零的资源 bundle，释放其内存占用（只卸零引用的，不动仍被持有的）。
        /// 要求包已就绪；失败应抛异常由上层处理。AssetUtility 会把它与同包缓存清理放进同一维护 lane。
        /// </summary>
        UniTask UnloadUnusedAssetsAsync(string packageName, CancellationToken ct);

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器期「模拟断网」开关源：provider 每次解析远端地址时<b>实时</b>读取，返回 true 时把远端地址换成不可达地址，
        /// 使 init / 下载 / 需下载的 Load <b>全部失败</b>。仅 Editor、仅远端模式（Host/Web）有意义；
        /// 不访问远端的 provider 可空实现（忽略此开关）。
        /// </summary>
        Func<bool> SimulateOffline { get; set; }
#endif
    }
}
