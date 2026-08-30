using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

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
    /// 新 clone 也先创建在这棵停用层级下并完成 <see cref="PooledObject"/> 接线，随后才由 Spawn 定位、激活；
    /// active prefab 不会在预热或接线完成前提前触发 Awake/OnEnable。<br/>
    /// <b>归还防护：</b><see cref="Despawn"/> 对"非本池实例 / 重复归还"的短路在**所有构建**生效——防止同一 live 实例被两次入栈、
    /// 进而被发给两个调用方（Release 下静默的别名 bug）；仅诊断日志放在 Editor / Development Build。
    /// </remarks>
    public sealed class GameObjectPool : IGameObjectPool, IPoolLifetime
    {
        private readonly GameObject _prefab;
        // 空闲实例停放点的提供者：按需解析而非缓存固定 Transform——停放节点被外部销毁时由提供方重建（见 PoolUtility.EnsureParkingFor）。
        private readonly Func<Transform> _parkingProvider;
        private readonly int _maxSize; // 0 = 不限容量
        private readonly Stack<GameObject> _inactive = new();
        private readonly Stack<GameObject> _pruneBuffer = new();
        private bool _terminated;

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
        /// <param name="parkingRoot">固定停放点（必须在 Hierarchy 中停用，通常是停用的 DontDestroyOnLoad 节点），必填。</param>
        /// <param name="maxSize">池容量上限；0 表示不限。</param>
        /// <remarks>
        /// 便利重载：停放点固定不自愈，用于测试或不会动停放节点的简单场景。生产经 <see cref="PoolUtility"/> 走 provider 重载。
        /// parking 若仍在激活 Hierarchy 中，会在首次创建实例时 fail-fast，避免 active prefab 提前触发生命周期。
        /// </remarks>
        public GameObjectPool(GameObject prefab, Transform parkingRoot, int maxSize = 0)
            : this(prefab, () => parkingRoot, maxSize)
        {
            if (parkingRoot == null) throw new ArgumentNullException(nameof(parkingRoot));
        }

        public GameObject Prefab => _prefab;
        public int CountInactive
        {
            get
            {
                PruneDestroyedInactive();
                return _inactive.Count;
            }
        }

        // 只统计已成功发布给调用方的 Active lease。Renting / Returning 是同步事务态，不进入计数；
        // 实例被外部 Destroy 时仍停在借出侧（见 IGameObjectPool.CountActive 文档）。
        private int _countActive;

        public int CountActive => _countActive;

        public GameObject Spawn(Transform parent = null, bool worldPositionStays = false)
        {
            return SpawnCore(parent, worldPositionStays, default, default, useWorldPose: false);
        }

        public GameObject Spawn(Vector3 position, Quaternion rotation, Transform parent = null)
        {
            return SpawnCore(parent, false, position, rotation, useWorldPose: true);
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
            if (marker.State != PooledInstanceState.Active)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Log.Error(
                    marker.State is PooledInstanceState.Renting or PooledInstanceState.Returning
                        ? $"实例 '{instance.name}' 正处于池钩子事务中，已拒绝重入归还。"
                        : $"检测到实例 '{instance.name}' 被重复归还，已忽略。",
                    category: $"GameObjectPool({_prefab.name})");
#endif
                return;
            }

            // 先关闭调用方所有权，再执行用户钩子：OnReturn 重入 Despawn 时只能看到 Returning，不会递归或二次减计数。
            marker.State = PooledInstanceState.Returning;
            _countActive--;

            Exception failure = InvokeHooksBestEffort(marker, rent: false);
            failure = TryDeactivate(instance, failure);

            bool mustDestroy = failure != null || _terminated || instance == null || marker == null;
            // raw Count 尚未触顶时无需 O(n) 扫描；只有容量判断可能拒绝本次归还时，才压缩任意位置的 fake-null 槽再决定。
            if (!mustDestroy && _maxSize != 0 && _inactive.Count >= _maxSize)
            {
                PruneDestroyedInactive();
                mustDestroy = _inactive.Count >= _maxSize;
            }

            if (!mustDestroy)
            {
                try
                {
                    // provider 是同步扩展点，也可能在解析过程中终止池；终止后绝不复活 idle/parking。
                    Transform parking = _parkingProvider();
                    if (parking == null || _terminated)
                    {
                        mustDestroy = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        if (!_terminated)
                            Log.Warning(
                                "池停放节点不可用，归还实例将直接销毁。",
                                $"GameObjectPool({_prefab.name})");
#endif
                    }
                    else
                    {
                        instance.transform.SetParent(parking, false);
                        // SetParent 会同步触发 OnTransformParentChanged / OnTransformChildrenChanged；回调可能终止池或销毁实例。
                        // 回调返回后必须重新验明事务仍成立，不能在 Terminated 池里把当前实例重新压成 Inactive。
                        if (_terminated || instance == null || marker == null ||
                            marker.State != PooledInstanceState.Returning)
                            mustDestroy = true;
                    }
                }
                catch (Exception e)
                {
                    failure = CaptureFirst(failure, e, "停放归还实例时又发生异常");
                    mustDestroy = true;
                }
            }

            // provider / SetParent 都会同步进入扩展代码；它们可能重入 Prewarm 填满容量。
            // 容量上限是提交期不变量，最终 push 前必须用最新 idle 状态再判一次。
            if (!mustDestroy && _maxSize != 0 && _inactive.Count >= _maxSize)
            {
                PruneDestroyedInactive();
                mustDestroy = _inactive.Count >= _maxSize;
            }

            if (mustDestroy)
            {
                DestroyWithoutReuse(instance, marker, failure);
            }
            else
            {
                marker.State = PooledInstanceState.Inactive;
                _inactive.Push(instance);
            }

            if (failure != null) Rethrow(failure);
        }

        public async UniTask Prewarm(int count, int perFrame = 1, CancellationToken ct = default)
        {
            ThrowIfTerminated();
            if (perFrame < 1) perFrame = 1;
            var thisFrame = 0;
            for (var i = 0; i < count; i++)
            {
                ThrowIfTerminated();
                // 只在 raw Count 触顶时扫描死槽：正常填充为 O(count)，同时仍能补足被外部 Destroy 的空闲实例。
                if (_maxSize != 0 && _inactive.Count >= _maxSize)
                {
                    PruneDestroyedInactive();
                    if (_inactive.Count >= _maxSize) break;
                }
                GameObject go = null;
                PooledObject marker = null;
                try
                {
                    go = CreateNew(out marker);
                    ThrowIfTerminated();
                    // parkingProvider / Instantiate 的同步回调可能重入另一轮 Prewarm 并先填满容量。
                    // 外层只能在提交点重新验明还有空间；否则丢弃本次未发布 clone。
                    if (_maxSize != 0 && _inactive.Count >= _maxSize)
                    {
                        PruneDestroyedInactive();
                        if (_inactive.Count >= _maxSize)
                        {
                            DestroyWithoutReuse(go, marker, null);
                            break;
                        }
                    }
                    marker.State = PooledInstanceState.Inactive;
                    _inactive.Push(go);
                }
                catch (Exception e)
                {
                    DestroyWithoutReuse(go, marker, e);
                    throw;
                }
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
            ThrowIfTerminated();
            ClearInactive();
        }

        public async UniTask TrimAsync(int targetCount, int perFrame = 1, CancellationToken ct = default)
        {
            ThrowIfTerminated();
            if (targetCount < 0) targetCount = 0;
            if (perFrame < 1) perFrame = 1;
            PruneDestroyedInactive();
            var thisFrame = 0;
            while (_inactive.Count > targetCount)
            {
                var go = _inactive.Pop();
                DestroyWithoutReuse(go, GetMarker(go), null);
                if (++thisFrame >= perFrame)
                {
                    thisFrame = 0;
                    await UniTask.Yield(ct); // 摊到多帧；取消则中断，剩余空闲实例留在池中
                    ThrowIfTerminated();
                    PruneDestroyedInactive();
                }
            }
        }

        public UniTask ClearAsync(int perFrame = 1, CancellationToken ct = default) => TrimAsync(0, perFrame, ct);

        void IPoolLifetime.Terminate()
        {
            if (_terminated) return;
            _terminated = true;
            ClearInactive();
            // 已 Spawn 的实例仍由调用方持有；它们之后可 Despawn 完成 OnReturn，但只 Destroy、不再进入 parking。
        }

        private GameObject SpawnCore(
            Transform parent,
            bool worldPositionStays,
            Vector3 position,
            Quaternion rotation,
            bool useWorldPose)
        {
            ThrowIfTerminated();
            GameObject go = TakeOrCreate(out PooledObject marker);
            Exception failure = null;
            bool rentHooksStarted = false;

            try
            {
                Transform t = go.transform;
                Scene targetScene = parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene();
                if (!targetScene.IsValid() || !targetScene.isLoaded)
                    throw new InvalidOperationException(
                        $"GameObjectPool({_prefab.name}) 无法 Spawn 到无效或未加载的目标 Scene。" +
                        "parent 为空时目标是当前激活 Scene；否则目标取 parent 所属 Scene。");
                t.SetParent(parent, useWorldPose ? false : worldPositionStays);
                // parent 变化会同步进入用户脚本；若它终止池，本次 Spawn 在激活 prefab 生命周期前就回滚。
                ThrowIfTerminated();
                if (parent == null && go.scene != targetScene)
                {
                    // 新 clone 来自 DontDestroyOnLoad parking；仅 SetParent(null) 仍会留在 DDOL Scene。
                    // 显式迁回调用时的激活 Scene，兑现“parent=null = 当前场景根”，避免切场景后意外存活。
                    SceneManager.MoveGameObjectToScene(go, targetScene);
                    ThrowIfTerminated();
                }
                else if (parent != null && go.scene != targetScene)
                {
                    throw new InvalidOperationException(
                        $"GameObjectPool({_prefab.name}) 无法把实例移动到 parent 所属 Scene '{targetScene.name}'。");
                }
                if (useWorldPose)
                {
                    t.localScale = _localScale;
                    t.SetPositionAndRotation(position, rotation);
                }
                else if (!worldPositionStays)
                {
                    // worldPositionStays:true 时保留世界位姿（含 scale）；false 则模拟新 Instantiate 的 prefab local transform。
                    t.localPosition = _localPosition;
                    t.localRotation = _localRotation;
                    t.localScale = _localScale;
                }

                go.SetActive(true);
                rentHooksStarted = true;
                failure = InvokeHooksBestEffort(marker, rent: true);
                if (failure == null)
                {
                    if (_terminated)
                        failure = new ObjectDisposedException(
                            $"GameObjectPool({_prefab.name})",
                            "OnRent 期间所属 PoolUtility 已释放，本次 Spawn 不再发布。请检查钩子里的生命周期操作。");
                    else if (go == null || marker == null)
                        failure = new InvalidOperationException("OnRent 期间池化 GameObject 或其 PooledObject 标记被销毁。");
                }
            }
            catch (Exception e)
            {
                failure ??= e;
            }

            if (failure != null)
            {
                if (marker != null) marker.State = PooledInstanceState.Returning;
                if (rentHooksStarted)
                {
                    Exception rollbackFailure = InvokeHooksBestEffort(marker, rent: false);
                    if (rollbackFailure != null)
                        Log.Error(
                            "Spawn 激活失败后的 OnReturn 补偿也抛出异常；仍保留并重抛最初的 Spawn 异常。",
                            rollbackFailure,
                            $"GameObjectPool({_prefab.name})");
                }
                DestroyWithoutReuse(go, marker, failure);
                Rethrow(failure);
                return null;
            }

            marker.State = PooledInstanceState.Active;
            _countActive++;
            return go;
        }

        // 取一个可用实例：跳过 fake-null 或被外部破坏的空槽，池空则新建；事务状态先进入 Renting，尚不计 Active。
        private GameObject TakeOrCreate(out PooledObject marker)
        {
            while (_inactive.Count > 0)
            {
                var pooled = _inactive.Pop();
                if (TryGetReusableInactive(pooled, out marker))
                {
                    marker.State = PooledInstanceState.Renting;
                    return pooled;
                }
                DestroyWithoutReuse(pooled, GetMarker(pooled), null);
            }
            var created = CreateNew(out marker);
            marker.State = PooledInstanceState.Renting;
            return created;
        }

        // 新 clone 必须先进入 inactive-in-hierarchy 的 parking，再强制 activeSelf=false 并初始化标记。
        // 否则 active prefab 会在 Instantiate 返回前提前执行 Awake/OnEnable：那时目标 parent/pose、来源池和钩子缓存都尚未就绪，
        // Prewarm 也会产生一次“先激活再停用”的副作用。真正的首次激活只允许发生在 SpawnCore 完成定位之后。
        private GameObject CreateNew(out PooledObject marker)
        {
            marker = null;
            GameObject go = null;
            try
            {
                Transform parking = _parkingProvider();
                ThrowIfTerminated(); // provider 是可重入扩展点；终止后不能再实例化。
                if (parking == null)
                    throw new InvalidOperationException(
                        $"GameObjectPool({_prefab.name}) 的 parkingProvider 返回了 null，无法安全创建停用实例。");
                if (parking.gameObject.activeInHierarchy)
                    throw new InvalidOperationException(
                        $"GameObjectPool({_prefab.name}) 的 parking 必须在 Hierarchy 中停用；" +
                        "否则 active prefab 会在池完成接线前提前执行 Awake/OnEnable。");

                go = UnityEngine.Object.Instantiate(_prefab, parking, false);
                go.SetActive(false);
                ThrowIfTerminated();
                marker = go.GetComponent<PooledObject>();
                if (marker == null) marker = go.AddComponent<PooledObject>();
                marker.OwningPool = this;
                // 含未激活子节点：新实例此时被强制停用，必须传 true 才能完整缓存。
                marker.Poolables = go.GetComponentsInChildren<IPoolable>(true);
                marker.State = PooledInstanceState.Inactive;
                return go;
            }
            catch (Exception e)
            {
                DestroyWithoutReuse(go, marker, e);
                throw;
            }
        }

        // 所有仍存活的钩子都要获得清理机会；返回首异常，后续异常进日志接缝。
        // 缓存接口可能包装已 Destroy 的 Component，必须额外识别 Unity fake-null，不能只用 ?.。
        private Exception InvokeHooksBestEffort(PooledObject marker, bool rent)
        {
            Exception first = null;
            IPoolable[] poolables = marker != null ? marker.Poolables : null;
            if (poolables == null) return null;
            for (var i = 0; i < poolables.Length; i++)
            {
                IPoolable poolable = poolables[i];
                if (poolable == null || poolable is UnityEngine.Object unityObject && unityObject == null)
                    continue;
                try
                {
                    if (rent) poolable.OnRent();
                    else poolable.OnReturn();
                }
                catch (Exception e)
                {
                    first = CaptureFirst(
                        first,
                        e,
                        rent ? "后续 OnRent 钩子也抛出异常" : "后续 OnReturn 钩子也抛出异常");
                }
            }
            return first;
        }

        private Exception CaptureFirst(Exception first, Exception next, string secondaryMessage)
        {
            if (first == null) return next;
            Log.Error(
                $"{secondaryMessage}；最终仍重抛首个异常。",
                next,
                $"GameObjectPool({_prefab.name})");
            return first;
        }

        private Exception TryDeactivate(GameObject go, Exception first)
        {
            if (go == null) return first;
            try
            {
                go.SetActive(false);
            }
            catch (Exception e)
            {
                return CaptureFirst(first, e, "停用归还实例时又发生异常");
            }
            return first;
        }

        private void DestroyWithoutReuse(GameObject go, PooledObject marker, Exception primaryFailure)
        {
            if (marker != null) marker.State = PooledInstanceState.Returning;
            if (go == null) return;
            Exception cleanupFailure = TryDeactivate(go, primaryFailure);
            try
            {
                UnityEngine.Object.Destroy(go);
            }
            catch (Exception e)
            {
                CaptureFirst(cleanupFailure, e, "销毁不可复用实例时又发生异常");
            }
        }

        private void ClearInactive()
        {
            while (_inactive.Count > 0)
            {
                var go = _inactive.Pop();
                DestroyWithoutReuse(go, GetMarker(go), null);
            }
            _pruneBuffer.Clear();
        }

        // Unity Destroy 在帧末才把引用变成 fake-null；一旦变死，就不再是可立即复用的容量。
        // 栈中任意位置都可能是死槽，借复用缓冲完整过滤并保持原 LIFO 顺序，避免每次诊断分配临时集合。
        private void PruneDestroyedInactive()
        {
            while (_inactive.Count > 0)
            {
                var go = _inactive.Pop();
                if (TryGetReusableInactive(go, out _))
                    _pruneBuffer.Push(go);
                else
                    DestroyWithoutReuse(go, GetMarker(go), null);
            }
            while (_pruneBuffer.Count > 0)
                _inactive.Push(_pruneBuffer.Pop());
        }

        private bool TryGetReusableInactive(GameObject go, out PooledObject marker)
        {
            marker = GetMarker(go);
            return go != null && marker != null && ReferenceEquals(marker.OwningPool, this) &&
                   marker.State == PooledInstanceState.Inactive;
        }

        private static PooledObject GetMarker(GameObject go) => go != null ? go.GetComponent<PooledObject>() : null;

        private static void Rethrow(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        private void ThrowIfTerminated()
        {
            if (_terminated)
                throw new ObjectDisposedException(
                    $"GameObjectPool({_prefab.name})",
                    "所属 PoolUtility 已释放；旧实例只允许 Despawn，不能继续 Spawn/Prewarm/维护池。");
        }
    }
}
