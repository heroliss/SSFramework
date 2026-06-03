using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Framework.Pool
{
    /// <summary>
    /// <see cref="IPoolUtility"/> 的默认实现：按类型缓存 <see cref="ObjectPool{T}"/>（纯 C# 对象），按 prefab 缓存 <see cref="GameObjectPool"/>。
    /// </summary>
    /// <remarks>
    /// 用 <c>builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility))</c> 注册到 Context。
    /// <b>不依赖 Context</b>（无 IGameContext/IAssetUtility 引用），可被父子 Context 共享（子级解析未命中会回退父级）。主线程独占。<br/>
    /// GameObject 池首次使用时惰性创建一个停用的 DontDestroyOnLoad parking 节点存放空闲实例——这让本工具触及 Unity 场景，
    /// 但仍不依赖框架 Context；位置加载交由调用方先 <c>Bag.Load&lt;GameObject&gt;(location)</c> 再建池，刻意不把 IAssetUtility 拉进来。
    /// parking 节点（总根及各 prefab 子节点）若被外部销毁，会在下次归还 / 预热时自愈重建（见 <see cref="EnsureParkingFor"/>），归还实例不会散落到场景根。<br/>
    /// <b>生命周期约束：</b>应在**根/全局 Context** 注册一次、随 app 存活（见 <c>Assets/Game/AGENTS.md</c> §23）。
    /// 其 DontDestroyOnLoad parking 根与池中实例按设计长存——容器不会 Dispose 注册值（见 <c>GameContext.Dispose</c>），
    /// 所以**不要为每个会被销毁重建的子 Context 各注册一个 PoolUtility**，否则每次重建都会泄漏一个 parking 根及其池化实例。
    /// </remarks>
    public sealed class PoolUtility : IPoolUtility
    {
        // key = 池化类型 T；value = IObjectPool<T>（按 T 装箱存储，取出时还原）
        private readonly Dictionary<Type, object> _pools = new();

        // key = 源 prefab；value = 其 GameObject 池。仅在首次用到 GameObject 池时分配。
        private Dictionary<GameObject, IGameObjectPool> _goPools;

        // key = 源 prefab；value = 其空闲实例的停放子节点（挂在总根下）。按需解析、Unity fake-null 时自愈重建。
        private Dictionary<GameObject, Transform> _parkings;

        // 所有 GameObject 池空闲实例的统一挂载父节点（停用 + DontDestroyOnLoad）。
        private Transform _parkingRoot;

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

        // ── GameObject / Prefab 池 ──────────────────────────────────────────

        public IGameObjectPool GetGameObjectPool(GameObject prefab, int maxSize = 0)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            _goPools ??= new Dictionary<GameObject, IGameObjectPool>();
            if (_goPools.TryGetValue(prefab, out var existing))
                return existing;

            // 停放点按需解析（不在此固定一个 Transform）：每次入池时经 EnsureParkingFor 取，
            // 内部停放节点若被外部销毁，下次归还会自动重建，归还实例不会散落到场景根。
            var pool = new GameObjectPool(prefab, () => EnsureParkingFor(prefab), maxSize);
            _goPools[prefab] = pool;
            return pool;
        }

        public GameObject Spawn(GameObject prefab, Transform parent = null)
            => GetGameObjectPool(prefab).Spawn(parent);

        public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
            => GetGameObjectPool(prefab).Spawn(position, rotation, parent);

        public void Despawn(GameObject instance)
        {
            if (instance == null) return;
            var marker = instance.GetComponent<PooledObject>();
            if (marker == null || marker.OwningPool == null)
            {
                // 非池化对象：无法路由归还。Editor/Dev 报错，Release 静默忽略。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogError(
                    $"[PoolUtility] Despawn called on a GameObject '{instance.name}' that wasn't spawned from any pool. Ignored.");
#endif
                return;
            }
            marker.OwningPool.Despawn(instance);
        }

        // 惰性创建统一的 parking 根：停用（空闲实例不渲染/不 Update）+ DontDestroyOnLoad（跨场景存活，随工具生命周期）。
        // _parkingRoot == null 同时识别"从未创建"和 Unity fake-null（被外部销毁）——两种情况都重建，所以总根能自愈。
        private Transform EnsureParkingRoot()
        {
            if (_parkingRoot == null)
            {
                var root = new GameObject("[Game.Framework PooledObjects]");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.SetActive(false);
                _parkingRoot = root.transform;
            }
            return _parkingRoot;
        }

        // 解析（必要时重建）某 prefab 的停放子节点。总根与子节点都做 Unity fake-null 检测——
        // 任一被外部销毁（如手动删 [Game.Framework PooledObjects] 节点）都会重建，
        // 保证归还实例始终能停回内部容器，而不是被 SetParent(已销毁的 Transform) 当作 null 扔到场景根。
        private Transform EnsureParkingFor(GameObject prefab)
        {
            var root = EnsureParkingRoot();
            _parkings ??= new Dictionary<GameObject, Transform>();
            // 每个 prefab 一个命名子节点，便于在 Hierarchy 观察各池的空闲实例；fake-null（含随总根一起被销毁）时重建。
            if (!_parkings.TryGetValue(prefab, out var parking) || parking == null)
            {
                parking = new GameObject(prefab.name).transform;
                parking.SetParent(root, false);
                _parkings[prefab] = parking;
            }
            return parking;
        }
    }
}
