using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Pool;
using Game.Framework.Systems;
using Game.Framework.Utility;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证 GameObject/Prefab 池：GameObjectPool 复用/停用/parking/IPoolable 钩子/容量/预热，
    /// PoolUtility 按 prefab 管理 + Despawn 路由，DisposableBag.Spawn 自动归还。
    /// PlayMode 测试——依赖 Instantiate / SetActive / 销毁等 Unity 运行时行为。
    /// </summary>
    public class GameObjectPoolTests
    {
        // 测试用池化组件：记录 OnRent/OnReturn 次数。
        private sealed class TestPoolable : MonoBehaviour, IPoolable
        {
            public int RentCount;
            public int ReturnCount;
            public void OnRent() => RentCount++;
            public void OnReturn() => ReturnCount++;
        }

        // 异常 / 重入路径用探针。静态回调只在单个测试的 SetUp/TearDown 之间生效，
        // 避免依赖 Unity 是否会克隆委托字段，同时仍能在首次 Spawn 的 OnRent 内注入行为。
        private sealed class LifecycleProbe : MonoBehaviour, IPoolable
        {
            public static Action<LifecycleProbe> RentAction;
            public static Action<LifecycleProbe> ReturnAction;
            public static GameObject LastInstance;

            public int RentCount;
            public int ReturnCount;

            public void OnRent()
            {
                LastInstance = gameObject;
                RentCount++;
                RentAction?.Invoke(this);
            }

            public void OnReturn()
            {
                ReturnCount++;
                ReturnAction?.Invoke(this);
            }

            public static void Reset()
            {
                RentAction = null;
                ReturnAction = null;
                LastInstance = null;
            }
        }

        // 单独的尾部探针用于证明：前一个 IPoolable.OnReturn 抛错后，后续清理钩子仍会执行。
        private sealed class TailReturnProbe : MonoBehaviour, IPoolable
        {
            public static int ReturnCount;
            public void OnRent() { }
            public void OnReturn() => ReturnCount++;
        }

        // active prefab 首次创建顺序探针：预热不能触发激活生命周期；正式 Spawn 时 Awake/OnEnable 必须看到已接线 marker 与最终 parent/pose。
        private sealed class ActivationOrderProbe : MonoBehaviour, IPoolable
        {
            public static readonly List<string> Events = new();
            public static Transform ExpectedParent;
            public static Vector3 ExpectedWorldPosition;
            public static bool ObservedReadyState = true;

            private void Awake()
            {
                Events.Add(nameof(Awake));
                ObserveReadyState();
            }

            private void OnEnable()
            {
                Events.Add(nameof(OnEnable));
                ObserveReadyState();
            }

            public void OnRent()
            {
                Events.Add(nameof(OnRent));
                ObserveReadyState();
            }

            public void OnReturn() => Events.Add(nameof(OnReturn));

            private void ObserveReadyState()
            {
                ObservedReadyState &= GetComponent<PooledObject>() != null &&
                                      transform.parent == ExpectedParent &&
                                      Vector3.Distance(transform.position, ExpectedWorldPosition) < 0.001f;
            }

            public static void Reset()
            {
                Events.Clear();
                ExpectedParent = null;
                ExpectedWorldPosition = default;
                ObservedReadyState = true;
            }
        }

        private sealed class ParentChangeProbe : MonoBehaviour
        {
            public static Action<GameObject> Changed;
            private void OnTransformParentChanged() => Changed?.Invoke(gameObject);
        }

        private GameObject _root;     // 所有测试对象的父节点，TearDown 一次性销毁
        private GameObject _prefab;   // 源 prefab（停用，避免在原位置触发 Awake）
        private Transform _parking;   // 直接构造 GameObjectPool 用的 parking 节点（在 _root 下，便于清理）

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            LifecycleProbe.Reset();
            TailReturnProbe.ReturnCount = 0;
            ActivationOrderProbe.Reset();
            ParentChangeProbe.Changed = null;
            _root = new GameObject("GameObjectPoolTestRoot");

            _prefab = new GameObject("PoolPrefab");
            _prefab.SetActive(false);
            _prefab.AddComponent<TestPoolable>();

            var parkingGo = new GameObject("Parking");
            parkingGo.transform.SetParent(_root.transform);
            parkingGo.SetActive(false);
            _parking = parkingGo.transform;

            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            LifecycleProbe.Reset();
            TailReturnProbe.ReturnCount = 0;
            ActivationOrderProbe.Reset();
            ParentChangeProbe.Changed = null;
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (_prefab != null) UnityEngine.Object.Destroy(_prefab);
            _root = null;
            _prefab = null;
            _parking = null;
            yield return null;
        }

        // ── GameObjectPool 核心 ─────────────────────────────────────────────

        [Test]
        public void Spawn_FromEmptyPool_ReturnsActiveInstanceUnderParent()
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var go = pool.Spawn(_root.transform);

            Assert.IsTrue(go.activeSelf, "Spawn 出来的实例应被激活");
            Assert.AreSame(_root.transform, go.transform.parent, "应挂到指定 parent");
            Assert.AreEqual(0, pool.CountInactive);
        }

        [Test]
        public void SpawnDespawn_ReusesSameInstance()
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var a = pool.Spawn(_root.transform);
            pool.Despawn(a);
            Assert.AreEqual(1, pool.CountInactive, "Despawn 后应入池");

            var b = pool.Spawn(_root.transform);
            Assert.AreSame(a, b, "应复用同一实例");
            Assert.AreEqual(0, pool.CountInactive);
        }

        [Test]
        public void Despawn_DeactivatesAndReparentsToParking()
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var go = pool.Spawn(_root.transform);
            pool.Despawn(go);

            Assert.IsFalse(go.activeSelf, "Despawn 应停用实例");
            Assert.AreSame(_parking, go.transform.parent, "Despawn 应挂回 parking 节点");
        }

        [Test]
        public void Poolable_HooksInvokedOnSpawnAndDespawn()
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var go = pool.Spawn(_root.transform);
            var p = go.GetComponent<TestPoolable>();
            Assert.AreEqual(1, p.RentCount, "Spawn 应触发 OnRent");
            Assert.AreEqual(0, p.ReturnCount);

            pool.Despawn(go);
            Assert.AreEqual(1, p.ReturnCount, "Despawn 应触发 OnReturn");

            var go2 = pool.Spawn(_root.transform);
            Assert.AreSame(go, go2);
            Assert.AreEqual(2, p.RentCount, "复用实例应再次触发 OnRent");
        }

        [UnityTest]
        public IEnumerator Spawn_OnRentThrows_CompensatesAndDestroysWithoutPublishing()
        {
            _prefab.AddComponent<LifecycleProbe>();
            var expected = new InvalidOperationException("rent failed");
            LifecycleProbe.RentAction = _ => throw expected;
            var pool = new GameObjectPool(_prefab, _parking);

            var thrown = Assert.Throws<InvalidOperationException>(() => pool.Spawn(_root.transform));
            Assert.AreSame(expected, thrown, "补偿清理不能覆盖最初的 OnRent 异常");
            Assert.AreEqual(0, pool.CountActive, "失败的 Spawn 不得发布活动 lease");
            Assert.AreEqual(0, pool.CountInactive, "执行过业务钩子的脏实例不得回池复用");

            var failedInstance = LifecycleProbe.LastInstance;
            Assert.IsTrue(failedInstance != null, "探针应记录本次创建的实例");
            Assert.AreEqual(1, failedInstance.GetComponent<LifecycleProbe>().ReturnCount,
                "OnRent 失败后应尽力执行一次 OnReturn 补偿");
            Assert.IsFalse(failedInstance.activeSelf, "延迟销毁前也应先停用失败实例");

            yield return null;
            yield return null;
            Assert.IsTrue(failedInstance == null, "失败实例最终应被销毁，不能留下无主对象");
        }

        [UnityTest]
        public IEnumerator ActivePrefab_PrewarmStaysDormant_AndFirstSpawnActivatesAfterWiring() => UniTask.ToCoroutine(async () =>
        {
            var activePrefab = new GameObject("ActivePoolPrefab");
            activePrefab.transform.SetParent(_root.transform);
            activePrefab.AddComponent<ActivationOrderProbe>();
            ActivationOrderProbe.Reset(); // 排除源对象 AddComponent 时自身的 Awake / OnEnable。

            var pool = new GameObjectPool(activePrefab, _parking);
            await pool.Prewarm(1);
            CollectionAssert.IsEmpty(
                ActivationOrderProbe.Events,
                "active prefab 的预热实例必须始终处于 inactive hierarchy，不能先触发一次 Awake/OnEnable 再停用");

            var position = new Vector3(7f, 8f, 9f);
            ActivationOrderProbe.ExpectedParent = _root.transform;
            ActivationOrderProbe.ExpectedWorldPosition = position;
            var spawned = pool.Spawn(position, Quaternion.identity, _root.transform);

            CollectionAssert.AreEqual(
                new[] { "Awake", "OnEnable", "OnRent" },
                ActivationOrderProbe.Events,
                "首次生命周期顺序应为完成池接线与定位后 Awake/OnEnable，再进入 OnRent");
            Assert.IsTrue(ActivationOrderProbe.ObservedReadyState,
                "Awake/OnEnable/OnRent 都应看到 PooledObject 标记、最终 parent 与最终世界位置");
            Assert.IsTrue(spawned.activeSelf);
        });

        [UnityTest]
        public IEnumerator Despawn_OnReturnThrows_ContinuesCleanupAndDestroysDirtyInstance()
        {
            _prefab.AddComponent<LifecycleProbe>();
            _prefab.AddComponent<TailReturnProbe>();
            var expected = new InvalidOperationException("return failed");
            LifecycleProbe.ReturnAction = _ => throw expected;
            var pool = new GameObjectPool(_prefab, _parking);
            var go = pool.Spawn(_root.transform);

            var thrown = Assert.Throws<InvalidOperationException>(() => pool.Despawn(go));
            Assert.AreSame(expected, thrown, "后续物理清理不能覆盖最初的 OnReturn 异常");
            Assert.AreEqual(1, TailReturnProbe.ReturnCount,
                "一个 OnReturn 抛错不应阻断其余 IPoolable 的 best-effort 清理");
            Assert.AreEqual(0, pool.CountActive, "抛错归还也必须结束活动 lease");
            Assert.AreEqual(0, pool.CountInactive, "清理钩子失败的脏实例不得重新入池");
            Assert.IsFalse(go.activeSelf, "延迟销毁前应先停用脏实例");

            yield return null;
            yield return null;
            Assert.IsTrue(go == null, "OnReturn 失败的实例最终应被销毁");
        }

        [Test]
        public void Spawn_OnRentReentrantDespawn_IsRejectedWithoutPublishingAlias()
        {
            _prefab.AddComponent<LifecycleProbe>();
            GameObjectPool pool = null;
            LifecycleProbe.RentAction = probe => pool.Despawn(probe.gameObject);
            pool = new GameObjectPool(_prefab, _parking);

            LogAssert.Expect(LogType.Error, new Regex("事务中"));
            var first = pool.Spawn(_root.transform);

            Assert.IsTrue(first.activeSelf);
            Assert.AreEqual(1, pool.CountActive, "OnRent 重入不能提前关闭尚未发布的 lease");
            Assert.AreEqual(0, pool.CountInactive, "Renting 实例不得在外层 Spawn 返回前进入空闲栈");

            LogAssert.Expect(LogType.Error, new Regex("事务中"));
            var second = pool.Spawn(_root.transform);
            Assert.AreNotSame(first, second, "同一实例不得因 OnRent 重入而同时发布给两个调用方");
        }

        [Test]
        public void Despawn_ReentrantCall_IsRejectedWithoutRecursionOrAliasing()
        {
            _prefab.AddComponent<LifecycleProbe>();
            GameObjectPool pool = null;
            LifecycleProbe.ReturnAction = probe => pool.Despawn(probe.gameObject);
            pool = new GameObjectPool(_prefab, _parking);
            var go = pool.Spawn(_root.transform);

            LogAssert.Expect(LogType.Error, new Regex("归还"));
            pool.Despawn(go);

            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(1, pool.CountInactive, "重入 Despawn 只能让实例入池一次");
            var first = pool.Spawn(_root.transform);
            var second = pool.Spawn(_root.transform);
            Assert.AreSame(go, first, "原实例应仍可被一个调用方正常复用");
            Assert.AreNotSame(first, second, "重入不得把同一实例发布给两个调用方");
        }

        [UnityTest]
        public IEnumerator Despawn_WhenParentCallbackDisposesUtility_DoesNotReviveInactivePool() => UniTask.ToCoroutine(async () =>
        {
            _prefab.AddComponent<ParentChangeProbe>();
            var util = new PoolUtility();
            var pool = util.GetGameObjectPool(_prefab);
            var go = pool.Spawn(_root.transform);
            ParentChangeProbe.Changed = _ => util.Dispose();

            util.Despawn(go); // SetParent(parking) 的同步回调终止池；外层归还必须复检并 Destroy，不能继续 push idle。
            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(0, pool.CountInactive, "终止回调返回后不得把实例重新写回已关闭池");
            Assert.IsFalse(go.activeSelf);

            await UniTask.Yield();
            await UniTask.Yield();
            Assert.IsTrue(go == null, "回调中终止池的归还实例最终应被销毁");
        });

        [UnityTest]
        public IEnumerator Despawn_WhenParentCallbackFillsPool_StillHonorsMaxSizeAtCommit() => UniTask.ToCoroutine(async () =>
        {
            _prefab.AddComponent<ParentChangeProbe>();
            var pool = new GameObjectPool(_prefab, _parking, maxSize: 1);
            var returning = pool.Spawn(_root.transform);
            var reentered = false;
            ParentChangeProbe.Changed = _ =>
            {
                if (reentered) return;
                reentered = true;
                // count=1 且 perFrame=2 会同步完成：在外层 SetParent 回调里先填满空闲槽。
                pool.Prewarm(1, perFrame: 2).GetAwaiter().GetResult();
            };

            pool.Despawn(returning);

            Assert.IsTrue(reentered, "测试前提：SetParent 回调应重入预热");
            Assert.AreEqual(1, pool.CountInactive,
                "maxSize 必须在最终入栈时仍成立，回调中填满池后外层实例不得超额入池");

            await UniTask.Yield();
            await UniTask.Yield();
            Assert.IsTrue(returning == null, "提交时超出容量的外层归还实例应被销毁");
        });

        [Test]
        public void Spawn_WithPositionRotation_AppliesWorldTransform()
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var pos = new Vector3(1, 2, 3);
            var rot = Quaternion.Euler(0, 90, 0);
            var go = pool.Spawn(pos, rot, _root.transform);

            Assert.Less(Vector3.Distance(go.transform.position, pos), 0.001f, "应置于指定世界位置");
            Assert.Less(Quaternion.Angle(go.transform.rotation, rot), 0.1f, "应置于指定世界旋转");
        }

        [Test]
        public void MaxSize_DestroysBeyondCap()
        {
            var pool = new GameObjectPool(_prefab, _parking, maxSize: 1);
            var a = pool.Spawn(_root.transform);
            var b = pool.Spawn(_root.transform);
            pool.Despawn(a);
            pool.Despawn(b); // 超过 cap=1，应被 Destroy 而非入池
            Assert.AreEqual(1, pool.CountInactive);
        }

        [UnityTest]
        public IEnumerator MaxSize_PrunesDestroyedInactiveBeforeCapacityDecision()
        {
            var pool = new GameObjectPool(_prefab, _parking, maxSize: 2);
            var dead = pool.Spawn(_root.transform);
            var live = pool.Spawn(_root.transform);
            var returning = pool.Spawn(_root.transform);
            pool.Despawn(dead);
            pool.Despawn(live);
            Assert.AreEqual(2, pool.CountInactive);

            UnityEngine.Object.Destroy(dead);
            yield return null;
            yield return null;
            Assert.IsTrue(dead == null, "测试前提：一个空闲槽已成为 Unity fake-null");

            // 刻意不在 Despawn 前读取 CountInactive；容量判断自身必须压缩所有死槽。
            pool.Despawn(returning);
            Assert.AreEqual(2, pool.CountInactive,
                "死槽不占容量，新归还的有效实例应进入池而不是被误判超限后销毁");

            var a = pool.Spawn(_root.transform);
            var b = pool.Spawn(_root.transform);
            Assert.IsTrue(a != null && b != null);
            Assert.AreNotSame(a, b, "压缩死槽后仍应保留两个互不别名的有效实例");
        }

        [UnityTest]
        public IEnumerator Prewarm_ReplenishesDestroyedInactiveSlotAtCapacity() => UniTask.ToCoroutine(async () =>
        {
            var pool = new GameObjectPool(_prefab, _parking, maxSize: 3);
            await pool.Prewarm(3, perFrame: 3);
            var dead = pool.Spawn(_root.transform);
            pool.Despawn(dead);

            UnityEngine.Object.Destroy(dead);
            await UniTask.Yield();
            await UniTask.Yield();
            Assert.IsTrue(dead == null, "测试前提：容量内有一个已销毁的空闲槽");

            // 不先读取 CountInactive，确保 Prewarm 自己在 raw Count 触顶时压缩死槽并补足容量。
            await pool.Prewarm(1);
            Assert.AreEqual(3, pool.CountInactive, "被外部销毁的空闲槽不应永久占用预热容量");
        });

        [UnityTest]
        public IEnumerator Prewarm_WhenProviderReenters_StillHonorsMaxSizeAtCommit() => UniTask.ToCoroutine(async () =>
        {
            GameObjectPool pool = null;
            var reentered = false;
            Transform ParkingProvider()
            {
                if (pool != null && !reentered)
                {
                    reentered = true;
                    // 内层先填满 maxSize=1；外层 CreateNew 返回后必须重新检查容量。
                    pool.Prewarm(1, perFrame: 2).GetAwaiter().GetResult();
                }
                return _parking;
            }

            pool = new GameObjectPool(_prefab, ParkingProvider, maxSize: 1);
            await pool.Prewarm(1, perFrame: 2);

            Assert.IsTrue(reentered, "测试前提：parkingProvider 应重入预热");
            Assert.AreEqual(1, pool.CountInactive,
                "provider 重入可以改变容量，但最终提交不得突破 maxSize");
        });

        [UnityTest]
        public IEnumerator Prewarm_PopulatesInactive() => UniTask.ToCoroutine(async () =>
        {
            var pool = new GameObjectPool(_prefab, _parking);
            await pool.Prewarm(3);
            Assert.AreEqual(3, pool.CountInactive, "预热应填充空闲实例");

            pool.Spawn(_root.transform);
            Assert.AreEqual(2, pool.CountInactive, "预热的实例应可被 Spawn 复用");
        });

        [UnityTest]
        public IEnumerator Clear_EmptiesPool() => UniTask.ToCoroutine(async () =>
        {
            var pool = new GameObjectPool(_prefab, _parking);
            await pool.Prewarm(2);
            Assert.AreEqual(2, pool.CountInactive);

            pool.Clear();
            Assert.AreEqual(0, pool.CountInactive, "Clear 应清空空闲实例");
        });

        [Test]
        public void Despawn_NonPooledObject_LogsErrorAndIgnores()
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var foreign = new GameObject("Foreign");
            foreign.transform.SetParent(_root.transform);

            LogAssert.Expect(LogType.Error, new Regex("非池化 GameObject.*Foreign"));
            pool.Despawn(foreign); // 无 PooledObject 标记 → 报错并忽略
            Assert.AreEqual(0, pool.CountInactive);
        }

        // ── PoolUtility 路由 ────────────────────────────────────────────────

        [Test]
        public void Utility_GetGameObjectPool_SameInstancePerPrefab()
        {
            IPoolUtility util = new PoolUtility();
            Assert.AreSame(util.GetGameObjectPool(_prefab), util.GetGameObjectPool(_prefab));
        }

        [Test]
        public void Utility_SpawnDespawn_RoutesBackToSourcePool()
        {
            IPoolUtility util = new PoolUtility();
            var go = util.Spawn(_prefab, _root.transform);
            Assert.IsTrue(go.activeSelf);

            util.Despawn(go); // 经 PooledObject 标记路由回源池
            Assert.AreEqual(1, util.GetGameObjectPool(_prefab).CountInactive);

            var again = util.Spawn(_prefab, _root.transform);
            Assert.AreSame(go, again, "归还的实例应被下次 Spawn 复用");
        }

        [UnityTest]
        public IEnumerator Utility_SpawnMovesInstanceFromPersistentParkingToRequestedScene() => UniTask.ToCoroutine(async () =>
        {
            var util = new PoolUtility();
            var pool = util.GetGameObjectPool(_prefab);
            var activeScene = SceneManager.GetActiveScene();
            var additiveScene = SceneManager.CreateScene($"PoolTarget-{Guid.NewGuid():N}");
            GameObject activeRootInstance = null;
            GameObject additiveParent = null;
            GameObject additiveChild = null;

            try
            {
                activeRootInstance = pool.Spawn();
                Assert.AreEqual(activeScene, activeRootInstance.scene,
                    "parent=null 的契约是当前激活 Scene 根，不能因为实例来自 DDOL parking 而继续常驻");
                pool.Despawn(activeRootInstance);
                activeRootInstance = null;

                additiveParent = new GameObject("AdditiveSceneParent");
                SceneManager.MoveGameObjectToScene(additiveParent, additiveScene);
                additiveChild = pool.Spawn(additiveParent.transform);
                Assert.AreEqual(additiveScene, additiveChild.scene,
                    "指定 parent 时，池化实例应属于 parent 所在 Scene");
                pool.Despawn(additiveChild);
                additiveChild = null;
            }
            finally
            {
                util.Dispose();
                if (activeRootInstance != null) UnityEngine.Object.Destroy(activeRootInstance);
                if (additiveChild != null) UnityEngine.Object.Destroy(additiveChild);
                if (additiveParent != null) UnityEngine.Object.Destroy(additiveParent);

                var unload = SceneManager.UnloadSceneAsync(additiveScene);
                if (unload != null) await unload.ToUniTask();
            }
        });

        [UnityTest]
        public IEnumerator Utility_SelfHealsParking_AfterRootDestroyedExternally() => UniTask.ToCoroutine(async () =>
        {
            IPoolUtility util = new PoolUtility();

            // 正常归还一次，定位内部停放总根（停放子节点的父节点）。
            var go1 = util.Spawn(_prefab, _root.transform);
            util.Despawn(go1);
            var parking = go1.transform.parent;
            Assert.IsTrue(parking != null, "归还实例应挂到内部停放子节点下");
            var parkingRoot = parking.parent;
            Assert.IsTrue(parkingRoot != null, "停放子节点应挂在内部停放总根下");
            Assert.AreEqual("[Game.Framework PooledObjects]", parkingRoot.name);

            // 模拟用户手动删 [Game.Framework PooledObjects] 节点（连同其下空闲实例一起销毁）。
            UnityEngine.Object.Destroy(parkingRoot.gameObject);
            await UniTask.Yield(); // 等 Unity 完成销毁，旧节点变 fake-null
            await UniTask.Yield();
            Assert.IsTrue(parking == null, "总根销毁后旧停放节点应变 Unity fake-null");

            // 再归还：池应自愈重建停放点，归还实例停回容器，而不是被 SetParent(已销毁) 扔到场景根。
            var go2 = util.Spawn(_prefab, _root.transform);
            util.Despawn(go2);
            Assert.IsTrue(go2.transform.parent != null,
                "自愈后归还实例应挂回重建的停放节点，而不是落到场景根（parent == null）");
            Assert.IsFalse(go2.activeSelf, "归还实例应停用");

            // 清理重建出来的总根（DontDestroyOnLoad，不在 _root 下，TearDown 不会清它）。
            var healedRoot = go2.transform.parent != null ? go2.transform.parent.parent : null;
            if (healedRoot != null) UnityEngine.Object.Destroy(healedRoot.gameObject);
        });

        // ── DisposableBag.Spawn 自动归还 ────────────────────────────────────

        [Test]
        public void Bag_Spawn_AutoDespawnsOnDispose()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());

            var pool = ctx.GetUtility<IPoolUtility>().GetGameObjectPool(_prefab);

            GameObject spawned;
            using (var bag = ctx.CreateBag())
            {
                spawned = bag.Spawn(_prefab, _root.transform);
                Assert.IsTrue(spawned.activeSelf);
                Assert.AreEqual(0, pool.CountInactive, "Spawn 后池中应无空闲实例");
            }

            Assert.AreEqual(1, pool.CountInactive, "bag.Dispose 应自动 Despawn 归还");
            Assert.IsFalse(spawned.activeSelf, "归还后实例应停用");
        }

        [Test]
        public void Bag_SpawnWithPositionRotation_AutoDespawns()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());
            var pool = ctx.GetUtility<IPoolUtility>().GetGameObjectPool(_prefab);

            var pos = new Vector3(4, 5, 6);
            using (var bag = ctx.CreateBag())
            {
                var go = bag.Spawn(_prefab, pos, Quaternion.identity);
                Assert.Less(Vector3.Distance(go.transform.position, pos), 0.001f, "应置于指定世界位置");
                Assert.AreEqual(0, pool.CountInactive);
            }
            Assert.AreEqual(1, pool.CountInactive, "bag.Dispose 应自动归还");
        }

        [Test]
        public void Bag_Spawn_WhenOnRentDisposesBag_DoesNotPublishReturnedInstance()
        {
            _prefab.AddComponent<LifecycleProbe>();
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());
            var pool = ctx.GetUtility<IPoolUtility>().GetGameObjectPool(_prefab);
            using var bag = ctx.CreateBag();
            LifecycleProbe.RentAction = _ => bag.Dispose();

            Assert.Throws<ObjectDisposedException>(() => bag.Spawn(_prefab, _root.transform),
                "OnRent 内关闭 bag 后，不应把随后已自动归还的实例交付给调用方");

            var instance = LifecycleProbe.LastInstance;
            Assert.IsTrue(instance != null, "探针应记录被立即归还的实例");
            Assert.IsFalse(instance.activeSelf, "晚到 lease 应由已关闭 bag 立即归还");
            Assert.AreEqual(1, instance.GetComponent<LifecycleProbe>().ReturnCount,
                "晚到 lease 的归还钩子应且仅应执行一次");
            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(1, pool.CountInactive, "实例应回到源池，但不得从 bag.Spawn 发布给调用方");
        }

        [Test]
        public void Bag_Despawn_ReleasesSingleEarly()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());
            var pool = ctx.GetUtility<IPoolUtility>().GetGameObjectPool(_prefab);

            GameObject a, b;
            using (var bag = ctx.CreateBag())
            {
                a = bag.Spawn(_prefab, _root.transform);
                b = bag.Spawn(_prefab, _root.transform);

                bag.Despawn(a);
                Assert.AreEqual(1, pool.CountInactive, "提前 Despawn a 后池中应有 1 个空闲");
                Assert.IsFalse(a.activeSelf, "a 应已停用");
                Assert.AreEqual(1, a.GetComponent<TestPoolable>().ReturnCount);
                Assert.IsTrue(b.activeSelf, "b 仍被 bag 持有");
            }

            // bag.Dispose 只归还剩余的 b；a 的登记已摘除，不会触发重复 Despawn 错误日志
            Assert.AreEqual(2, pool.CountInactive);
            Assert.AreEqual(1, a.GetComponent<TestPoolable>().ReturnCount, "a 不应被 Dispose 重复归还");
            Assert.AreEqual(1, b.GetComponent<TestPoolable>().ReturnCount);
        }

        [UnityTest]
        public IEnumerator Bag_Despawn_DestroyedInstance_SkipsQuietly()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());
            var pool = ctx.GetUtility<IPoolUtility>().GetGameObjectPool(_prefab);

            var bag = ctx.CreateBag();
            var go = bag.Spawn(_prefab, _root.transform);
            UnityEngine.Object.Destroy(go);
            yield return null;   // Destroy 帧末生效，等一帧让实例变 fake null

            bag.Despawn(go);     // 实例已死：登记被摘除，归还句柄的 null 守卫跳过入池，不报错
            Assert.AreEqual(0, pool.CountInactive, "已销毁实例不应入池");

            bag.Dispose();       // 登记已摘除：不产生重复 Despawn / 死实例归还的错误日志
            Assert.AreEqual(0, pool.CountInactive);
        }

        // ── 归还防护（Release 也生效的短路）─────────────────────────────────

        [Test]
        public void Despawn_Twice_IgnoresSecondAndDoesNotDoublePool()
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var go = pool.Spawn(_root.transform);
            pool.Despawn(go);
            Assert.AreEqual(1, pool.CountInactive);

            LogAssert.Expect(LogType.Error, new Regex("重复归还"));
            pool.Despawn(go); // 第二次归还应被短路，不再次入池
            Assert.AreEqual(1, pool.CountInactive, "重复 Despawn 不应把同一实例二次入池（避免别名 bug）");
        }

        [Test]
        public void Despawn_IntoDifferentPool_IgnoredAndLogsError()
        {
            var poolA = new GameObjectPool(_prefab, _parking);
            var parkingB = new GameObject("ParkingB");
            parkingB.transform.SetParent(_root.transform);
            parkingB.SetActive(false);
            var poolB = new GameObjectPool(_prefab, parkingB.transform);

            var goA = poolA.Spawn(_root.transform); // OwningPool = poolA
            LogAssert.Expect(LogType.Error, new Regex("属于其他池"));
            poolB.Despawn(goA); // 归还到错误的池应被拒绝
            Assert.AreEqual(0, poolB.CountInactive, "不应把别的池的实例入本池");
        }

        // ── 复用语义 ────────────────────────────────────────────────────────

        [Test]
        public void Spawn_WorldPositionStaysTrue_DoesNotResetTransform()
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var a = pool.Spawn(_root.transform);
            a.transform.localScale = new Vector3(3, 3, 3);
            pool.Despawn(a);

            var b = pool.Spawn(_root.transform, worldPositionStays: true);
            Assert.AreSame(a, b);
            Assert.AreEqual(3f, b.transform.localScale.x, 0.001f,
                "worldPositionStays:true 应保留世界位姿，不把 scale 重置为 prefab 默认");
        }

        [UnityTest]
        public IEnumerator Spawn_AfterPooledInstanceDestroyedExternally_CreatesNew() => UniTask.ToCoroutine(async () =>
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var a = pool.Spawn(_root.transform);
            pool.Despawn(a);
            Assert.AreEqual(1, pool.CountInactive);

            UnityEngine.Object.Destroy(a); // 外部直接销毁池中的空闲实例
            await UniTask.Yield();          // 等 Unity 完成销毁，a 变 fake-null
            await UniTask.Yield();

            var b = pool.Spawn(_root.transform);
            Assert.IsTrue(b != null, "应跳过被销毁的空槽、新建实例，而非返回 null 或 NRE");
            Assert.AreNotSame(a, b);
            Assert.AreEqual(0, pool.CountInactive);
        });

        // ── 分帧收缩 / 预热节流 ──────────────────────────────────────────────

        [UnityTest]
        public IEnumerator TrimAsync_ShrinksToTarget() => UniTask.ToCoroutine(async () =>
        {
            var pool = new GameObjectPool(_prefab, _parking);
            await pool.Prewarm(5);
            Assert.AreEqual(5, pool.CountInactive);

            await pool.TrimAsync(2, perFrame: 2);
            Assert.AreEqual(2, pool.CountInactive, "TrimAsync 应分帧收缩到 targetCount");
        });

        [UnityTest]
        public IEnumerator ClearAsync_EmptiesPool() => UniTask.ToCoroutine(async () =>
        {
            var pool = new GameObjectPool(_prefab, _parking);
            await pool.Prewarm(3);
            await pool.ClearAsync();
            Assert.AreEqual(0, pool.CountInactive, "ClearAsync 应分帧清空");
        });

        [UnityTest]
        public IEnumerator Prewarm_PerFrame_PopulatesAllRequested() => UniTask.ToCoroutine(async () =>
        {
            var pool = new GameObjectPool(_prefab, _parking);
            await pool.Prewarm(5, perFrame: 2);
            Assert.AreEqual(5, pool.CountInactive, "perFrame 节流的预热也应建满 count 个");
        });

        // ── Mono 版：注册 + 转发 + 随宿主销毁清池 ────────────────────────────

        [UnityTest]
        public IEnumerator MonoPoolUtility_Registers_Forwards_DisposesParkingOnDestroy() => UniTask.ToCoroutine(async () =>
        {
            var ctxGo = new GameObject("Ctx");
            ctxGo.transform.SetParent(_root.transform);
            var ctx = ctxGo.AddComponent<MonoGameContextBase>();

            var poolGo = new GameObject("MonoPool");
            poolGo.transform.SetParent(ctxGo.transform);
            var monoPool = poolGo.AddComponent<MonoPoolUtility>();
            await UniTask.Yield(); // 等 Awake 注册

            // Mono 路径：注册为 IPoolUtility（MonoUtilityBase 注册具体类型 + 派生接口）
            Assert.AreSame(monoPool, ctx.GetUtility<IPoolUtility>(), "MonoPoolUtility 应注册为 IPoolUtility");

            // 转发：经它 Spawn/Despawn 正常路由，归还后入内部停放节点
            var go = monoPool.Spawn(_prefab, _root.transform);
            Assert.IsTrue(go.activeSelf);
            monoPool.Despawn(go);
            var parking = go.transform.parent;
            Assert.IsTrue(parking != null, "Despawn 后应入内部停放子节点");
            var parkingRoot = parking.parent; // [Game.Framework PooledObjects]
            Assert.IsTrue(parkingRoot != null);

            // 销毁宿主 GameObject → OnDestroy → _impl.Dispose() 销毁停放总根，不残留 DontDestroyOnLoad 节点
            UnityEngine.Object.Destroy(poolGo);
            await UniTask.Yield();
            await UniTask.Yield();
            Assert.IsTrue(parkingRoot == null, "MonoPoolUtility 销毁后应 Dispose 底层池、销毁停放总根");
        });

        // ── post-dispose 防护：Dispose 后归还不复活停放根（评审 high）────────
        [UnityTest]
        public IEnumerator PostDispose_Despawn_DestroysInstance_NoResurrect() => UniTask.ToCoroutine(async () =>
        {
            var util = new PoolUtility();
            var pool = util.GetGameObjectPool(_prefab);   // 模拟被 Bag 闭包捕获、Dispose 后仍存活的 GameObjectPool
            var go = pool.Spawn(_root.transform);

            util.Dispose();                                // 释放池工具：标记 disposed（此例尚无停放根可销毁）
            await UniTask.Yield();

            Assert.Throws<ObjectDisposedException>(() => pool.Spawn(_root.transform),
                "旧池句柄在 Utility Dispose 后不得继续发布新实例");
            Assert.Throws<ObjectDisposedException>(() => util.GetGameObjectPool(_prefab),
                "Utility Dispose 后不得重新创建同 prefab 的新池");

            pool.Despawn(go);                              // post-dispose 归还：不应复活停放根
            await UniTask.Yield();
            await UniTask.Yield();

            Assert.IsTrue(go == null, "Dispose 后归还的实例应被直接销毁");
            Assert.AreEqual(0, pool.CountInactive, "Dispose 后归还不入池（不复活停放根）");
        });

        // ── 分帧异步的取消语义（评审 medium）────────────────────────────────
        [UnityTest]
        public IEnumerator Prewarm_Cancellation_StopsPartwayAndKeepsBuilt() => UniTask.ToCoroutine(async () =>
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var cts = new CancellationTokenSource();
            var task = pool.Prewarm(100, perFrame: 1, ct: cts.Token); // 每帧建 1 个
            await UniTask.Yield();
            await UniTask.Yield();
            cts.Cancel();

            var canceled = false;
            try { await task; }
            catch (OperationCanceledException) { canceled = true; }
            Assert.IsTrue(canceled, "取消应抛 OperationCanceledException");

            var built = pool.CountInactive;
            Assert.Greater(built, 0, "取消前已建的实例应留在池中（部分完成，不回滚）");
            Assert.Less(built, 100, "取消应中断，未建满");
            cts.Dispose();
        });

        // ── MonoPoolUtility Inspector 配置预热（评审：核心新增价值此前零覆盖）──
        [UnityTest]
        public IEnumerator MonoPoolUtility_InspectorConfig_PrewarmsOnAwake() => UniTask.ToCoroutine(async () =>
        {
            var ctxGo = new GameObject("Ctx");
            ctxGo.transform.SetParent(_root.transform);
            ctxGo.AddComponent<MonoGameContextBase>();
            await UniTask.Yield(); // ctx 先就绪

            // 先建 inactive 节点挂组件（Awake 不跑），反射注入一条 PrewarmCount>0 的配置，再激活触发 Awake
            var poolGo = new GameObject("MonoPool");
            poolGo.SetActive(false);
            poolGo.transform.SetParent(ctxGo.transform);
            var monoPool = poolGo.AddComponent<MonoPoolUtility>();

            var cfgType = typeof(MonoPoolUtility).GetNestedType("GameObjectPoolConfig");
            var cfg = Activator.CreateInstance(cfgType);
            cfgType.GetField("Prefab").SetValue(cfg, _prefab);
            cfgType.GetField("MaxSize").SetValue(cfg, 0);
            cfgType.GetField("PrewarmCount").SetValue(cfg, 3);
            var listType = typeof(List<>).MakeGenericType(cfgType);
            var list = (IList)Activator.CreateInstance(listType);
            list.Add(cfg);
            typeof(MonoPoolUtility).GetField("_prefabPools", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(monoPool, list);

            poolGo.SetActive(true); // 触发 Awake → ApplyInspectorConfig → 分帧预热
            for (var i = 0; i < 8; i++) await UniTask.Yield(); // 等分帧预热完成（3 个，perFrame=1）

            Assert.AreEqual(3, monoPool.GetGameObjectPool(_prefab).CountInactive,
                "Inspector 配置的 PrewarmCount 应在 Awake 后分帧预热到位");

            UnityEngine.Object.Destroy(poolGo);
            await UniTask.Yield();
        });
    }
}
