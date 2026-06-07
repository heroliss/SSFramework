using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Utility;
using R3;
using UnityEngine.SceneManagement;

namespace Game.Framework
{
    /// <summary>
    /// 资源系统初始化状态。
    /// View 可以订阅 <see cref="IAssetUtility.InitState"/> 来驱动启动加载界面。
    /// </summary>
    public enum AssetInitState
    {
        /// <summary>尚未开始初始化（Awake 注册完成但 System 还没启动 pipeline）。</summary>
        Idle,
        /// <summary>初始化流程进行中。</summary>
        Initializing,
        /// <summary>初始化完成，可以发起加载。</summary>
        Ready,
        /// <summary>初始化失败，等待或加载该包会抛出初始化异常。</summary>
        Failed,
    }

    /// <summary>
    /// 缓存清理方式。清理的是「已下载到本地沙盒的 bundle 缓存」（Host / Web 远端模式才有实际内容），
    /// 跟「卸载内存里已加载的资源」是两回事——它只删盘上的下载文件并同步更新内存缓存记录，
    /// 清理后对应资源的 <c>IsNeedDownload</c> 会重新变真，可在不重启的情况下重新下载。
    /// </summary>
    public enum AssetCacheClearMode
    {
        /// <summary>清理未被当前版本清单引用的 bundle：热更到新版本后回收旧版本残留，最常用的「省空间」清理。</summary>
        Unused,
        /// <summary>清理全部已缓存 bundle：用于设置里的「清除缓存」、资源损坏恢复、强制全量重下。</summary>
        All,
    }

    /// <summary>
    /// 资源加载工具。框架统一的资源入口，业务层通过 <c>GetUtility&lt;IAssetUtility&gt;</c> 访问。
    ///
    /// 设计原则：
    /// - Utility 只管理 package 初始化状态和类型化加载 API；底层资源库细节由 provider 适配层隐藏。
    /// - 业务通常不直接调用 utility 的加载方法，而是经由 <see cref="DisposableBag"/> 的 <c>Load</c>/<c>LoadScene</c> 等同名方法。
    /// - 默认包重载兼容最常见用法；跨包资源使用带 packageName 的显式重载。
    /// - 全部加载方法返回 <c>UniTask</c>，按“无同步对应版本”约定省略 <c>Async</c> 后缀。
    /// </summary>
    /// <remarks>
    /// <b>包生命周期：</b>已初始化的 package 在 utility 整个生命周期内**视为全局单例**，框架不提供 <c>UnloadPackage</c> API。
    /// 原因：单个 package 内有大量散落的 <see cref="IAssetHandle{T}"/>（散布在各 Bag、AssetReference 中），框架无法可靠确认它们都已释放；
    /// 强制卸载会让仍持有 handle 的业务收到不可预期的 null Asset / 异常。
    /// 如果项目确实需要"加载 DLC → 用完释放"的场景，按以下方式之一处理：
    /// <list type="bullet">
    ///   <item>把 DLC 资源全部放在独立 Context 下，Context Dispose 时所有 Bag 级联释放 handle，再由业务直接调用底层 provider 的卸载 API。</item>
    ///   <item>用 <see cref="AssetReference{T}.Unload"/> / <see cref="DisposableBag.Dispose"/> 显式释放 handle，让底层资源库的 unused-assets GC 自然回收。</item>
    /// </list>
    /// 注意区分：上面说的是「卸载内存里已加载的资源」；要清理「已下载到磁盘的 bundle 缓存」（省空间 / 强制重下）用
    /// <see cref="ClearCacheAsync(AssetCacheClearMode, CancellationToken)"/>，两者互不相关。
    /// </remarks>
    public interface IAssetUtility : IUtility
    {
        /// <summary>默认资源包是否已就绪。</summary>
        bool IsInitialized { get; }

        /// <summary>
        /// 当前生效的运行模式（EditorSimulate / Offline / Host / Web），决定资源来源与是否走 CDN / 真实下载。
        /// <see cref="AssetInitSystem"/> 启动包初始化时写入；首次包初始化前为底层默认值（不要在初始化前读取做决策）。
        /// </summary>
        AssetPlayMode CurrentPlayMode { get; }

        /// <summary>
        /// 默认资源包初始化状态流。
        /// View 通过 <c>Bag.Subscribe(InitState, ...)</c> 订阅启动进度，订阅时立即拿到当前状态（R3 内置）。
        /// </summary>
        ReadOnlyReactiveProperty<AssetInitState> InitState { get; }

        /// <summary>查询指定包的初始化状态；packageName 为空时返回默认包状态。</summary>
        ReadOnlyReactiveProperty<AssetInitState> GetInitState(string packageName);

        /// <summary>等待默认资源包初始化完成。加载方法内部已经隐式调用过它。</summary>
        UniTask EnsureInitialized(CancellationToken ct = default);

        /// <summary>等待指定资源包初始化完成。初始化失败时抛出底层异常。</summary>
        UniTask EnsureInitialized(string packageName, CancellationToken ct = default);

        /// <summary>
        /// 重跑指定包的初始化（packageName 为空时为默认包）：初始化失败后无需重建实例即可再次尝试。
        /// <para>语义：包当前为 <see cref="AssetInitState.Failed"/> 或 <see cref="AssetInitState.Idle"/> 时重新初始化；
        /// 已 <see cref="AssetInitState.Ready"/> 则直接返回（幂等）；进行中则等待本次完成。</para>
        /// <para>用最近一次的运行模式与配置重试，<b>本身不抛异常</b>——结果（成功 / 失败）写回 <see cref="InitState"/> 供订阅方读取。</para>
        /// </summary>
        UniTask RetryInitialize(string packageName = null, CancellationToken ct = default);

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器「模拟断网」开关状态流（仅 Editor）：开启时 provider 的远端请求走不可达地址，使远端拉取失败。
        /// 只作用于<b>新发起的</b>远端请求——已 <see cref="AssetInitState.Ready"/> 的包不会因此回退、已缓存的资源仍能加载；
        /// 故要让某个包的<b>初始化</b>因此失败，须在该包初始化前就开启。订阅即得当前值（R3 内置），Inspector 切换同样会推送给订阅方。
        /// </summary>
        ReadOnlyReactiveProperty<bool> SimulateOffline { get; }

        /// <summary>设置「模拟断网」开关（仅 Editor）；值写回 <see cref="SimulateOffline"/>，订阅方与 Inspector 同步。</summary>
        void SetSimulateOffline(bool on);
#endif

        /// <summary>
        /// 按 location 从默认包加载 UnityEngine.Object 资源。
        /// 返回的 <see cref="IAssetHandle{T}"/> 持有一份引用计数；调用方负责 Dispose（或交给 <see cref="DisposableBag"/> 托管）。
        /// <para><b>失败语义</b>（所有 Load 重载一致，这是刻意的契约）：地址无效 / 类型不符 / 空地址 → 返回 <c>null</c>（不抛，打 warning/error）；
        /// 包<b>初始化</b>失败（CDN 不可达 / 断网）→ <b>抛</b>初始化异常（内部会先 <see cref="EnsureInitialized(string, CancellationToken)"/>）。
        /// 即「资源级问题给 null、系统级问题给异常」：包 Ready 后只会返 null，会抛只发生在「init 成功前就加载」。要零异常就先等 <see cref="InitState"/>=Ready 再加载。</para>
        /// </summary>
        UniTask<IAssetHandle<T>> Load<T>(string location, CancellationToken ct = default)
            where T : UnityEngine.Object;

        /// <summary>按 location 从指定包加载资源；packageName 为空时使用默认包。</summary>
        UniTask<IAssetHandle<T>> Load<T>(string packageName, string location, CancellationToken ct = default)
            where T : UnityEngine.Object;

        /// <summary>从默认包加载场景。suspendLoad=true 时需要业务显式 <c>UnSuspend</c>。</summary>
        UniTask<ISceneHandle> LoadScene(
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default);

        /// <summary>从指定包加载场景；packageName 为空时使用默认包。</summary>
        UniTask<ISceneHandle> LoadScene(
            string packageName,
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default);

        /// <summary>从默认包加载 RawFile 文本内容。读取完成后内部立即释放临时 handle。</summary>
        UniTask<string> LoadText(string location, CancellationToken ct = default);

        /// <summary>从指定包加载 RawFile 文本；packageName 为空时使用默认包。</summary>
        UniTask<string> LoadText(string packageName, string location, CancellationToken ct = default);

        /// <summary>从默认包加载 RawFile 二进制内容。语义同 <see cref="LoadText(string, CancellationToken)"/>。</summary>
        UniTask<byte[]> LoadBytes(string location, CancellationToken ct = default);

        /// <summary>从指定包加载 RawFile 二进制；packageName 为空时使用默认包。</summary>
        UniTask<byte[]> LoadBytes(string packageName, string location, CancellationToken ct = default);

        /// <summary>
        /// 通过 Inspector 序列化的 GUID 从默认包加载资源。仅供 <see cref="AssetReference{T}"/> 等内部框架组件使用。
        /// </summary>
        UniTask<IAssetHandle<T>> LoadByGuid<T>(string guid, CancellationToken ct = default)
            where T : UnityEngine.Object;

        /// <summary>通过 Inspector 序列化的 GUID 从指定包加载资源；packageName 为空时使用默认包。</summary>
        UniTask<IAssetHandle<T>> LoadByGuid<T>(string packageName, string guid, CancellationToken ct = default)
            where T : UnityEngine.Object;

        /// <summary>
        /// 检查默认包中 location 是否能在 manifest 中解析。
        /// 注意：包未就绪（未初始化 / 初始化失败）或参数为空时也返回 false——即 false 不止「地址无效」，
        /// 也可能是「还查不了」。需要区分时先用 <see cref="IsInitialized"/> / <see cref="InitState"/> 确认就绪。
        /// </summary>
        bool CheckLocationValid(string location);

        /// <summary>检查指定包中 location 是否能在 manifest 中解析；packageName 为空时使用默认包。</summary>
        bool CheckLocationValid(string packageName, string location);

        /// <summary>
        /// 检查默认包中指定资源是否需要从远端下载。
        /// 注意：包未就绪（未初始化 / 初始化失败）时也返回 false——即 false 不等于「无需下载 / 已在本地」，
        /// 也可能是「还查不了」。需要区分时先用 <see cref="IsInitialized"/> / <see cref="InitState"/> 确认就绪。
        /// </summary>
        bool IsNeedDownload(string location);

        /// <summary>检查指定包中资源是否需要从远端下载；packageName 为空时使用默认包。</summary>
        bool IsNeedDownload(string packageName, string location);

        /// <summary>创建默认包的按 tag 统计和下载资源任务。</summary>
        IAssetDownloader CreateTagDownloader(params string[] tags);

        /// <summary>创建指定包的按 tag 统计和下载资源任务；packageName 为空时使用默认包。</summary>
        IAssetDownloader CreateTagDownloader(string packageName, IReadOnlyList<string> tags);

        /// <summary>
        /// 创建默认包的「全量下载器」：下载该包当前清单下全部尚未缓存的 bundle（无 tag 过滤）。
        /// 适合「把整个包 / 整个 DLC 全量预下」。要求包已就绪。
        /// </summary>
        IAssetDownloader CreateAllDownloader();

        /// <summary>创建指定包的全量下载器；packageName 为空时使用默认包。</summary>
        IAssetDownloader CreateAllDownloader(string packageName);

        /// <summary>
        /// 创建默认包的「按 location 下载器」：下载这些资源所需 bundle（含依赖），适合进某功能前点名预下少数已知资源。
        /// manifest 里解析不到的 location 跳过并打 <c>warning</c>。要求包已就绪。
        /// </summary>
        IAssetDownloader CreateLocationDownloader(params string[] locations);

        /// <summary>创建指定包的按 location 下载器；packageName 为空时使用默认包。</summary>
        IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations);

        /// <summary>
        /// 清理默认包「已下载到本地的 bundle 缓存」（远端模式才有实际内容）。清理后内存缓存记录同步更新，
        /// 相关资源的 <see cref="IsNeedDownload(string)"/> 会重新变真，可在不重启的情况下重新下载。
        /// 与「不提供 UnloadPackage」不冲突（见类型 remarks）：这只删盘上的下载文件，不动已加载到内存的资源。
        /// 常见用途：设置里的「清除缓存」(<see cref="AssetCacheClearMode.All"/>)、热更后回收旧版本残留 (<see cref="AssetCacheClearMode.Unused"/>)。
        /// </summary>
        UniTask ClearCacheAsync(AssetCacheClearMode mode = AssetCacheClearMode.Unused, CancellationToken ct = default);

        /// <summary>清理指定包的本地 bundle 缓存；packageName 为空时使用默认包。语义同 <see cref="ClearCacheAsync(AssetCacheClearMode, CancellationToken)"/>。</summary>
        UniTask ClearCacheAsync(string packageName, AssetCacheClearMode mode = AssetCacheClearMode.Unused, CancellationToken ct = default);

        /// <summary>
        /// 按 tag 清理默认包中这些 tag 标记的「已下载 bundle 缓存」：用于卸载某关卡 / DLC / 子内容的整批资源缓存
        /// （省空间，或强制其下次重新下载）。语义同 <see cref="ClearCacheAsync(AssetCacheClearMode, CancellationToken)"/>——
        /// 只删盘上下载文件、不动内存里已加载的资源；清理后这些资源的 <see cref="IsNeedDownload(string)"/> 重新变真。
        /// tag 与 <see cref="CreateTagDownloader(string[])"/> 用的是同一套（资源收集时打在 bundle 上的标签）。
        /// <para><b>多 tag 是并集（OR）</b>：命中其中<b>任意一个</b> tag 的 bundle 都会被清，<b>不是</b>「同时带所有 tag 才清」。
        /// 传空数组会抛 <see cref="ArgumentException"/>（避免空集被误当成全清）。</para>
        /// </summary>
        UniTask ClearCacheByTagsAsync(IReadOnlyList<string> tags, CancellationToken ct = default);

        /// <summary>按 tag 清理指定包的已下载缓存；packageName 为空时使用默认包。语义同 <see cref="ClearCacheByTagsAsync(IReadOnlyList{string}, CancellationToken)"/>（多 tag 并集）。</summary>
        UniTask ClearCacheByTagsAsync(string packageName, IReadOnlyList<string> tags, CancellationToken ct = default);

        /// <summary>
        /// 按精确 location 清理默认包中这些资源「已下载的 bundle 缓存」：适合点名驱逐少数已知大资源。
        /// 语义同 <see cref="ClearCacheByTagsAsync(IReadOnlyList{string}, CancellationToken)"/>——只删盘上下载文件、不动内存里已加载的资源。
        /// <para><b>清理粒度是 bundle，不是单个资源</b>：每个 location 会解析到它所属的 bundle，整份 bundle 删掉——
        /// 因此<b>同一 bundle 里的其他资源会被连带清掉</b>。这是磁盘缓存以 bundle 为最小单位决定的，无法只清单个资源。
        /// 想精确隔离某资源的缓存，应在打包（AssetBundleCollector）时让它<b>独占一个 bundle</b>（pack-by-file 或独立分组），
        /// 而不是指望此 API 做到资源级精度。逻辑内容组（关卡 / DLC）的整批清理优先用 <see cref="ClearCacheByTagsAsync(IReadOnlyList{string}, CancellationToken)"/>。</para>
        /// <para>location 必须是 manifest 里能解析的精确地址（不支持目录前缀 / 通配）；解析不到的地址会被跳过并打 <c>warning</c>
        /// （通常意味着拼错地址 / 传错包，不会无声吞掉）。地址有效但本就没缓存属正常 no-op，不警告。传空数组会抛 <see cref="ArgumentException"/>。</para>
        /// </summary>
        UniTask ClearCacheByLocationsAsync(IReadOnlyList<string> locations, CancellationToken ct = default);

        /// <summary>按精确 location 清理指定包的已下载缓存；packageName 为空时使用默认包。语义同 <see cref="ClearCacheByLocationsAsync(IReadOnlyList{string}, CancellationToken)"/>（bundle 粒度，连带同 bundle 邻居）。</summary>
        UniTask ClearCacheByLocationsAsync(string packageName, IReadOnlyList<string> locations, CancellationToken ct = default);
    }
}
