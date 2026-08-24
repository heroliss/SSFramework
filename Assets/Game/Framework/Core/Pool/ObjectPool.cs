using System;
using System.Collections.Generic;
using Game.Framework.Logging;

namespace Game.Framework.Pool
{
    /// <summary>
    /// <see cref="IObjectPool{T}"/> 的默认实现：栈式空闲列表 + 工厂 + 可选租借/归还钩子。
    /// </summary>
    /// <remarks>
    /// 主线程独占、不加锁。Editor / Development Build 下额外用一个 active 集合检测"重复归还 / 归还外来实例"，
    /// 帮助及早发现别名 bug；Release 下该检测编译消除，零开销。<br/>
    /// <b>池不拥有实例的释放责任：</b><see cref="Clear"/> / <see cref="Trim"/> / 超容量丢弃只是解除引用交 GC，
    /// <b>不会</b>调用实例的 <c>Dispose</c>——池化类型若持有非托管资源，应在 <see cref="IPoolable.OnReturn"/> /
    /// onReturn 钩子里释放，或干脆不要把这类对象交给池管理。
    /// </remarks>
    public sealed class ObjectPool<T> : IObjectPool<T>, IPoolCounters where T : class
    {
        private readonly Stack<T> _inactive = new();
        private readonly Func<T> _factory;
        private readonly Action<T> _onRent;
        private readonly Action<T> _onReturn;
        private readonly int _maxSize; // 0 = 不限容量

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // 已租出实例集合，仅用于诊断重复归还 / 归还外来实例。
        private readonly HashSet<T> _active = new();
#endif

        /// <param name="factory">新建实例的工厂，必填。</param>
        /// <param name="onRent">取出时回调（在 <see cref="IPoolable.OnRent"/> 之前），可空。</param>
        /// <param name="onReturn">归还时回调（在 <see cref="IPoolable.OnReturn"/> 之前），可空。</param>
        /// <param name="maxSize">池容量上限；0 表示不限。超限的归还实例被丢弃交 GC。</param>
        public ObjectPool(Func<T> factory, Action<T> onRent = null, Action<T> onReturn = null, int maxSize = 0)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Unity 类型误用守卫：GameObject / Component 满足 class（甚至 new()）约束，也能进这个 C# 对象池——
            // 但这里不会 Instantiate / SetActive（new GameObject() 造的是空物体、归还也不停用；new 出的 Component 是无效对象），
            // 几乎必然是误用。一次性检查（仅建池时），指路 GameObject 池。
            if (typeof(UnityEngine.Object).IsAssignableFrom(typeof(T)))
                Log.Error(
                    $"{typeof(T).Name} is a UnityEngine.Object — this C# object pool " +
                    "won't Instantiate/activate it. Use the GameObject pool instead (Bag.Spawn / IPoolUtility.Spawn).",
                    category: $"ObjectPool<{typeof(T).Name}>");
#endif
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onRent = onRent;
            _onReturn = onReturn;
            _maxSize = maxSize < 0 ? 0 : maxSize;
        }

        public int CountInactive => _inactive.Count;

        // 借出计数：Rent +1、被接受的 Return -1。Editor/Dev 下 _active 守卫挡掉误用，计数精确；
        // Release 下无守卫，重复归还会漂移——钳到 ≥0，作诊断近似值（见 IObjectPool.CountActive 文档）。
        private int _countActive;

        public int CountActive => _countActive;

        public T Rent()
        {
            var instance = _inactive.Count > 0 ? _inactive.Pop() : _factory();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _active.Add(instance);
#endif
            _countActive++;
            _onRent?.Invoke(instance);
            (instance as IPoolable)?.OnRent();
            return instance;
        }

        public void Return(T instance)
        {
            if (instance == null) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_active.Remove(instance))
            {
                Log.Error(
                    "Returning an instance that wasn't rented from this pool " +
                    "(double-return or foreign instance). Ignored.",
                    category: $"ObjectPool<{typeof(T).Name}>");
                return;
            }
#endif
            if (_countActive > 0) _countActive--;
            _onReturn?.Invoke(instance);
            (instance as IPoolable)?.OnReturn();
            if (_maxSize == 0 || _inactive.Count < _maxSize)
                _inactive.Push(instance);
        }

        public void Prewarm(int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (_maxSize != 0 && _inactive.Count >= _maxSize) break;
                _inactive.Push(_factory());
            }
        }

        public void Clear() => _inactive.Clear();

        public void Trim(int targetCount)
        {
            if (targetCount < 0) targetCount = 0;
            while (_inactive.Count > targetCount)
                _inactive.Pop(); // 丢弃多余空闲实例，交 GC（纯托管对象，无需 Destroy）
        }
    }
}
