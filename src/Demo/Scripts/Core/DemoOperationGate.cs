using System;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// 模块级共享操作闸门。它比单个按钮的防连点活得更久，用来保护多个按钮共同读写的资源，
    /// 并在 UIDocument 重建后继续挡住上一轮仍在取消或收尾的操作。
    /// </summary>
    /// <remarks>
    /// 成功进入后必须释放返回的 <see cref="Lease"/>。租约携带本次进入的身份，旧租约即使被重复释放，
    /// 也不能误放行后来取得闸门的新操作；这比裸 <c>bool</c> 的“最后统一写回 false”更能抵抗异步迟到续体。
    /// 本类型按 Unity 主线程使用，不提供跨线程同步语义。
    /// </remarks>
    internal sealed class DemoOperationGate
    {
        private object _owner;

        /// <summary>当前是否已有操作持有闸门。</summary>
        internal bool IsEntered => _owner != null;

        /// <summary>尝试取得闸门；失败时返回默认租约且不改变当前 owner。</summary>
        internal bool TryEnter(out Lease lease)
        {
            if (_owner != null)
            {
                lease = default;
                return false;
            }

            var owner = new object();
            _owner = owner;
            lease = new Lease(this, owner);
            return true;
        }

        private void Release(object owner)
        {
            if (ReferenceEquals(_owner, owner))
                _owner = null;
        }

        /// <summary>一次成功进入的所有权凭证；可复制、可重复释放，但只会释放自己取得的那一轮。</summary>
        internal readonly struct Lease : IDisposable
        {
            private readonly DemoOperationGate _gate;
            private readonly object _owner;

            internal Lease(DemoOperationGate gate, object owner)
            {
                _gate = gate;
                _owner = owner;
            }

            public void Dispose() => _gate?.Release(_owner);
        }
    }
}
