using System;
using System.Collections.Generic;

namespace Game.Framework.Pool
{
    /// <summary>
    /// <see cref="IPoolUtility"/> 的默认纯 C# 实现：按类型缓存 <see cref="ObjectPool{T}"/>。
    /// </summary>
    /// <remarks>
    /// 用 <c>builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility))</c> 注册到 Context。
    /// 无状态业务数据、不依赖 Context，可被父子 Context 共享（子级解析未命中会回退父级）。主线程独占。
    /// </remarks>
    public sealed class PoolUtility : IPoolUtility
    {
        // key = 池化类型 T；value = IObjectPool<T>（按 T 装箱存储，取出时还原）
        private readonly Dictionary<Type, object> _pools = new();

        public IObjectPool<T> GetPool<T>() where T : class, new()
            => GetPool(static () => new T());

        public IObjectPool<T> GetPool<T>(Func<T> factory, Action<T> onRent = null, Action<T> onReturn = null, int maxSize = 0)
            where T : class
        {
            if (_pools.TryGetValue(typeof(T), out var existing))
                return (IObjectPool<T>)existing;

            var pool = new ObjectPool<T>(factory, onRent, onReturn, maxSize);
            _pools[typeof(T)] = pool;
            return pool;
        }

        public T Rent<T>() where T : class, new() => GetPool<T>().Rent();

        public void Return<T>(T instance) where T : class, new() => GetPool<T>().Return(instance);
    }
}
