using System.Collections;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Pool;
using Game.Framework.System;
using Game.Framework.Utility;
using NUnit.Framework;
using UnityEngine;
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

        private GameObject _root;     // 所有测试对象的父节点，TearDown 一次性销毁
        private GameObject _prefab;   // 源 prefab（停用，避免在原位置触发 Awake）
        private Transform _parking;   // 直接构造 GameObjectPool 用的 parking 节点（在 _root 下，便于清理）

        [UnitySetUp]
        public IEnumerator SetUp()
        {
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

            LogAssert.Expect(LogType.Error, new Regex("non-pooled"));
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
            builder.RegisterValue(new PoolUtility(), new[] { typeof(IPoolUtility), typeof(IUtility) });
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
            builder.RegisterValue(new PoolUtility(), new[] { typeof(IPoolUtility), typeof(IUtility) });
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

        // ── 归还防护（Release 也生效的短路）─────────────────────────────────

        [Test]
        public void Despawn_Twice_IgnoresSecondAndDoesNotDoublePool()
        {
            var pool = new GameObjectPool(_prefab, _parking);
            var go = pool.Spawn(_root.transform);
            pool.Despawn(go);
            Assert.AreEqual(1, pool.CountInactive);

            LogAssert.Expect(LogType.Error, new Regex("Double-despawn"));
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
            LogAssert.Expect(LogType.Error, new Regex("different pool"));
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
    }
}
