using System;

namespace Game.Framework.UI
{
    /// <summary>
    /// 一次全局 Loading 占用的所有权句柄。释放最后一个有效句柄时，框架才会关闭共享 Loading 窗口。
    /// </summary>
    /// <remarks>
    /// <b>陈旧安全：</b>句柄重复释放、所属 UI 已清场/销毁、或 <c>default(LoadingHandle)</c> 都是安全 no-op；
    /// 内部用自增 id 区分不同时期的占用，旧句柄不会误关后来重新显示的 Loading。<br/>
    /// <b>生命周期：</b>优先写 <c>using var loading = await ui.AcquireLoading(...)</c>；也可把句柄登记进
    /// <c>DisposableBag</c>，随 View / Context 生命周期自动释放。
    /// </remarks>
    public readonly struct LoadingHandle : IDisposable
    {
        private readonly ILoadingHandleOwner _owner;
        private readonly int _id;

        /// <summary>
        /// 签发句柄。自定义 <see cref="IUIUtility"/> 实现也可通过自己的 <see cref="ILoadingHandleOwner"/>
        /// 签发同语义句柄；陈旧 id 必须安全 no-op。
        /// </summary>
        public LoadingHandle(ILoadingHandleOwner owner, int id)
        {
            _owner = owner;
            _id = id;
        }

        /// <summary>该占用是否仍然有效；default、已释放或已被强制清场的句柄返回 false。</summary>
        public bool IsActive => _owner != null && _owner.IsLoadingActive(_id);

        /// <summary>释放本次占用。重复调用或释放陈旧句柄是安全 no-op。</summary>
        public void Dispose() => _owner?.ReleaseLoading(_id);
    }

    /// <summary>
    /// <see cref="LoadingHandle"/> 的签发方契约。实现必须让重复/陈旧 id 的查询与释放安全 no-op，
    /// 并保证仍有其它有效占用时不会关闭共享 Loading。
    /// </summary>
    public interface ILoadingHandleOwner
    {
        /// <summary>id 对应的 Loading 占用是否仍有效。</summary>
        bool IsLoadingActive(int id);

        /// <summary>释放 id 对应的占用；重复或陈旧 id 安全 no-op。</summary>
        void ReleaseLoading(int id);
    }
}
