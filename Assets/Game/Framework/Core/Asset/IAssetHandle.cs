using System;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace Game.Framework
{
    /// <summary>
    /// 单个资源的所有权 token。
    ///
    /// 持有它意味着对底层资源持有一份引用计数；<see cref="IDisposable.Dispose"/> 时减一份。
    /// 由 <see cref="IAssetUtility"/> 的加载方法创建，由调用方负责释放。
    /// <see cref="DisposableBag.Load{T}"/> 会把 handle 自动登记到 bag，让业务直接拿 <c>T</c> 用。
    ///
    /// 业务很少直接持有 handle：只有"加载完即用即释"、"用完手动卸载"这种场景才有意义。
    /// 否则统一用 <c>Bag.Load</c>。
    /// 由 <see cref="IAssetUtility"/> 返回的 handle 属于 Unity 主线程；属性与 <see cref="IDisposable.Dispose"/>
    /// 都应从主线程访问。
    /// </summary>
    public interface IAssetHandle<out T> : IDisposable where T : UnityEngine.Object
    {
        /// <summary>加载完成的资源对象；handle 已 Dispose 或加载失败时返回 null。</summary>
        T Asset { get; }

        /// <summary>句柄是否仍有效。Dispose 后变 false。</summary>
        bool IsValid { get; }
    }

    /// <summary>
    /// 场景句柄。
    ///
    /// 场景跟资源不同：需要 Activate、UnSuspend、Unload 等显式操作；卸载是异步的。
    /// 因此场景不能简单等同于 <c>IAssetHandle&lt;T&gt;</c>，而是单独抽象一层。
    ///
    /// <see cref="IDisposable.Dispose"/> 发起一次 fire-and-forget 卸载；需要等待卸载完成请显式 await <see cref="Unload"/>。
    /// 由 <see cref="IAssetUtility"/> 返回的句柄属于 Unity 主线程；同步成员、Dispose 与 Unload 入口都从主线程访问，
    /// Unload 的成功、异常也在主线程交付。
    /// </summary>
    public interface ISceneHandle : IDisposable
    {
        /// <summary>Unity 场景对象。卸载后返回 default(Scene)。</summary>
        Scene Scene { get; }

        /// <summary>句柄是否仍有效。卸载发起后变 false，避免外部继续操作失效场景。</summary>
        bool IsValid { get; }

        /// <summary>把已经加载完成的场景设为 Active Scene。它不解除预加载挂起；挂起场景先调用 <see cref="UnSuspend"/>。</summary>
        bool Activate();

        /// <summary>
        /// 恢复 <c>suspendLoad=true</c> 的场景激活流程。LoadScene task 在内容到达激活门时返回本 handle，
        /// 此方法只负责放行，实际激活仍由 Unity 异步完成；可通过 <see cref="Scene"/> 的 <c>isLoaded</c> 观察。
        /// </summary>
        bool UnSuspend();

        /// <summary>
        /// 异步卸载场景并释放底层句柄。可重复调用，第一次后为 no-op；经 <see cref="IAssetUtility"/>
        /// 返回的句柄会在 Unity 主线程交付成功或异常。
        /// </summary>
        UniTask Unload();
    }
}
