using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Framework.Pool
{
    /// <summary>
    /// <see cref="IPoolUtility"/> 的默认实现：按类型缓存 <see cref="ObjectPool{T}"/>（纯 C# 对象），按 prefab 缓存 <see cref="GameObjectPool"/>。
    /// </summary>
    /// <remarks>
    /// <b>注册（按生命周期选）：</b>纯 C# 跟随 Context 用 <c>builder.RegisterOwned(new PoolUtility(), typeof(IPoolUtility))</c>——
    /// 随 <c>GameContext.Dispose</c> 自动清池（销毁停放根 + 空闲实例），可安全 per-Context 注册；不关心释放（全局唯一、随进程退出）
    /// 用 <c>RegisterValue</c>（不被容器拥有、不会被 Dispose）；需 Inspector 配参数 / 跟随 GameObject 生命周期用 <see cref="MonoPoolUtility"/>。三者复用本类同一套逻辑。<br/>
    /// <b>不依赖 Context</b>（无 IGameContext/IAssetUtility 引用），可被父子 Context 共享（子级解析未命中会回退父级）。主线程独占。<br/>
    /// GameObject 池首次使用时惰性创建一个停用的 DontDestroyOnLoad parking 节点存放空闲实例——这让本工具触及 Unity 场景，
    /// 但仍不依赖框架 Context；位置加载交由调用方先 <c>Bag.Load&lt;GameObject&gt;(location)</c> 再建池，刻意不把 IAssetUtility 拉进来。
    /// parking 节点（总根及各 prefab 子节点）若被外部销毁，会在下次归还 / 预热时自愈重建（见 <see cref="EnsureParkingFor"/>），归还实例不会散落到场景根。<br/>
    /// <b>Dispose 后不可再用：</b><see cref="Dispose"/> 后停放根被销毁，任何继续的 Spawn/Rent/Despawn 都<b>不</b>会复活停放根
    /// （Dispose 后归还的实例直接 Destroy，避免泄漏一个永不回收的 DontDestroyOnLoad 节点）；Editor/Dev 构建下对 Dispose 后的调用会 LogError 帮你抓到过期引用。
    /// </remarks>
    public sealed class PoolUtility : IPoolUtility, IDisposable
    {
        // key = 池化类型 T；value = IObjectPool<T>（按 T 装箱存储，取出时还原）
        private readonly Dictionary<Type, object> _pools = new();

        // key = 源 prefab；value = 其 GameObject 池。仅在首次用到 GameObject 池时分配。
        private Dictionary<GameObject, IGameObjectPool> _goPools;

        // key = 源 prefab；value = 其空闲实例的停放子节点（挂在总根下）。按需解析、Unity fake-null 时自愈重建。
        private Dictionary<GameObject, Transform> _parkings;

        // 所有 GameObject 池空闲实例的统一挂载父节点（停用 + DontDestroyOnLoad）。
        private Transform _parkingRoot;

        private bool _disposed;

        public IObjectPool<T> GetPool<T>() where T : class, new()
            => GetPool(static () => new T());

        public IObjectPool<T> GetPool<T>(Func<T> factory, Action<T> onRent = null, Action<T> onReturn = null, int maxSize = 0)
            where T : class
        {
            WarnIfDisposed(nameof(GetPool));
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
            WarnIfDisposed(nameof(GetGameObjectPool));
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
            WarnIfDisposed(nameof(Despawn));
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

        /// <summary>
        /// 诊断用：当前各池概要——GameObject 池给 <c>[GO] prefab名 空闲=N</c>，C# 对象池给 <c>[C#] 类型名</c>。
        /// 仅供 Inspector 只读展示（<see cref="MonoPoolUtility"/>），不参与池逻辑；无池时返回空列表。
        /// </summary>
        public IReadOnlyList<string> GetPoolDiagnostics()
        {
            var list = new List<string>();
            if (_goPools != null)
                foreach (var kv in _goPools)
                    list.Add($"[GO] {(kv.Key != null ? kv.Key.name : "?")} 空闲={kv.Value.CountInactive}");
            foreach (var t in _pools.Keys)
                list.Add($"[C#] {t.Name}");
            return list;
        }

        /// <summary>
        /// 释放池工具：销毁 GameObject 池的停放总根（连带所有子停放节点与池中空闲实例）、清空所有池缓存。
        /// 已 Spawn 出去的活动实例挂在调用方节点下、不在停放区，<b>不</b>受影响（归调用方生命周期）。幂等。
        /// </summary>
        /// <remarks>
        /// 由 <c>ContainerBuilder.RegisterOwned</c> + <c>GameContext.Dispose</c>（纯 C# 路径），
        /// 或 <c>MonoPoolUtility.OnDestroy</c>（Mono 路径）调用，让池生命周期跟随其归属 Context / GameObject。
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // GameObject 池：销毁停放总根，连带子停放节点 + 池中空闲实例一起回收。
            if (_parkingRoot != null)
                UnityEngine.Object.Destroy(_parkingRoot.gameObject);
            _parkingRoot = null;
            _parkings?.Clear();
            _goPools?.Clear();

            // C# 对象池：纯托管对象，清引用交 GC。
            _pools.Clear();
        }

        // 惰性创建统一的 parking 根：停用（空闲实例不渲染/不 Update）+ DontDestroyOnLoad（跨场景存活，随工具生命周期）。
        // _parkingRoot == null 同时识别"从未创建"和 Unity fake-null（被外部销毁）——两种都重建，所以总根能自愈。
        // 但 Dispose 后返回 null（不复活）：否则 Dispose 后的归还会建出一个永不被回收的 DontDestroyOnLoad 节点。
        private Transform EnsureParkingRoot()
        {
            if (_disposed) return null;
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
            if (root == null) return null; // 已 Dispose：不复活停放点；归还方（GameObjectPool）拿到 null 会直接销毁实例
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

        // Dispose 后误用诊断：池已随其归属 Context/GameObject 释放，继续用多半是持有了过期引用。
        // 仅 Editor/Dev 生效（对齐本层其它防护风格）；不阻断调用——归还路径已能安全地不复活停放根。
        private void WarnIfDisposed(string op)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_disposed)
                Debug.LogError($"[PoolUtility] '{op}' called after Dispose——池已释放，检查是否持有了过期的 IPoolUtility 引用。");
#endif
        }
    }
}
