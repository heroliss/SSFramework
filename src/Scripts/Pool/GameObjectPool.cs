using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Framework.Pool
{
    /// <summary>
    /// <see cref="IGameObjectPool"/> 的默认实现：栈式空闲列表 + 统一的 parking 节点 + 复用时重置 local transform。
    /// </summary>
    /// <remarks>
    /// 主线程独占、不加锁。每个实例挂一个 <see cref="PooledObject"/> 标记（记录来源池 + 缓存 <see cref="IPoolable"/> 组件）。<br/>
    /// 空闲实例统一停用并挂在 <paramref name="parkingRoot"/>（由 <see cref="PoolUtility"/> 创建的 DontDestroyOnLoad 停用节点）下，
    /// 既隔离出场景视图、又因 parent 停用而不参与渲染/Update。<br/>
    /// <b>归还防护：</b><see cref="Despawn"/> 对"非本池实例 / 重复归还"的短路在**所有构建**生效——防止同一 live 实例被两次入栈、
    /// 进而被发给两个调用方（Release 下静默的别名 bug）；仅诊断日志放在 Editor / Development Build。
    /// </remarks>
    public sealed class GameObjectPool : IGameObjectPool
    {
        private readonly GameObject _prefab;
        private readonly Transform _parkingRoot;
        private readonly int _maxSize; // 0 = 不限容量
        private readonly Stack<GameObject> _inactive = new();

        // prefab 的本地 transform 默认值，复用实例时重置，让池化实例与新 Instantiate 行为一致。
        private readonly Vector3 _localPosition;
        private readonly Quaternion _localRotation;
        private readonly Vector3 _localScale;

        /// <param name="prefab">源 prefab，必填。</param>
        /// <param name="parkingRoot">空闲实例的挂载节点（应为停用的 DontDestroyOnLoad 节点），必填。</param>
        /// <param name="maxSize">池容量上限；0 表示不限。超限的归还实例被 Destroy。</param>
        public GameObjectPool(GameObject prefab, Transform parkingRoot, int maxSize = 0)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            if (parkingRoot == null) throw new ArgumentNullException(nameof(parkingRoot));
            _prefab = prefab;
            _parkingRoot = parkingRoot;
            _maxSize = maxSize < 0 ? 0 : maxSize;

            var t = prefab.transform;
            _localPosition = t.localPosition;
            _localRotation = t.localRotation;
            _localScale = t.localScale;
        }

        public GameObject Prefab => _prefab;
        public int CountInactive => _inactive.Count;

        public GameObject Spawn(Transform parent = null, bool worldPositionStays = false)
        {
            var go = TakeOrCreate(out var marker);
            var t = go.transform;
            t.SetParent(parent, worldPositionStays);
            // worldPositionStays:true 时刻意不动 transform（含 scale）——让 SetParent 保留世界位姿是该参数的全部意义；
            // 否则把 local 位姿整体重置回 prefab 默认值，使复用实例与新 Instantiate 行为一致。
            if (!worldPositionStays)
            {
                t.localPosition = _localPosition;
                t.localRotation = _localRotation;
                t.localScale = _localScale;
            }
            go.SetActive(true);
            InvokeRent(marker);
            return go;
        }

        public GameObject Spawn(Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var go = TakeOrCreate(out var marker);
            var t = go.transform;
            t.SetParent(parent, false);
            t.localScale = _localScale;
            t.SetPositionAndRotation(position, rotation);
            go.SetActive(true);
            InvokeRent(marker);
            return go;
        }

        public void Despawn(GameObject instance)
        {
            if (instance == null) return;

            var marker = instance.GetComponent<PooledObject>();
            if (marker == null)
            {
                // 无标记 = 非池化对象，无法安全入池：Release 也忽略（避免污染池），Editor/Dev 额外报错。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogError(
                    $"[GameObjectPool({_prefab.name})] Despawning a non-pooled GameObject '{instance.name}'. Ignored.");
#endif
                return;
            }
            // 以下两个短路在所有构建生效：把"非本池 / 已归还"的 live 实例挡在栈外，否则它会被二次入池、
            // 被下一次 Spawn 发给第二个调用方（同一对象别名）——Release 下静默且难查。诊断日志仅 Editor/Dev。
            if (!ReferenceEquals(marker.OwningPool, this))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogError(
                    $"[GameObjectPool({_prefab.name})] Despawning '{instance.name}' that belongs to a different pool. Ignored.");
#endif
                return;
            }
            if (!marker.IsSpawned)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogError(
                    $"[GameObjectPool({_prefab.name})] Double-despawn of '{instance.name}'. Ignored.");
#endif
                return;
            }

            InvokeReturn(marker);
            marker.IsSpawned = false;

            // 超过容量上限：直接 Destroy，不入池。先 SetActive(false) 与正常归还路径一致，
            // 避免延迟销毁前实例还活跃渲染/Update 一帧（OnReturn 已清理状态，留着会显示脏画面）。
            if (_maxSize != 0 && _inactive.Count >= _maxSize)
            {
                instance.SetActive(false);
                UnityEngine.Object.Destroy(instance);
                return;
            }

            instance.SetActive(false);
            instance.transform.SetParent(_parkingRoot, false);
            _inactive.Push(instance);
        }

        public async UniTask Prewarm(int count, CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
            {
                if (_maxSize != 0 && _inactive.Count >= _maxSize) break;
                var go = CreateNew(out _);
                go.SetActive(false);
                go.transform.SetParent(_parkingRoot, false);
                _inactive.Push(go);
                // 每帧一个，把实例化开销摊到多帧（通常在加载界面期间预热）。取消则中断，已建实例留在池中。
                await UniTask.Yield(ct);
            }
        }

        public void Clear()
        {
            while (_inactive.Count > 0)
            {
                var go = _inactive.Pop();
                if (go != null) UnityEngine.Object.Destroy(go);
            }
        }

        // 取一个可用实例：跳过已被外部 Destroy 的空槽，池空则新建。一并返回标记，省掉调用处再次 GetComponent。
        private GameObject TakeOrCreate(out PooledObject marker)
        {
            while (_inactive.Count > 0)
            {
                var pooled = _inactive.Pop();
                if (pooled != null) // Unity fake-null：跳过被外部 Destroy 的空槽
                {
                    marker = pooled.GetComponent<PooledObject>();
                    marker.IsSpawned = true;
                    return pooled;
                }
            }
            var created = CreateNew(out marker);
            marker.IsSpawned = true;
            return created;
        }

        // 实例化一个新对象并挂标记（缓存来源池 + IPoolable 组件）。
        private GameObject CreateNew(out PooledObject marker)
        {
            var go = UnityEngine.Object.Instantiate(_prefab);
            marker = go.GetComponent<PooledObject>();
            if (marker == null) marker = go.AddComponent<PooledObject>();
            marker.OwningPool = this;
            // 含未激活子节点：池化对象 Spawn 前处于停用状态，必须传 true 才能扫到。
            marker.Poolables = go.GetComponentsInChildren<IPoolable>(true);
            return go;
        }

        // 注：Poolables 假定在 GameObject 整个生命周期内有效——不要单独 Destroy 池化实例上的某个 IPoolable 组件
        // （那会在数组里留下 fake-null，下面的 ?. 不识别 Unity fake-null，无法跳过）。
        private static void InvokeRent(PooledObject marker)
        {
            var poolables = marker.Poolables;
            for (var i = 0; i < poolables.Length; i++) poolables[i]?.OnRent();
        }

        private static void InvokeReturn(PooledObject marker)
        {
            var poolables = marker.Poolables;
            for (var i = 0; i < poolables.Length; i++) poolables[i]?.OnReturn();
        }
    }
}
