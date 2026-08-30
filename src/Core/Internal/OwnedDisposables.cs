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
        private List<WeakReference<IDisposable>> _released;
        private bool _disposed;

        /// <summary>按对象身份登记资源；同一实例经多个契约注册时仍只释放一次。</summary>
        internal void Add(IDisposable item)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OwnedDisposables),
                    "所有权作用域已释放，不能再添加托管资源。");
            if (item == null) throw new ArgumentNullException(nameof(item));

            if (Contains(item)) return;
            _items.Add(item);
        }

        /// <summary>按对象身份判断资源是否已经由本 registry 持有，不调用用户自定义 Equals。</summary>
        internal bool Contains(IDisposable item)
        {
            if (item == null) return false;
            for (int i = 0; i < _items.Count; i++)
                if (ReferenceEquals(_items[i], item)) return true;
            return false;
        }

        /// <summary>
        /// 判断资源当前由 registry 持有，或已在本 registry 的释放事务中被尝试释放过。
        /// 历史只保留弱引用，供 Factory 失败回滚避免重复 Dispose，不延长已关闭服务的生命周期。
        /// </summary>
        internal bool ContainsCurrentOrReleased(IDisposable item)
        {
            if (Contains(item)) return true;
            if (item == null || _released == null) return false;

            for (int i = _released.Count - 1; i >= 0; i--)
            {
                if (!_released[i].TryGetTarget(out var released))
                {
                    _released.RemoveAt(i);
                    continue;
                }
                if (ReferenceEquals(released, item)) return true;
            }
            return false;
        }

        /// <summary>逆序释放全部资源；单个 Dispose 失败只记录，不阻断其余清理。幂等。</summary>
        internal void Dispose(string category)
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                (_released ??= new List<WeakReference<IDisposable>>(_items.Count))
                    .Add(new WeakReference<IDisposable>(item));
                try { item.Dispose(); }
                catch (Exception e)
                {
                    Log.Error(
                        "托管服务在释放期间抛出异常；其余服务仍会继续释放。",
                        e,
                        category);
                }
            }
            _items.Clear();
        }
    }
}
