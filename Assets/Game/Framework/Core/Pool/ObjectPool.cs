using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using Game.Framework.Logging;

namespace Game.Framework.Pool
{
    /// <summary>
    /// <see cref="IObjectPool{T}"/> 的默认实现：栈式空闲列表 + 工厂 + 可选租借/归还钩子。
    /// </summary>
    /// <remarks>
    /// 主线程独占、不加锁。实例状态表在所有构建中按<b>引用身份</b>跟踪租借所有权，拒绝重复归还 / 外来实例；
    /// 这是防止同一引用二次入栈并被交给两个 owner 的正确性约束，不是可从 Release 删除的诊断。<br/>
    /// Rent / Return 钩子由 Renting / Returning 事务态隔离：同步重入不能提前归还同一对象；钩子失败时先补偿并丢弃脏实例，
    /// 再以原始堆栈重抛首异常，不会把半初始化 / 未清干净的对象重新发布。<br/>
    /// <b>池不拥有实例的释放责任：</b><see cref="Clear"/> / <see cref="Trim"/> / 超容量丢弃只是解除引用交 GC，
    /// <b>不会</b>调用实例的 <c>Dispose</c>——池化类型若持有非托管资源，应在 <see cref="IPoolable.OnReturn"/> /
    /// onReturn 钩子里释放，或干脆不要把这类对象交给池管理。
    /// </remarks>
    public sealed class ObjectPool<T> : IObjectPool<T>, IPoolCounters, IPoolLifetime, IPoolReturnRoute where T : class
    {
        private readonly Stack<T> _inactive = new();
        private readonly Func<T> _factory;
        private readonly Action<T> _onRent;
        private readonly Action<T> _onReturn;
        private readonly int _maxSize; // 0 = 不限容量
        private readonly IPoolLeaseRegistry _leaseRegistry;

        private enum InstanceState
        {
            Inactive,
            Renting,
            Active,
            Returning,
        }

        // 所有由池管理的引用与其事务状态。必须按引用身份比较：值相等的两个对象仍是两个独立 lease。
        // Renting / Returning 让钩子同步重入也只能被拒绝，不能把同一引用提前压栈或重复发布。
        private readonly Dictionary<T, InstanceState> _states = new(ReferenceComparer.Instance);
        private int _countActive;
        private bool _terminated;

        /// <param name="factory">新建实例的工厂，必填。</param>
        /// <param name="onRent">取出时回调（在 <see cref="IPoolable.OnRent"/> 之前），可空。</param>
        /// <param name="onReturn">归还时回调（在 <see cref="IPoolable.OnReturn"/> 之前），可空。</param>
        /// <param name="maxSize">池容量上限；0 表示不限。超限的归还实例被丢弃交 GC。</param>
        public ObjectPool(Func<T> factory, Action<T> onRent = null, Action<T> onReturn = null, int maxSize = 0)
            : this(factory, onRent, onReturn, maxSize, null)
        {
        }

        internal ObjectPool(
            Func<T> factory,
            Action<T> onRent,
            Action<T> onReturn,
            int maxSize,
            IPoolLeaseRegistry leaseRegistry)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Unity 类型误用守卫：GameObject / Component 满足 class（甚至 new()）约束，也能进这个 C# 对象池——
            // 但这里不会 Instantiate / SetActive（new GameObject() 造的是空物体、归还也不停用；new 出的 Component 是无效对象），
            // 几乎必然是误用。一次性检查（仅建池时），指路 GameObject 池。
            if (typeof(UnityEngine.Object).IsAssignableFrom(typeof(T)))
                Log.Error(
                    $"类型 '{typeof(T).Name}' 是 UnityEngine.Object，C# 对象池不会实例化或激活它。" +
                    "请改用 GameObject 池（Bag.Spawn / IPoolUtility.Spawn）。",
                    category: $"ObjectPool<{typeof(T).Name}>");
#endif
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onRent = onRent;
            _onReturn = onReturn;
            _maxSize = maxSize < 0 ? 0 : maxSize;
            _leaseRegistry = leaseRegistry;
        }

        public int CountInactive => _inactive.Count;

        public int CountActive => _countActive;

        public T Rent()
        {
            ThrowIfTerminated();
            T instance;
            if (_inactive.Count > 0)
            {
                instance = _inactive.Pop();
                if (!_states.TryGetValue(instance, out InstanceState state) || state != InstanceState.Inactive)
                    throw new InvalidOperationException($"ObjectPool<{typeof(T).Name}> 的空闲栈与所有权状态不一致。");
                _states[instance] = InstanceState.Renting;
            }
            else
            {
                instance = CreateInstance();
                // factory 是可替换同步扩展点，可能重入所属 Utility.Dispose。
                ThrowIfTerminated();
                if (_states.ContainsKey(instance))
                    throw new InvalidOperationException(
                        $"ObjectPool<{typeof(T).Name}> 的 factory 返回了已经由此池管理的同一引用。");
                _states.Add(instance, InstanceState.Renting);
            }

            bool leaseRouteReserved = false;
            if (_leaseRegistry != null)
            {
                try
                {
                    // 先按引用身份预留来源，再执行用户钩子：跨池 singleton factory 会在触碰另一个 owner 的实例前失败。
                    _leaseRegistry.RegisterLease(instance, this);
                    leaseRouteReserved = true;
                }
                catch (Exception e)
                {
                    _states.Remove(instance);
                    Rethrow(e);
                    return null;
                }
            }

            Exception rentFailure = null;
            try
            {
                _onRent?.Invoke(instance);
                (instance as IPoolable)?.OnRent();
                // OnRent 同样可能重入 Dispose；已终止的池不能在回调返回后再发布一个新 lease。
                ThrowIfTerminated();
            }
            catch (Exception e)
            {
                rentFailure = e;
            }

            if (rentFailure != null)
            {
                Exception rollbackFailure = InvokeReturnHooksBestEffort(instance);
                if (rollbackFailure != null)
                    Log.Error(
                        "租借钩子失败后的补偿清理也抛出异常；仍保留并重抛最初的租借异常。",
                        rollbackFailure,
                        $"ObjectPool<{typeof(T).Name}>");
                if (leaseRouteReserved) _leaseRegistry.UnregisterLease(instance, this);
                _states.Remove(instance); // 失败激活过的实例不再复用
                Rethrow(rentFailure);
                return null; // ExceptionDispatchInfo.Throw 后不可达，供编译器完成返回分析。
            }

            _states[instance] = InstanceState.Active;
            _countActive++;
            return instance;
        }

        public void Return(T instance)
        {
            if (instance == null) return;
            if (!_states.TryGetValue(instance, out InstanceState state) || state != InstanceState.Active)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Log.Error(
                    state == InstanceState.Renting || state == InstanceState.Returning
                        ? "对象池钩子中重入归还同一实例，已拒绝以保护租借事务。"
                        : "正在归还未从此池租出的实例（可能是重复归还或外来实例），已忽略。",
                    category: $"ObjectPool<{typeof(T).Name}>");
#endif
                return;
            }

            // 先关闭调用方所有权，再执行用户代码：OnReturn 重入 Return 时会看到 Returning 并被拒绝，不会递归/二次入池。
            _states[instance] = InstanceState.Returning;
            _countActive--;
            _leaseRegistry?.UnregisterLease(instance, this);
            Exception hookFailure = InvokeReturnHooksBestEffort(instance);
            bool canReuse = hookFailure == null && !_terminated &&
                            (_maxSize == 0 || _inactive.Count < _maxSize);
            if (canReuse)
            {
                _states[instance] = InstanceState.Inactive;
                _inactive.Push(instance);
            }
            else
            {
                _states.Remove(instance);
            }

            if (hookFailure != null) Rethrow(hookFailure);
        }

        public void Prewarm(int count)
        {
            ThrowIfTerminated();
            for (var i = 0; i < count; i++)
            {
                if (_maxSize != 0 && _inactive.Count >= _maxSize) break;
                T instance = CreateInstance();
                ThrowIfTerminated();
                if (_states.ContainsKey(instance))
                    throw new InvalidOperationException(
                        $"ObjectPool<{typeof(T).Name}> 的 factory 重复返回了已经由此池管理的同一引用。");
                // factory 是同步扩展点，可能重入 Prewarm 并先填满容量；最终入栈前必须重新验证提交条件。
                if (_maxSize != 0 && _inactive.Count >= _maxSize) break;
                _states.Add(instance, InstanceState.Inactive);
                _inactive.Push(instance);
            }
        }

        public void Clear()
        {
            ThrowIfTerminated();
            ClearInactive();
        }

        public void Trim(int targetCount)
        {
            ThrowIfTerminated();
            if (targetCount < 0) targetCount = 0;
            while (_inactive.Count > targetCount)
                _states.Remove(_inactive.Pop()); // 丢弃多余空闲实例，交 GC（纯托管对象，无需 Destroy）
        }

        void IPoolLifetime.Terminate()
        {
            if (_terminated) return;
            _terminated = true;
            ClearInactive();
            // Active / 同步事务状态保留：既有 lease 仍可 Return；Renting/Returning 由原调用栈负责补偿并移除。
        }

        void IPoolReturnRoute.ReturnObject(object instance)
        {
            if (instance is not T typed)
                throw new InvalidOperationException(
                    $"ObjectPool<{typeof(T).Name}> 收到不兼容的类型擦除归还实例 '{instance?.GetType().Name ?? "null"}'。");
            Return(typed);
        }

        private T CreateInstance()
        {
            T instance = _factory();
            if (instance == null)
                throw new InvalidOperationException($"ObjectPool<{typeof(T).Name}> 的 factory 返回了 null。");
            return instance;
        }

        private void ClearInactive()
        {
            while (_inactive.Count > 0)
                _states.Remove(_inactive.Pop());
        }

        // 清理钩子属于同一个 best-effort 事务：第一个失败不能跳过第二个；调用方最终收到首异常，后续异常进日志接缝。
        private Exception InvokeReturnHooksBestEffort(T instance)
        {
            Exception first = null;
            try
            {
                _onReturn?.Invoke(instance);
            }
            catch (Exception e)
            {
                first = e;
            }

            try
            {
                (instance as IPoolable)?.OnReturn();
            }
            catch (Exception e)
            {
                if (first == null)
                    first = e;
                else
                    Log.Error(
                        "对象池后续 OnReturn 清理钩子也抛出异常；最终仍重抛首个清理异常。",
                        e,
                        $"ObjectPool<{typeof(T).Name}>");
            }

            return first;
        }

        private static void Rethrow(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        private void ThrowIfTerminated()
        {
            if (_terminated)
                throw new ObjectDisposedException(
                    $"ObjectPool<{typeof(T).Name}>",
                    "所属 PoolUtility 已释放；旧 lease 只允许 Return，不能继续 Rent/Prewarm/维护池。");
        }

        private sealed class ReferenceComparer : IEqualityComparer<T>
        {
            internal static readonly ReferenceComparer Instance = new();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
