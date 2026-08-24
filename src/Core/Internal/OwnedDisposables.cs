using System;
using System.Collections.Generic;
using Game.Framework.Logging;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 容器构建事务的资源所有权模块：按引用去重登记，在回滚或 Context 销毁时逆序、尽力释放。
    /// Builder 与 Container 共享同一实例，<c>Build</c> 成功后只改变谁负责触发释放，不复制所有权。
    /// </summary>
    internal sealed class OwnedDisposables
    {
        private readonly List<IDisposable> _items = new();
        private bool _disposed;

        /// <summary>按对象身份登记资源；同一实例经多个契约注册时仍只释放一次。</summary>
        internal void Add(IDisposable item)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OwnedDisposables),
                    "Cannot add owned resources after the ownership scope has been disposed.");
            if (item == null) throw new ArgumentNullException(nameof(item));

            for (int i = 0; i < _items.Count; i++)
                if (ReferenceEquals(_items[i], item)) return;
            _items.Add(item);
        }

        /// <summary>逆序释放全部资源；单个 Dispose 失败只记录，不阻断其余清理。幂等。</summary>
        internal void Dispose(string category)
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                try { _items[i].Dispose(); }
                catch (Exception e)
                {
                    Log.Error(
                        "An owned service threw during disposal; remaining services will still be released.",
                        e,
                        category);
                }
            }
            _items.Clear();
        }
    }
}
