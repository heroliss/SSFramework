using Game.Framework.Context;
using Game.Framework.Pool;
using Game.Framework.System;
using Game.Framework.Utility;
using NUnit.Framework;

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
            builder.RegisterValue(new PoolUtility(), new[] { typeof(IPoolUtility), typeof(IUtility) });
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
    }
}
