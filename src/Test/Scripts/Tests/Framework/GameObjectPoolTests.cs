using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
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
            var ctx = new GameContext(builder.Build());
            var pool = ctx.GetUtility<IPoolUtility>().GetGameObjectPool(_prefab);

            var bag = ctx.CreateBag();
            var go = bag.Spawn(_prefab, _root.transform);
            UnityEngine.Object.Destroy(go);
            yield return null;   // Destroy 帧末生效，等一帧让实例变 fake null

            bag.Despawn(go);     // 实例已死：登记被摘除，归还句柄的 null 守卫跳过入池，不报错
            Assert.AreEqual(0, pool.CountInactive, "已销毁实例不应入池");

            bag.Dispose();       // 登记已摘除：不产生重复 Despawn / 死实例归还的错误日志
            Assert.AreEqual(0, pool.CountInactive);
            ctx.Dispose();
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
