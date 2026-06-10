using System.Text.RegularExpressions;
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
        public void ObjectPool_Prewarm_PopulatesInactive()
        {
            var pool = new ObjectPool<Widget>(() => new Widget());
            pool.Prewarm(3);
            Assert.AreEqual(3, pool.CountInactive);
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
            LogAssert.Expect(LogType.Error, new Regex("not leased by this bag"));
            bag.Return(new Widget());

            // 重复归还：第二次已不在登记表，忽略且不触达池
            var a = bag.Rent<Widget>();
            bag.Return(a);
            LogAssert.Expect(LogType.Error, new Regex("not leased by this bag"));
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
        public void PoolUtility_Dispose_ClearsPools_AndIsIdempotent()
        {
            var util = new PoolUtility();
            var pool = util.GetPool<Widget>();
            pool.Prewarm(3);
            Assert.AreEqual(3, pool.CountInactive);

            util.Dispose();

            // Dispose 后再取池：_pools 已清，返回新空池；Editor/Dev 下伴随一条 use-after-dispose 诊断
            LogAssert.Expect(LogType.Error, new Regex("after Dispose"));
            var fresh = util.GetPool<Widget>();
            Assert.AreNotSame(pool, fresh, "Dispose 应清 _pools，再取是新池实例");
            Assert.AreEqual(0, fresh.CountInactive, "Dispose 后再取应是空池");

            util.Dispose(); // 幂等：再次 Dispose 不抛、不二次释放
        }
    }
}
