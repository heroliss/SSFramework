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
    /// </summary>
    public interface ISceneHandle : IDisposable
    {
        /// <summary>Unity 场景对象。卸载后返回 default(Scene)。</summary>
        Scene Scene { get; }

        /// <summary>句柄是否仍有效。卸载发起后变 false，避免外部继续操作失效场景。</summary>
        bool IsValid { get; }

        /// <summary>激活通过 <c>suspendLoad=true</c> 加载的场景。无效或不允许激活时返回 false。</summary>
        bool Activate();

        /// <summary>恢复挂起的场景加载流程。供调用方控制何时继续，便于做转场或加载界面。</summary>
        bool UnSuspend();

        /// <summary>异步卸载场景并释放底层句柄。可重复调用，第一次后为 no-op。</summary>
        UniTask Unload();
    }
}
