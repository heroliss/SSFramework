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
    /// </remarks>
    public interface IAssetUtility : IUtility
    {
        /// <summary>默认资源包是否已就绪。</summary>
        bool IsInitialized { get; }

        /// <summary>
        /// 当前生效的运行模式。
        /// <see cref="AssetInitSystem"/> 启动包初始化时写入；首次包初始化前为底层默认值（不要在初始化前读取做决策）。
        /// 用于 UI 展示「当前是 EditorSimulate / Offline / Host / Web 哪种模式」，影响下载/CDN 行为的判断。
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
        /// 按 location 从默认包加载 UnityEngine.Object 资源。
        /// 返回的 <see cref="IAssetHandle{T}"/> 持有一份引用计数；调用方负责 Dispose（或交给 <see cref="DisposableBag"/> 托管）。
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

        /// <summary>检查默认包中 location 是否能在 manifest 中解析；未初始化或参数为空时返回 false。</summary>
        bool CheckLocationValid(string location);

        /// <summary>检查指定包中 location 是否能在 manifest 中解析；packageName 为空时使用默认包。</summary>
        bool CheckLocationValid(string packageName, string location);

        /// <summary>检查默认包中指定资源是否需要从远端下载；未初始化时返回 false。</summary>
        bool IsNeedDownload(string location);

        /// <summary>检查指定包中资源是否需要从远端下载；packageName 为空时使用默认包。</summary>
        bool IsNeedDownload(string packageName, string location);

        /// <summary>创建默认包的按 tag 统计和下载资源任务。</summary>
        IAssetDownloader CreateTagDownloader(params string[] tags);

        /// <summary>创建指定包的按 tag 统计和下载资源任务；packageName 为空时使用默认包。</summary>
        IAssetDownloader CreateTagDownloader(string packageName, IReadOnlyList<string> tags);
    }
}
