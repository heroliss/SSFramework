using System;
using System.Text.RegularExpressions;
using Game.Framework.Context;
using Game.Framework.Pool;
using Game.Framework.Systems;
using Game.Framework.Utility;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证对象池：ObjectPool 复用/钩子/容量、PoolUtility 按类型管理、DisposableBag.Rent 自动归还。
    /// </summary>
    public class PoolTests
    {
        private sealed class Widget : IPoolable
        {
            public int Value;
            public int RentCount;
            public int ReturnCount;
            public void OnRent() => RentCount++;
            public void OnReturn() { ReturnCount++; Value = 0; }
        }

        private class BaseWidget : IPoolable
        {
            public int RentCount;
            public int ReturnCount;
            public void OnRent() => RentCount++;
            public void OnReturn() => ReturnCount++;
        }

        private sealed class DerivedWidget : BaseWidget
        {
        }

        private sealed class ValueEqualWidget : IPoolable
        {
            public int RentCount;
            public int ReturnCount;
            public void OnRent() => RentCount++;
            public void OnReturn() => ReturnCount++;
            public override bool Equals(object obj) => obj is ValueEqualWidget;
            public override int GetHashCode() => 1;
        }

        [Test]
        public void ObjectPool_RentReturn_ReusesSameInstance()
        {
            var pool = new ObjectPool<Widget>(() => new Widget());
            var a = pool.Rent();
            pool.Return(a);
            var b = pool.Rent();
            Assert.AreSame(a, b);
            Assert.AreEqual(0, pool.CountInactive);
        }

        [Test]
        public void ObjectPool_RentWhenEmpty_CreatesNew()
        {
            var created = 0;
            var pool = new ObjectPool<Widget>(() => { created++; return new Widget(); });
            var a = pool.Rent();
            var b = pool.Rent();
            Assert.AreNotSame(a, b);
            Assert.AreEqual(2, created);
        }

        [Test]
        public void ObjectPool_ValueEqualReferences_AreIndependentLeases()
        {
            var pool = new ObjectPool<ValueEqualWidget>(() => new ValueEqualWidget());

            var a = pool.Rent();
            var b = pool.Rent();

            Assert.AreNotSame(a, b);
            Assert.IsTrue(a.Equals(b), "前置条件：两个独立引用在业务值语义上相等");
            Assert.AreEqual(2, pool.CountActive, "池所有权必须按引用身份，而不是 Equals/GetHashCode 跟踪");

            pool.Return(a);
            pool.Return(b);
            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(2, pool.CountInactive);
        }

        [Test]
        public void ObjectPool_FactoryNullOrRepeatedReference_FailsWithoutPublishingLease()
        {
            var nullPool = new ObjectPool<Widget>(() => null);
            Assert.Throws<InvalidOperationException>(() => nullPool.Rent());
            Assert.AreEqual(0, nullPool.CountActive);
            Assert.AreEqual(0, nullPool.CountInactive);

            var singleton = new Widget();
            var singletonPool = new ObjectPool<Widget>(() => singleton);
            Assert.AreSame(singleton, singletonPool.Rent());
            Assert.Throws<InvalidOperationException>(() => singletonPool.Rent(),
                "factory 不能把仍处于活动 lease 的同一引用再次发布给另一个 owner");
            Assert.AreEqual(1, singletonPool.CountActive);
            Assert.AreEqual(0, singletonPool.CountInactive);

            singletonPool.Return(singleton);
        }

        [Test]
        public void ObjectPool_Poolable_HooksInvokedAndStateCleared()
        {
            var pool = new ObjectPool<Widget>(() => new Widget());
            var w = pool.Rent();
            Assert.AreEqual(1, w.RentCount);

            w.Value = 42;
            pool.Return(w);
            Assert.AreEqual(1, w.ReturnCount);
            Assert.AreEqual(0, w.Value, "OnReturn 应清理状态");

            var w2 = pool.Rent();
            Assert.AreSame(w, w2);
            Assert.AreEqual(2, w2.RentCount);
        }

        [Test]
        public void ObjectPool_RentHookFailure_RollsBackAndDiscardsDirtyInstance()
        {
            var failure = new ApplicationException("rent failed");
            var created = 0;
            Widget dirty = null;
            var pool = new ObjectPool<Widget>(
                () => dirty = new Widget { Value = ++created },
                onRent: _ => throw failure);

            var thrown = Assert.Throws<ApplicationException>(() => pool.Rent());

            Assert.AreSame(failure, thrown, "应保留最初租借异常，而不是用补偿路径改写失败原因");
            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(0, pool.CountInactive, "经历过失败激活的实例不得回到可复用栈");
            Assert.AreEqual(1, dirty.ReturnCount, "租借失败也要执行一次 best-effort 归还清理");
            Assert.AreEqual(0, dirty.Value, "IPoolable.OnReturn 应参与租借失败补偿");
        }

        [Test]
        public void ObjectPool_ReturnHookFailure_CompletesCleanupAndDiscardsDirtyInstance()
        {
            var failure = new ApplicationException("return failed");
            var shouldFail = true;
            var pool = new ObjectPool<Widget>(
                () => new Widget(),
                onReturn: _ =>
                {
                    if (shouldFail) throw failure;
                });
            var dirty = pool.Rent();
            dirty.Value = 42;

            var thrown = Assert.Throws<ApplicationException>(() => pool.Return(dirty));

            Assert.AreSame(failure, thrown);
            Assert.AreEqual(1, dirty.ReturnCount, "委托失败不能跳过 IPoolable.OnReturn");
            Assert.AreEqual(0, dirty.Value);
            Assert.AreEqual(0, pool.CountActive, "归还所有权必须在用户清理回调前关闭");
            Assert.AreEqual(0, pool.CountInactive, "清理失败的脏实例不得再复用");

            shouldFail = false;
            var fresh = pool.Rent();
            Assert.AreNotSame(dirty, fresh);
            pool.Return(fresh);
        }

        [Test]
        public void ObjectPool_OnRentReentry_IsRejectedWithoutPublishingAlias()
        {
            ObjectPool<Widget> pool = null;
            pool = new ObjectPool<Widget>(
                () => new Widget(),
                onRent: instance => pool.Return(instance));

            LogAssert.Expect(LogType.Error, new Regex("重入归还"));
            var first = pool.Rent();
            LogAssert.Expect(LogType.Error, new Regex("重入归还"));
            var second = pool.Rent();

            Assert.AreNotSame(first, second, "Renting 实例不得被钩子提前压栈并再次发布");
            Assert.AreEqual(2, pool.CountActive);
            Assert.AreEqual(0, pool.CountInactive);
        }

        [Test]
        public void ObjectPool_OnReturnReentry_IsRejectedWithoutDuplicateInactiveAlias()
        {
            ObjectPool<Widget> pool = null;
            pool = new ObjectPool<Widget>(
                () => new Widget(),
                onReturn: instance => pool.Return(instance));
            var instance = pool.Rent();

            LogAssert.Expect(LogType.Error, new Regex("重入归还"));
            pool.Return(instance);

            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(1, pool.CountInactive, "同一引用只能在空闲栈中出现一次");
            Assert.AreSame(instance, pool.Rent());
            Assert.AreEqual(0, pool.CountInactive);
        }

        [Test]
        public void ObjectPool_UnityObjectType_LogsMisuseGuard()
        {
            // GameObject 满足 class/new() 约束也能进 C# 对象池，但这里不 Instantiate/SetActive——几乎必然是误用。
            // 建池时一次性 LogError 指路 GameObject 池（Bag.Spawn / IPoolUtility.Spawn）。
            LogAssert.Expect(LogType.Error, new Regex("UnityEngine.Object"));
            _ = new ObjectPool<GameObject>(() => new GameObject());
        }

        [Test]
        public void ObjectPool_Prewarm_PopulatesInactive()
        {
            var pool = new ObjectPool<Widget>(() => new Widget());
            pool.Prewarm(3);
            Assert.AreEqual(3, pool.CountInactive);
        }

        [Test]
        public void ObjectPool_PrewarmFactoryReentry_DoesNotExceedMaxSizeAtCommit()
        {
            ObjectPool<Widget> pool = null;
            var reentered = false;
            pool = new ObjectPool<Widget>(
                () =>
                {
                    var instance = new Widget();
                    if (!reentered)
                    {
                        reentered = true;
                        pool.Prewarm(1); // 内层先占满唯一 idle 槽。
                    }
                    return instance;
                },
                maxSize: 1);

            pool.Prewarm(1);

            Assert.AreEqual(1, pool.CountInactive,
                "factory 重入填满容量后，外层 Prewarm 的未发布实例必须在提交点丢弃，不能突破 maxSize");
        }

        [Test]
        public void ObjectPool_MaxSize_DropsBeyondCap()
        {
            var pool = new ObjectPool<Widget>(() => new Widget(), maxSize: 1);
            var a = pool.Rent();
            var b = pool.Rent();
            pool.Return(a);
            pool.Return(b); // 超过 cap=1，应被丢弃而非入池
            Assert.AreEqual(1, pool.CountInactive);
        }

        [Test]
        public void ObjectPool_Trim_ShrinksToTarget()
        {
            var pool = new ObjectPool<Widget>(() => new Widget());
            pool.Prewarm(5);
            Assert.AreEqual(5, pool.CountInactive);

            pool.Trim(2);
            Assert.AreEqual(2, pool.CountInactive, "Trim 应把空闲收缩到 targetCount");

            pool.Trim(0);
            Assert.AreEqual(0, pool.CountInactive, "Trim(0) 应清空");
        }

        [Test]
        public void PoolUtility_RentReturn_UsesDefaultPool()
        {
            IPoolUtility util = new PoolUtility();
            var a = util.Rent<Widget>();
            util.Return(a);
            var b = util.Rent<Widget>();
            Assert.AreSame(a, b);
        }

        [Test]
        public void PoolUtility_GetPool_SameInstancePerType()
        {
            IPoolUtility util = new PoolUtility();
            Assert.AreSame(util.GetPool<Widget>(), util.GetPool<Widget>());
        }

        [Test]
        public void PoolUtility_ReturnAfterUpcast_RoutesToActualSourcePool()
        {
            var util = new PoolUtility();
            var sourcePool = util.GetPool<DerivedWidget>();
            var derived = sourcePool.Rent();
            BaseWidget upcast = derived;

            util.Return(upcast);

            Assert.AreEqual(0, sourcePool.CountActive);
            Assert.AreEqual(1, sourcePool.CountInactive);
            LogAssert.Expect(LogType.Error, new Regex("没有活动来源记录"));
            util.Return(upcast);
            Assert.AreEqual(1, sourcePool.CountInactive, "重复归还不得再次触达来源池");
            Assert.AreSame(derived, sourcePool.Rent(),
                "Return<T> 应按实例引用的来源路由，而不是调用点静态类型 T 猜池");
            Assert.AreEqual(1, util.GetPoolDiagnostics().Count,
                "上转型归还不应顺带创建 BaseWidget 池");

            sourcePool.Return(derived);
            util.Dispose();
        }

        [Test]
        public void Bag_Rent_AutoReturnsOnDispose()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());

            var pool = ctx.GetUtility<IPoolUtility>().GetPool<Widget>();

            Widget rented;
            using (var bag = ctx.CreateBag())
            {
                rented = bag.Rent<Widget>();
                Assert.AreEqual(0, pool.CountInactive, "租出后池中应无空闲实例");
            }

            Assert.AreEqual(1, pool.CountInactive, "bag.Dispose 应自动把租借的实例归还到池");
            var again = ctx.GetUtility<IPoolUtility>().Rent<Widget>();
            Assert.AreSame(rented, again, "归还的实例应被下次租借复用");
        }

        [Test]
        public void Bag_Return_ReleasesSingleEarly()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());
            var pool = ctx.GetUtility<IPoolUtility>().GetPool<Widget>();

            Widget a, b;
            using (var bag = ctx.CreateBag())
            {
                a = bag.Rent<Widget>();
                b = bag.Rent<Widget>();

                bag.Return(a);
                Assert.AreEqual(1, pool.CountInactive, "提前归还 a 后池中应有 1 个空闲");
                Assert.AreEqual(1, a.ReturnCount, "a 应被归还一次");
                Assert.AreEqual(0, b.ReturnCount, "b 仍被 bag 持有");
            }

            Assert.AreEqual(2, pool.CountInactive, "bag.Dispose 应只归还剩余的 b");
            Assert.AreEqual(1, a.ReturnCount, "a 不应被 Dispose 重复归还");
            Assert.AreEqual(1, b.ReturnCount);
        }

        [Test]
        public void Bag_Return_ForeignOrAlreadyReturned_LogsErrorAndIgnores()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());
            var pool = ctx.GetUtility<IPoolUtility>().GetPool<Widget>();

            using var bag = ctx.CreateBag();

            // 外来实例：不是本 bag 借出的
            LogAssert.Expect(LogType.Error, new Regex("并非由此 Bag 租出"));
            bag.Return(new Widget());

            // 重复归还：第二次已不在登记表，忽略且不触达池
            var a = bag.Rent<Widget>();
            bag.Return(a);
            LogAssert.Expect(LogType.Error, new Regex("并非由此 Bag 租出"));
            bag.Return(a);
            Assert.AreEqual(1, a.ReturnCount, "重复 Return 不应真正归还第二次");
            Assert.AreEqual(1, pool.CountInactive);
        }

        [Test]
        public void Bag_Return_ThenRerent_Roundtrip()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());
            var pool = ctx.GetUtility<IPoolUtility>().GetPool<Widget>();

            Widget a;
            using (var bag = ctx.CreateBag())
            {
                a = bag.Rent<Widget>();
                bag.Return(a);
                var again = bag.Rent<Widget>();
                Assert.AreSame(a, again, "提前归还的实例应被同 bag 再次租出");
                Assert.AreEqual(0, pool.CountInactive);
            }

            Assert.AreEqual(1, pool.CountInactive, "Dispose 应归还重新登记的实例，恰好一次");
            Assert.AreEqual(2, a.ReturnCount, "两轮租借各归还一次，无重复");
        }

        [Test]
        public void Bag_Return_AfterDispose_IsNoOp()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());
            var pool = ctx.GetUtility<IPoolUtility>().GetPool<Widget>();

            var bag = ctx.CreateBag();
            var a = bag.Rent<Widget>();
            bag.Dispose();   // a 随 Dispose 自动归还
            Assert.AreEqual(1, pool.CountInactive);

            bag.Return(a);   // 已 Dispose 的 bag：静默无操作——实例已归还，不报错也不重复归还
            Assert.AreEqual(1, a.ReturnCount, "Dispose 后 Return 不应重复归还");
            Assert.AreEqual(1, pool.CountInactive);
        }

        [Test]
        public void Bag_RentWhenOnRentDisposesBag_DoesNotPublishReturnedInstance()
        {
            var util = new PoolUtility();
            DisposableBag bag = null;
            var pool = util.GetPool<Widget>(
                () => new Widget(),
                onRent: _ => bag.Dispose());
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(util, typeof(IPoolUtility));
            using var ctx = new GameContext(builder.Build());
            bag = ctx.CreateBag();

            Assert.Throws<ObjectDisposedException>(() => bag.Rent<Widget>(),
                "OnRent 关闭 bag 后，Rent 不得把已经补偿归还的实例发布给调用方");
            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(1, pool.CountInactive);

            var recovered = pool.Rent();
            Assert.AreEqual(2, recovered.RentCount, "补偿归还后的实例仍可由存活的池重新租借");
            pool.Return(recovered);
            util.Dispose();
        }

        [Test]
        public void PoolUtility_Dispose_TerminatesNewWorkButAllowsTerminalReturn()
        {
            var util = new PoolUtility();
            var pool = util.GetPool<Widget>();
            pool.Prewarm(3);
            Assert.AreEqual(3, pool.CountInactive);
            var active = pool.Rent();
            Assert.AreEqual(1, pool.CountActive);

            util.Dispose();

            Assert.AreEqual(0, pool.CountInactive, "Dispose 应立即清除旧池的 idle 缓存");
            Assert.Throws<ObjectDisposedException>(() => pool.Rent(), "旧池句柄不得在终止后继续发布 lease");
            Assert.Throws<ObjectDisposedException>(() => util.GetPool<Widget>(),
                "Dispose 后不得通过 Utility 复活一个同类型新池");

            util.Return(active);
            Assert.AreEqual(1, active.ReturnCount, "Dispose 前已发布的 lease 仍要完成一次 terminal Return 清理");
            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(0, pool.CountInactive, "terminal Return 只清理并丢弃，不得复活 idle 缓存");

            util.Dispose(); // 幂等：再次 Dispose 不抛、不二次释放
        }
    }
}
