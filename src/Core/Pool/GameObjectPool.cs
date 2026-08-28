using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Pool
{
    /// <summary>
    /// <see cref="IGameObjectPool"/> 的默认实现：栈式空闲列表 + 统一的 parking 节点 + 复用时重置 local transform。
    /// </summary>
    /// <remarks>
    /// 主线程独占、不加锁。每个实例挂一个 <see cref="PooledObject"/> 标记（记录来源池 + 缓存 <see cref="IPoolable"/> 组件）。<br/>
    /// 空闲实例统一停用并挂在 <see cref="PoolUtility"/> 提供的 DontDestroyOnLoad 停用停放节点下，
    /// 既隔离出场景视图、又因 parent 停用而不参与渲染/Update；停放点经 <c>parkingProvider</c> 按需解析——
    /// 节点被外部销毁（如手动删 Hierarchy 里的内部停放节点）时由提供方在下次入池时重建，归还实例不会散落到场景根。<br/>
    /// <b>归还防护：</b><see cref="Despawn"/> 对"非本池实例 / 重复归还"的短路在**所有构建**生效——防止同一 live 实例被两次入栈、
    /// 进而被发给两个调用方（Release 下静默的别名 bug）；仅诊断日志放在 Editor / Development Build。
    /// </remarks>
    public sealed class GameObjectPool : IGameObjectPool
    {
        private readonly GameObject _prefab;
        // 空闲实例停放点的提供者：按需解析而非缓存固定 Transform——停放节点被外部销毁时由提供方重建（见 PoolUtility.EnsureParkingFor）。
        private readonly Func<Transform> _parkingProvider;
        private readonly int _maxSize; // 0 = 不限容量
        private readonly Stack<GameObject> _inactive = new();

        // prefab 的本地 transform 默认值，复用实例时重置，让池化实例与新 Instantiate 行为一致。
        private readonly Vector3 _localPosition;
        private readonly Quaternion _localRotation;
        private readonly Vector3 _localScale;

        /// <param name="prefab">源 prefab，必填。</param>
        /// <param name="parkingProvider">
        /// 空闲实例停放点的提供者，必填。每次入池（归还 / 预热）时调用取节点——把"停放点是否仍有效"交给提供方，
        /// 它可在节点被外部销毁后重建，避免归还实例被 <c>SetParent</c> 到已销毁节点而散落到场景根。
        /// </param>
        /// <param name="maxSize">池容量上限；0 表示不限。超限的归还实例被 Destroy。</param>
        public GameObjectPool(GameObject prefab, Func<Transform> parkingProvider, int maxSize = 0)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            _parkingProvider = parkingProvider ?? throw new ArgumentNullException(nameof(parkingProvider));
            _prefab = prefab;
            _maxSize = maxSize < 0 ? 0 : maxSize;

            var t = prefab.transform;
            _localPosition = t.localPosition;
            _localRotation = t.localRotation;
            _localScale = t.localScale;
        }

        /// <param name="prefab">源 prefab，必填。</param>
        /// <param name="parkingRoot">固定停放点（应为停用的 DontDestroyOnLoad 节点），必填。</param>
        /// <param name="maxSize">池容量上限；0 表示不限。</param>
        /// <remarks>便利重载：停放点固定不自愈，用于测试或不会动停放节点的简单场景。生产经 <see cref="PoolUtility"/> 走 provider 重载。</remarks>
        public GameObjectPool(GameObject prefab, Transform parkingRoot, int maxSize = 0)
            : this(prefab, () => parkingRoot, maxSize)
        {
            if (parkingRoot == null) throw new ArgumentNullException(nameof(parkingRoot));
        }

        public GameObject Prefab => _prefab;
        public int CountInactive => _inactive.Count;

        // 借出计数：TakeOrCreate +1、通过归还防护的 Despawn -1。防护在所有构建生效，计数不会因误用漂移；
        // 实例被外部 Destroy 时停在借出侧（见 IGameObjectPool.CountActive 文档）。
        private int _countActive;

        public int CountActive => _countActive;

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
                Log.Error(
                    $"正在归还非池化 GameObject '{instance.name}'，已忽略。",
                    category: $"GameObjectPool({_prefab.name})");
#endif
                return;
            }
            // 以下两个短路在所有构建生效：把"非本池 / 已归还"的 live 实例挡在栈外，否则它会被二次入池、
            // 被下一次 Spawn 发给第二个调用方（同一对象别名）——Release 下静默且难查。诊断日志仅 Editor/Dev。
            if (!ReferenceEquals(marker.OwningPool, this))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Log.Error(
                    $"实例 '{instance.name}' 属于其他池，已拒绝归还。",
                    category: $"GameObjectPool({_prefab.name})");
#endif
                return;
            }
            if (!marker.IsSpawned)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Log.Error(
                    $"检测到实例 '{instance.name}' 被重复归还，已忽略。",
                    category: $"GameObjectPool({_prefab.name})");
#endif
                return;
            }

            InvokeReturn(marker);
            marker.IsSpawned = false;
            if (_countActive > 0) _countActive--;

            // 超过容量上限：直接 Destroy，不入池。先 SetActive(false) 与正常归还路径一致，
            // 避免延迟销毁前实例还活跃渲染/Update 一帧（OnReturn 已清理状态，留着会显示脏画面）。
            if (_maxSize != 0 && _inactive.Count >= _maxSize)
            {
                instance.SetActive(false);
                UnityEngine.Object.Destroy(instance);
                return;
            }

            instance.SetActive(false);
            var parking = _parkingProvider();
            if (parking == null) // 停放点不可用（如池工具已 Dispose）：直接销毁，不入栈——避免散落场景根或泄漏复活的停放根
            {
                UnityEngine.Object.Destroy(instance);
                return;
            }
            instance.transform.SetParent(parking, false);
            _inactive.Push(instance);
        }

        public async UniTask Prewarm(int count, int perFrame = 1, CancellationToken ct = default)
        {
            if (perFrame < 1) perFrame = 1;
            var thisFrame = 0;
            for (var i = 0; i < count; i++)
            {
                if (_maxSize != 0 && _inactive.Count >= _maxSize) break;
                var go = CreateNew(out _);
                go.SetActive(false);
                var parking = _parkingProvider();
                if (parking == null) { UnityEngine.Object.Destroy(go); break; } // 停放点不可用（池工具已 Dispose）：停止预热
                go.transform.SetParent(parking, false);
                _inactive.Push(go);
                // 每帧 perFrame 个，把实例化开销摊到多帧（通常在加载界面期间预热）。取消则中断，已建实例留在池中。
                if (++thisFrame >= perFrame)
                {
                    thisFrame = 0;
                    await UniTask.Yield(ct);
                }
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

        public async UniTask TrimAsync(int targetCount, int perFrame = 1, CancellationToken ct = default)
        {
            if (targetCount < 0) targetCount = 0;
            if (perFrame < 1) perFrame = 1;
            var thisFrame = 0;
            while (_inactive.Count > targetCount)
            {
                var go = _inactive.Pop();
                if (go != null) UnityEngine.Object.Destroy(go); // 跳过已被外部销毁的空槽
                if (++thisFrame >= perFrame)
                {
                    thisFrame = 0;
                    await UniTask.Yield(ct); // 摊到多帧；取消则中断，剩余空闲实例留在池中
                }
            }
        }

        public UniTask ClearAsync(int perFrame = 1, CancellationToken ct = default) => TrimAsync(0, perFrame, ct);

        // 取一个可用实例：跳过已被外部 Destroy 的空槽，池空则新建。一并返回标记，省掉调用处再次 GetComponent。
        private GameObject TakeOrCreate(out PooledObject marker)
        {
            _countActive++; // 两条出口都算借出
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
