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
    /// 资源系统初始化状态。通过 <see cref="IAssetUtility.InitState"/> 以状态流形式暴露，供订阅方观察初始化进展。
    /// </summary>
    public enum AssetInitState
    {
        /// <summary>尚未开始、也没有任何初始化被安排：既没配自动初始化、也没人显式触发。
        /// 此状态下 <c>Load</c> / <c>EnsureInitialized</c> 会<b>抛「未初始化」异常</b>（而非无限等待），提示先 <c>Initialize</c> 或为该包开启自动初始化。</summary>
        Idle,
        /// <summary>已登记、排队中、尚未开跑（自动初始化批次里还没轮到它，或刚被请求初始化还没开始）。
        /// 此状态下 <c>Load</c> / <c>EnsureInitialized</c> 会<b>等待</b>，直到转入 <see cref="Ready"/> / <see cref="Failed"/>。</summary>
        Pending,
        /// <summary>初始化流程进行中（拉版本 / 清单）。<c>Load</c> / <c>EnsureInitialized</c> 等待其完成。</summary>
        Initializing,
        /// <summary>初始化完成，可以发起加载。</summary>
        Ready,
        /// <summary>初始化失败，等待或加载该包会抛出初始化异常。</summary>
        Failed,
    }

    /// <summary>
    /// 资源地址在指定包当前清单与本地缓存下的同步快照。
    /// 它把旧式布尔查询中混在一起的“包还不能查、地址不存在、资源已在本地、资源需远端下载”拆成互斥状态，
    /// 让调用方不必先守卫初始化状态、再拼接两次查询结果。
    /// </summary>
    public enum AssetLocationState
    {
        /// <summary>
        /// 包尚未 <see cref="AssetInitState.Ready"/>，当前无法解释该地址。
        /// 用 <see cref="IAssetUtility.GetInitState(string)"/> 可进一步区分 Idle / Pending / Initializing / Failed。
        /// </summary>
        PackageNotReady,

        /// <summary>地址为空，或已就绪包的 manifest 中不存在该地址。</summary>
        Invalid,

        /// <summary>地址有效，所需内容已内置或已缓存，本次无需远端下载。</summary>
        AvailableLocally,

        /// <summary>地址有效，但所需内容尚未在本地，需要从远端下载。</summary>
        RequiresDownload,
    }

    /// <summary>
    /// 缓存清理方式。清理的是「已下载到本地沙盒的 bundle 缓存」（Host / Web 远端模式才有实际内容），
    /// 跟「卸载内存里已加载的资源」是两回事——它只删盘上的下载文件并同步更新内存缓存记录，
    /// 清理后对应资源的 <see cref="AssetLocationState"/> 会重新变为 <see cref="AssetLocationState.RequiresDownload"/>，
    /// 可在不重启的情况下重新下载。
    /// </summary>
    public enum AssetCacheClearMode
    {
        /// <summary>清理未被当前版本清单引用的 bundle：热更到新版本后回收旧版本残留，最常用的「省空间」清理。</summary>
        Unused,
        /// <summary>清理全部已缓存 bundle：用于资源损坏恢复、强制全量重下、整体清空缓存。</summary>
        All,
    }

    /// <summary>
    /// 资源加载工具。框架统一的资源入口，业务层通过 <c>GetUtility&lt;IAssetUtility&gt;</c> 访问。
    ///
    /// 设计原则：
    /// - Utility 只管理 package 初始化状态和类型化加载 API；底层资源库细节由 provider 适配层隐藏。
    /// - 业务通常不直接调用 utility 的加载方法，而是经由 <see cref="DisposableBag"/> 的 <c>Load</c>/<c>LoadScene</c> 等同名方法。
    /// - 默认包重载兼容最常见用法；跨包资源使用带 packageName 的显式重载。
    /// - 全部异步公共方法返回 <c>UniTask</c>，按“无同步对应版本”约定统一省略 <c>Async</c> 后缀（加载 / 清缓存 / 卸载 / 初始化等）。
    /// </summary>
    /// <remarks>
    /// <b>线程：</b>本 Utility 与返回的状态流由 Unity 主线程独占，所有入口从主线程调用。Provider 可在任意线程
    /// 物理完成；Initialize / EnsureInitialized / Load / LoadScene / LoadText / LoadBytes / 缓存维护的成功、异常或取消
    /// 都会回到 Unity 主线程再交付，因此调用方 await 后可安全继续操作 Context、Bag、Model 与 Unity 对象。<br/>
    /// <b>Utility 生命周期：</b>宿主组件销毁会取消仍在运行的初始化 / 维护 owner，并正常完结已经取得的
    /// <see cref="InitState"/> / <see cref="GetInitState(string)"/> 状态流；之后通过旧 Utility 引用重新查询状态会抛
    /// <see cref="ObjectDisposedException"/>，不会悄悄创建一个脱离 Context 的新状态流。Provider 释放异常会进入日志，
    /// 但不会截断状态流完结与 Context 反注册。<br/>
    /// <b>包生命周期：</b>已初始化的 package 在 utility 整个生命周期内**视为全局单例**，框架不提供 <c>UnloadPackage</c> API。
    /// 原因：单个 package 内有大量散落的 <see cref="IAssetHandle{T}"/>（散布在各 Bag、AssetReference 中），框架无法可靠确认它们都已释放；
    /// 强制卸载会让仍持有 handle 的业务收到不可预期的 null Asset / 异常。
    /// 如果项目确实需要"加载 DLC → 用完释放"的场景，按以下方式之一处理：
    /// <list type="bullet">
    ///   <item>把 DLC 资源全部放在独立 Context 下，Context Dispose 时所有 Bag 级联释放 handle，再由业务直接调用底层 provider 的卸载 API。</item>
    ///   <item>用 <see cref="AssetReference{T}.Unload"/> / <see cref="DisposableBag.Dispose"/> 显式释放 handle 让 bundle 引用归零，再调 <see cref="UnloadUnusedAssets(CancellationToken)"/> 把零引用 bundle 从内存卸载（底层库不会自动回收、须显式调）。</item>
    /// </list>
    /// 注意区分：上面说的是「卸载内存里已加载的资源」；要清理「已下载到磁盘的 bundle 缓存」（省空间 / 强制重下）用
    /// <see cref="ClearCache(AssetCacheClearMode, CancellationToken)"/>，两者互不相关。
    /// </remarks>
    public interface IAssetUtility : IUtility
    {
        /// <summary>默认资源包是否已就绪。</summary>
        bool IsInitialized { get; }

        /// <summary>
        /// 当前生效的运行模式（EditorSimulate / Offline / Host / Web），决定资源来源与是否走 CDN / 真实下载。
        /// 由 <see cref="AssetUtility.Settings"/> 或代码引导的 <see cref="AssetUtility.Configure"/> 写入；
        /// 首次包初始化前不要读取它做业务决策。
        /// </summary>
        AssetPlayMode CurrentPlayMode { get; }

        /// <summary>
        /// 默认资源包初始化状态流。
        /// View 通过 <c>Bag.Subscribe(InitState, ...)</c> 订阅启动进度，订阅时立即拿到当前状态（R3 内置）。
        /// Utility 销毁时流正常完结；销毁后重新读取抛 <see cref="ObjectDisposedException"/>。
        /// </summary>
        ReadOnlyReactiveProperty<AssetInitState> InitState { get; }

        /// <summary>
        /// 查询指定包的初始化状态；packageName 为空时返回默认包状态。Utility 销毁时已取得的流正常完结；
        /// 销毁后重新查询抛 <see cref="ObjectDisposedException"/>。
        /// </summary>
        ReadOnlyReactiveProperty<AssetInitState> GetInitState(string packageName);

        /// <summary>
        /// 等待默认资源包初始化完成。加载方法内部已经隐式调用过它。
        /// <para>包为 <see cref="AssetInitState.Idle"/>（既没配自动初始化、也没人触发）时<b>抛异常</b>而非无限等待——
        /// 这种包需先 <see cref="Initialize"/> 或为它开启自动初始化。<see cref="AssetInitState.Failed"/> 抛初始化异常。</para>
        /// </summary>
        UniTask EnsureInitialized(CancellationToken ct = default);

        /// <summary>等待指定资源包初始化完成。语义同 <see cref="EnsureInitialized(CancellationToken)"/>：Idle/Failed 抛异常、Pending/Initializing 等待。</summary>
        UniTask EnsureInitialized(string packageName, CancellationToken ct = default);

        /// <summary>
        /// 初始化指定包（packageName 为空时为默认包）：既是「失败后重试」、也是「未自动初始化的包的冷启动入口」。
        /// <para>语义：包当前为 <see cref="AssetInitState.Idle"/> / <see cref="AssetInitState.Pending"/> / <see cref="AssetInitState.Failed"/>
        /// 时（重新）初始化；已 <see cref="AssetInitState.Ready"/> 则直接返回（幂等）；<see cref="AssetInitState.Initializing"/> 则等待本次完成。</para>
        /// <para>使用 <see cref="AssetUtility"/> 已应用的运行模式与配置执行；<b>普通初始化失败不抛</b>——结果写回 <see cref="InitState"/> 供订阅方读取。</para>
        /// <para>调用者 token 只取消当前等待并保持 <see cref="OperationCanceledException"/>；物理初始化由 utility 生命周期继续持有，
        /// 包保持 <see cref="AssetInitState.Initializing"/>，最终仍会转入 <see cref="AssetInitState.Ready"/> / <see cref="AssetInitState.Failed"/>。
        /// 因此取消 loading 页面不会把底层仍在运行的初始化误判成失败，也不会允许同包重入。</para>
        /// <para>典型用途：①初始化失败后不重建实例即可重试；②给某包配了「不自动初始化」（DLC 懒加载 / 隐私同意后再联网）时，
        /// 在合适时机对 <see cref="AssetInitState.Idle"/> 包调用本方法做「冷启动初始化」，再用 <see cref="InitState"/> 驱动 loading。</para>
        /// </summary>
        UniTask Initialize(string packageName = null, CancellationToken ct = default);

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
        /// 包<b>初始化失败</b>（CDN 不可达 / 断网）或<b>尚未初始化</b>（既没开自动初始化、也没 <see cref="Initialize"/> 触发过）→ <b>抛</b>异常（内部先 <see cref="EnsureInitialized(string, CancellationToken)"/>）。
        /// 即「资源级问题给 null、系统级问题给异常」：包 Ready 后只会返 null，会抛只发生在「init 未成功 / 未触发就加载」。要零异常就先确保该包 <see cref="InitState"/>=Ready（自动初始化或先 <see cref="Initialize"/>）再加载。</para>
        /// </summary>
        UniTask<IAssetHandle<T>> Load<T>(string location, CancellationToken ct = default)
            where T : UnityEngine.Object;

        /// <summary>按 location 从指定包加载资源；packageName 为空时使用默认包。</summary>
        UniTask<IAssetHandle<T>> Load<T>(string packageName, string location, CancellationToken ct = default)
            where T : UnityEngine.Object;

        /// <summary>
        /// 从默认包加载场景。<c>suspendLoad=true</c> 时 task 在场景内容读完并停在激活门后返回，
        /// 业务拿到 handle 后显式 <c>UnSuspend</c>；不要把“task 已返回”误解为 Scene 已激活。
        /// </summary>
        UniTask<ISceneHandle> LoadScene(
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default);

        /// <summary>从指定包加载场景；packageName 为空时使用默认包。挂起语义同默认包重载。</summary>
        UniTask<ISceneHandle> LoadScene(
            string packageName,
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default);

        /// <summary>
        /// 从默认包直读文本内容：内容拷出后内部立即释放临时 handle，调用方拿到与资源生命周期无关的纯数据。
        /// 对任何包类型都成立（按包的构建类型自动选通道）；普通 AB 包要求该 location 是文本类资产（.bytes/.txt/.json 等）。
        /// 失败返回 null。
        /// </summary>
        UniTask<string> LoadText(string location, CancellationToken ct = default);

        /// <summary>从指定包直读文本内容；packageName 为空时使用默认包。语义同 <see cref="LoadText(string, CancellationToken)"/>。</summary>
        UniTask<string> LoadText(string packageName, string location, CancellationToken ct = default);

        /// <summary>从默认包直读二进制内容。语义同 <see cref="LoadText(string, CancellationToken)"/>。</summary>
        UniTask<byte[]> LoadBytes(string location, CancellationToken ct = default);

        /// <summary>从指定包直读二进制内容；packageName 为空时使用默认包。</summary>
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
        /// 查询默认包中 location 的四态快照：包未就绪、地址无效、本地可用或需要下载。
        /// <para>空白地址始终为 <see cref="AssetLocationState.Invalid"/>；非空地址在包未 Ready 时为
        /// <see cref="AssetLocationState.PackageNotReady"/>。如需知道未就绪的具体原因，再读 <see cref="InitState"/>。</para>
        /// <para>这是同步快照；底层包正在或已经排队维护时会抛 <see cref="InvalidOperationException"/>，请在维护完成后重试，
        /// 不会阻塞 Unity 主线程或越过 Writer 读取中间态。</para>
        /// </summary>
        AssetLocationState GetLocationState(string location);

        /// <summary>
        /// 查询指定包中 location 的四态快照；packageName 为空时使用默认包。
        /// 状态含义、空地址与维护并发语义同 <see cref="GetLocationState(string)"/>。
        /// </summary>
        AssetLocationState GetLocationState(string packageName, string location);

        /// <summary>
        /// 指定包当前生效的资源版本号（初始化时拉到 / 选定的清单版本）；packageName 为空时查默认包。
        /// 包未就绪（未初始化 / 初始化失败）时返回 <c>null</c>。
        /// <para>典型用途：设置页 / 登录页展示资源版本（客服排查「你是什么版本」）、更新完成后的版本确认。
        /// 版本号只在包初始化时确定——框架刻意<b>不提供</b>运行中重新拉版本的 API（清单换血会让加载到一半的内容版本错乱），
        /// 发新版本后客户端重启（重进 Play）自然拿到，这与主流商业游戏「启动时检查更新」的做法一致。</para>
        /// </summary>
        string GetPackageVersion(string packageName = null);

        /// <summary>
        /// 创建默认包的按 tag 统计和下载资源任务。下载器是创建时缓存状态的同步快照；底层包正在或已经排队维护时，
        /// 创建会 fail-fast，维护完成后重试创建，不会阻塞 Unity 主线程或从变化中的缓存记录生成中间态快照。
        /// </summary>
        IAssetDownloader CreateTagDownloader(params string[] tags);

        /// <summary>创建指定包的按 tag 统计和下载资源任务；packageName 为空时使用默认包。并发与快照语义同默认包重载。</summary>
        IAssetDownloader CreateTagDownloader(string packageName, IReadOnlyList<string> tags);

        /// <summary>
        /// 创建默认包的「全量下载器」：下载该包当前清单下全部尚未缓存的 bundle（无 tag 过滤）。
        /// 适合「把整个包 / 整个 DLC 全量预下」。要求包已就绪；并发与快照语义同 tag 下载器。
        /// </summary>
        IAssetDownloader CreateAllDownloader();

        /// <summary>创建指定包的全量下载器；packageName 为空时使用默认包。并发与快照语义同 tag 下载器。</summary>
        IAssetDownloader CreateAllDownloader(string packageName);

        /// <summary>
        /// 创建默认包的「按 location 下载器」：下载这些资源所需 bundle（含依赖），适合进某功能前点名预下少数已知资源。
        /// manifest 里解析不到的 location 跳过并打 <c>warning</c>。要求包已就绪；并发与快照语义同 tag 下载器。
        /// </summary>
        IAssetDownloader CreateLocationDownloader(params string[] locations);

        /// <summary>创建指定包的按 location 下载器；packageName 为空时使用默认包。并发与快照语义同 tag 下载器。</summary>
        IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations);

        /// <summary>
        /// 清理默认包「已下载到本地的 bundle 缓存」（远端模式才有实际内容）。清理后内存缓存记录同步更新，
        /// 相关资源的 <see cref="GetLocationState(string)"/> 会重新变为 <see cref="AssetLocationState.RequiresDownload"/>，
        /// 可在不重启的情况下重新下载。
        /// 与「不提供 UnloadPackage」不冲突（见类型 remarks）：这只删盘上的下载文件，不动已加载到内存的资源。
        /// 常见用途：整体清空缓存 (<see cref="AssetCacheClearMode.All"/>)、热更后回收旧版本残留 (<see cref="AssetCacheClearMode.Unused"/>)。
        /// <para>同一 utility 内、同一包的三种清缓存与 <see cref="UnloadUnusedAssets(CancellationToken)"/> 共用串行维护 lane。
        /// 调用者取消只停止自己的等待；已启动的物理操作继续到结束，下一项不会提前与它重叠。仍在排队且调用者已取消的项不会启动。
        /// 若底层 provider 的 package 在进程内跨 utility 共享，provider 还须在 Adapter 边界协调加载 / 下载 Reader 与维护 Writer；
        /// 清缓存后此前创建的下载器快照失效，应重新创建。</para>
        /// </summary>
        UniTask ClearCache(AssetCacheClearMode mode = AssetCacheClearMode.Unused, CancellationToken ct = default);

        /// <summary>清理指定包的本地 bundle 缓存；packageName 为空时使用默认包。语义同 <see cref="ClearCache(AssetCacheClearMode, CancellationToken)"/>。</summary>
        UniTask ClearCache(string packageName, AssetCacheClearMode mode = AssetCacheClearMode.Unused, CancellationToken ct = default);

        /// <summary>
        /// 按 tag 清理默认包中这些 tag 标记的「已下载 bundle 缓存」：用于卸载某关卡 / DLC / 子内容的整批资源缓存
        /// （省空间，或强制其下次重新下载）。语义同 <see cref="ClearCache(AssetCacheClearMode, CancellationToken)"/>——
        /// 只删盘上下载文件、不动内存里已加载的资源；清理后这些资源的 <see cref="GetLocationState(string)"/>
        /// 重新变为 <see cref="AssetLocationState.RequiresDownload"/>。
        /// tag 与 <see cref="CreateTagDownloader(string[])"/> 用的是同一套（资源收集时打在 bundle 上的标签）。
        /// <para><b>多 tag 是并集（OR）</b>：命中其中<b>任意一个</b> tag 的 bundle 都会被清，<b>不是</b>「同时带所有 tag 才清」。
        /// 传空数组会抛 <see cref="ArgumentException"/>（避免空集被误当成全清）。</para>
        /// </summary>
        UniTask ClearCacheByTags(IReadOnlyList<string> tags, CancellationToken ct = default);

        /// <summary>按 tag 清理指定包的已下载缓存；packageName 为空时使用默认包。语义同 <see cref="ClearCacheByTags(IReadOnlyList{string}, CancellationToken)"/>（多 tag 并集）。</summary>
        UniTask ClearCacheByTags(string packageName, IReadOnlyList<string> tags, CancellationToken ct = default);

        /// <summary>
        /// 按精确 location 清理默认包中这些资源「已下载的 bundle 缓存」：适合点名驱逐少数已知大资源。
        /// 语义同 <see cref="ClearCacheByTags(IReadOnlyList{string}, CancellationToken)"/>——只删盘上下载文件、不动内存里已加载的资源。
        /// <para><b>清理粒度是 bundle，不是单个资源</b>：每个 location 会解析到它所属的 bundle，整份 bundle 删掉——
        /// 因此<b>同一 bundle 里的其他资源会被连带清掉</b>。这是磁盘缓存以 bundle 为最小单位决定的，无法只清单个资源。
        /// 想精确隔离某资源的缓存，应在打包（AssetBundleCollector）时让它<b>独占一个 bundle</b>（pack-by-file 或独立分组），
        /// 而不是指望此 API 做到资源级精度。逻辑内容组（关卡 / DLC）的整批清理优先用 <see cref="ClearCacheByTags(IReadOnlyList{string}, CancellationToken)"/>。</para>
        /// <para>location 必须是 manifest 里能解析的精确地址（不支持目录前缀 / 通配）；解析不到的地址会被跳过并打 <c>warning</c>
        /// （通常意味着拼错地址 / 传错包，不会无声吞掉）。地址有效但本就没缓存属正常 no-op，不警告。传空数组会抛 <see cref="ArgumentException"/>。</para>
        /// </summary>
        UniTask ClearCacheByLocations(IReadOnlyList<string> locations, CancellationToken ct = default);

        /// <summary>按精确 location 清理指定包的已下载缓存；packageName 为空时使用默认包。语义同 <see cref="ClearCacheByLocations(IReadOnlyList{string}, CancellationToken)"/>（bundle 粒度，连带同 bundle 邻居）。</summary>
        UniTask ClearCacheByLocations(string packageName, IReadOnlyList<string> locations, CancellationToken ct = default);

        /// <summary>
        /// 卸载默认包内「无用」资源——引用计数已归零（handle 都已 <c>Unload</c> / <c>Dispose</c>）的 bundle 从内存卸载，释放其 RAM。
        /// <para>这是释放 handle 之后的「第二步」：<c>Unload</c> / <c>Dispose</c> 只让 bundle 引用归零、变「可卸载」，bundle 仍留在内存
        /// （所以释放后仍能秒加载）；本方法才真正把零引用 bundle 从内存卸掉。底层库<b>不会</b>自动回收、须显式调——常在场景切换 / 关卡结束时调一次。</para>
        /// <para>只卸引用归零的，仍被持有的资源不受影响。与 <see cref="ClearCache(AssetCacheClearMode, CancellationToken)"/> 是两回事：那个删盘上下载文件，这个释放内存。</para>
        /// <para>并发与取消语义同清缓存：它与同包三种清缓存共用串行维护 lane；调用者取消不提前释放正在运行的物理操作。</para>
        /// </summary>
        UniTask UnloadUnusedAssets(CancellationToken ct = default);

        /// <summary>卸载指定包内引用归零的 bundle 释放内存；packageName 为空时使用默认包。语义同 <see cref="UnloadUnusedAssets(CancellationToken)"/>。</summary>
        UniTask UnloadUnusedAssets(string packageName, CancellationToken ct = default);
    }

    /// <summary>
    /// 旧资源布尔查询的源码迁移层。新代码应直接读取 <see cref="AssetLocationState"/>，避免再次把“包未就绪”压成 false。
    /// 扩展方法不占用 <see cref="IAssetUtility"/> 的长期 Interface 表面积，可在调用方迁移完成后独立删除。
    /// </summary>
    public static class AssetUtilityCompatibilityExtensions
    {
        /// <summary>兼容旧调用：仅本地可用或需要下载时返回 true；包未就绪与地址无效均返回 false。</summary>
        [Obsolete("Use GetLocationState(location) so PackageNotReady is not confused with Invalid.")]
        public static bool CheckLocationValid(this IAssetUtility utility, string location)
        {
            var state = utility.GetLocationState(location);
            return state == AssetLocationState.AvailableLocally || state == AssetLocationState.RequiresDownload;
        }

        /// <summary>兼容旧调用：仅本地可用或需要下载时返回 true；包未就绪与地址无效均返回 false。</summary>
        [Obsolete("Use GetLocationState(packageName, location) so PackageNotReady is not confused with Invalid.")]
        public static bool CheckLocationValid(this IAssetUtility utility, string packageName, string location)
        {
            var state = utility.GetLocationState(packageName, location);
            return state == AssetLocationState.AvailableLocally || state == AssetLocationState.RequiresDownload;
        }

        /// <summary>兼容旧调用：仅 <see cref="AssetLocationState.RequiresDownload"/> 返回 true。</summary>
        [Obsolete("Use GetLocationState(location) so PackageNotReady is not confused with AvailableLocally.")]
        public static bool IsNeedDownload(this IAssetUtility utility, string location)
            => utility.GetLocationState(location) == AssetLocationState.RequiresDownload;

        /// <summary>兼容旧调用：仅 <see cref="AssetLocationState.RequiresDownload"/> 返回 true。</summary>
        [Obsolete("Use GetLocationState(packageName, location) so PackageNotReady is not confused with AvailableLocally.")]
        public static bool IsNeedDownload(this IAssetUtility utility, string packageName, string location)
            => utility.GetLocationState(packageName, location) == AssetLocationState.RequiresDownload;
    }
}
