using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Systems;
using NUnit.Framework;
using R3;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 测试 DisposableBag：invokeImmediately、OnEvent&lt;T&gt;() 桥接、ReactiveProperty 订阅、Dispose 行为、异常隔离、嵌套 child bag。
    /// 不覆盖资源加载方法（依赖 YooAsset 初始化，参见 YooAssetLoadTests）。
    /// </summary>
    public class DisposableBagTests
    {
        private GameContext _ctx;
        private DisposableBag _bag;

        [SetUp]
        public void SetUp()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            _ctx = new GameContext(builder.Build());
            _bag = new DisposableBag(_ctx);
        }

        [TearDown]
        public void TearDown()
        {
            _bag?.Dispose();
            _ctx?.Dispose();
        }

        // ─── invokeImmediately：Framework Event 无数据重载 ──────────────────

        [Test]
        public void Subscribe_FrameworkEventNoData_Default_DoesNotFireOnSubscribe()
        {
            var count = 0;
            _bag.Subscribe<EmptyEvent>(() => count++);
            Assert.AreEqual(0, count, "默认不应在订阅时触发");

            _ctx.SendEvent<EmptyEvent>();
            Assert.AreEqual(1, count);
        }

        [Test]
        public void Subscribe_FrameworkEventNoData_InvokeImmediately_FiresOnceOnSubscribe()
        {
            var count = 0;
            _bag.Subscribe<EmptyEvent>(() => count++, invokeImmediately: true);
            Assert.AreEqual(1, count, "应在订阅后立即触发一次");

            _ctx.SendEvent<EmptyEvent>();
            Assert.AreEqual(2, count, "之后事件继续触发");
        }

        // ─── invokeImmediately：UnityEvent 无数据重载 ──────────────────────

        [Test]
        public void Subscribe_UnityEventNoData_Default_DoesNotFireOnSubscribe()
        {
            var evt = new UnityEvent();
            var count = 0;
            _bag.Subscribe(evt, () => count++);
            Assert.AreEqual(0, count);

            evt.Invoke();
            Assert.AreEqual(1, count);
        }

        [Test]
        public void Subscribe_UnityEventNoData_InvokeImmediately_FiresOnceOnSubscribe()
        {
            var evt = new UnityEvent();
            var count = 0;
            _bag.Subscribe(evt, () => count++, invokeImmediately: true);
            Assert.AreEqual(1, count);

            evt.Invoke();
            Assert.AreEqual(2, count);
        }

        [Test]
        public void Subscribe_UnityEventNoData_NullEvent_LogsErrorAndDoesNotFire()
        {
            // 设计决策：evt 为 null 视为"无订阅源"，连带 init 也跳过——不在空对象上凭空触发 handler；
            // 同时 Editor/Dev 下 LogError（Inspector 漏配应尽早暴露，规则「Inspector 引用默认 fail-fast」），Release 下容忍。
            LogAssert.Expect(LogType.Error, new Regex("null UnityEvent"));
            var count = 0;
            _bag.Subscribe((UnityEvent)null, () => count++, invokeImmediately: true);
            Assert.AreEqual(0, count);
        }

        // ─── OnEvent<T>() 桥接 ───────────────────────────────────────────

        [Test]
        public void OnEvent_BasicSubscribe_ReceivesEvent()
        {
            var sys = AttachSystem();
            string received = null;
            _bag.Subscribe(sys.OnEvent<TestEvent>(), e => received = e.Message);

            _ctx.SendEvent(new TestEvent("hello", 0));
            Assert.AreEqual("hello", received);
        }

        [Test]
        public void OnEvent_Prepend_FiresSeedImmediatelyThenLiveEvents()
        {
            var sys = AttachSystem();
            var received = new List<string>();
            _bag.Subscribe(
                sys.OnEvent<TestEvent>().Prepend(new TestEvent("seed", -1)),
                e => received.Add(e.Message));

            Assert.AreEqual(1, received.Count, "Prepend 应在订阅时立即推种子值");
            Assert.AreEqual("seed", received[0]);

            _ctx.SendEvent(new TestEvent("after", 0));
            Assert.AreEqual(2, received.Count);
            Assert.AreEqual("after", received[1]);
        }

        [Test]
        public void OnEvent_WhereFilter_OnlyMatchingPasses()
        {
            var sys = AttachSystem();
            var received = new List<int>();
            _bag.Subscribe(
                sys.OnEvent<TestEvent>().Where(e => e.Value > 10),
                e => received.Add(e.Value));

            _ctx.SendEvent(new TestEvent("a", 5));
            _ctx.SendEvent(new TestEvent("b", 20));
            _ctx.SendEvent(new TestEvent("c", 100));

            CollectionAssert.AreEqual(new[] { 20, 100 }, received);
        }

        [Test]
        public void OnEvent_DisposeBag_StopsReceiving()
        {
            var sys = AttachSystem();
            var count = 0;
            _bag.Subscribe(sys.OnEvent<TestEvent>(), _ => count++);

            _ctx.SendEvent(new TestEvent("first", 0));
            Assert.AreEqual(1, count);

            _bag.Dispose();
            _ctx.SendEvent(new TestEvent("second", 0));
            Assert.AreEqual(1, count, "DisposableBag Dispose 后不应再接收");
        }

        [Test]
        public void OnEvent_MultipleSubscribers_EachIndependent()
        {
            var sys = AttachSystem();
            int a = 0, b = 0;
            _bag.Subscribe(sys.OnEvent<TestEvent>(), _ => a++);
            _bag.Subscribe(sys.OnEvent<TestEvent>(), _ => b++);

            _ctx.SendEvent(new TestEvent("x", 0));
            Assert.AreEqual(1, a);
            Assert.AreEqual(1, b);
        }

        // ─── ReactiveProperty 订阅即得 current value（R3 内置行为）── 回归检查 ───

        [Test]
        public void Subscribe_ReactiveProperty_FiresCurrentValueOnSubscribe()
        {
            var rp = new ReactiveProperty<int>(42);
            var received = 0;
            _bag.Subscribe(rp, v => received = v);

            Assert.AreEqual(42, received, "RP 订阅时 R3 自动推 current value");
        }

        [Test]
        public void Subscribe_ReactivePropertyWithSkip1_SkipsInitialValue()
        {
            // 想跳过 RP 初值的官方写法：.Skip(1)
            var rp = new ReactiveProperty<int>(42);
            var received = -1;
            _bag.Subscribe(rp.Skip(1), v => received = v);

            Assert.AreEqual(-1, received, "Skip(1) 后订阅时不应推 current value");

            rp.Value = 100;
            Assert.AreEqual(100, received);
        }

        [UnityTest]
        public IEnumerator Subscribe_Debounce_UsesPlayerLoopTimer_AndEmitsLastValue()
        {
            var rp = new RP<int>(0);
            _bag.Add(rp);
            var received = -1;
            _bag.Subscribe(
                rp.Skip(1).Debounce(TimeSpan.FromMilliseconds(30)),
                value => received = value);

            rp.Value = 1;
            rp.Value = 2;

            yield return new WaitForSecondsRealtime(0.1f);

            Assert.AreEqual(2, received, "PlayerLoop 防抖应只发出静默期前的最后一个值");
        }

        // ─── child bag 级联 ──────────────────────────────────────────────

        [Test]
        public void CreateChild_DisposingParent_DisposesChild()
        {
            var child = _bag.CreateChild();
            int count = 0;
            child.Subscribe<EmptyEvent>(() => count++);

            _ctx.SendEvent<EmptyEvent>();
            Assert.AreEqual(1, count);

            _bag.Dispose();
            _ctx.SendEvent<EmptyEvent>();
            Assert.AreEqual(1, count, "父 bag Dispose 应级联到 child");
            Assert.IsTrue(child.IsDisposed);
        }

        [Test]
        public void CreateChild_DisposingChildIndependently_DoesNotAffectParent()
        {
            var child = _bag.CreateChild();
            int childCount = 0, parentCount = 0;
            child.Subscribe<EmptyEvent>(() => childCount++);
            _bag.Subscribe<EmptyEvent>(() => parentCount++);

            child.Dispose();
            _ctx.SendEvent<EmptyEvent>();

            Assert.AreEqual(0, childCount, "child Dispose 后不再接收");
            Assert.AreEqual(1, parentCount, "child Dispose 不影响 parent");
            Assert.IsFalse(_bag.IsDisposed);
        }

        [Test]
        public void CreateChild_ParentDisposeCascades_ChildToken_AndAddedDisposables()
        {
            // 验证父级 Dispose 是真级联——不仅停事件传播，还触发 child.DisposeToken 取消 + child.Add 的 IDisposable 释放
            var child = _bag.CreateChild();
            var addedDisposed = false;
            child.Add(Disposable.Create(() => addedDisposed = true));

            // 先快照 token（DisposableBag.Dispose 会同时 Dispose 底层 CTS，导致访问 bag.DisposeToken 抛 ObjectDisposedException）
            var childToken = child.DisposeToken;
            Assert.IsFalse(childToken.IsCancellationRequested);

            _bag.Dispose();

            Assert.IsTrue(child.IsDisposed, "parent.Dispose 后 child 应已 dispose");
            Assert.IsTrue(childToken.IsCancellationRequested,
                "parent.Dispose 应级联取消 child.DisposeToken（用于资源加载链路）");
            Assert.IsTrue(addedDisposed, "parent.Dispose 应级联释放 child.Add 登记的 IDisposable");
        }

        [Test]
        public void CreateChild_ChildDispose_DoesNotCancelParentToken()
        {
            var child = _bag.CreateChild();

            // 先快照 token 副本，Dispose 后再访问 bag.DisposeToken 会抛 ObjectDisposedException
            var childToken = child.DisposeToken;
            var parentToken = _bag.DisposeToken;

            child.Dispose();

            Assert.IsTrue(childToken.IsCancellationRequested,
                "child.Dispose 应取消自己的 DisposeToken");
            Assert.IsFalse(parentToken.IsCancellationRequested,
                "child.Dispose 不应影响 parent.DisposeToken");
        }

        [Test]
        public void CreateChild_OnDisposedParent_Throws()
        {
            _bag.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => _bag.CreateChild(),
                "已 dispose 的 parent 上调 CreateChild 应抛 ObjectDisposedException");
        }

        // ─── Add(IDisposable) ───────────────────────────────────────────

        [Test]
        public void Add_RegistersDisposableAndReleasesOnDispose()
        {
            var trackedDisposed = false;
            var d = Disposable.Create(() => trackedDisposed = true);
            _bag.Add(d);

            _bag.Dispose();
            Assert.IsTrue(trackedDisposed, "Add 登记的 IDisposable 应在 bag.Dispose 时释放");
        }

        [Test]
        public void Dispose_WhenTrackedDisposableThrows_ContinuesWithRemainingCleanup()
        {
            var order = new List<string>();
            _bag.Add(Disposable.Create(() =>
            {
                order.Add("throwing");
                throw new InvalidOperationException("tracked dispose failed");
            }));
            _bag.Add(Disposable.Create(() => order.Add("remaining")));

            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.DoesNotThrow(() => _bag.Dispose());
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            CollectionAssert.AreEqual(new[] { "throwing", "remaining" }, order,
                "单个 IDisposable 失败不能让 CompositeDisposable 在半路停止");
        }

        [Test]
        public void Dispose_WhenCancellationCallbackThrows_StillReleasesTrackedItems()
        {
            var trackedDisposed = false;
            _bag.DisposeToken.Register(() => throw new InvalidOperationException("cancel callback failed"));
            _bag.Add(Disposable.Create(() => trackedDisposed = true));

            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.DoesNotThrow(() => _bag.Dispose());
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            Assert.IsTrue(trackedDisposed,
                "CancellationToken 回调异常不能阻断 DisposableBag 的其余资源释放");
        }

        // ─── 辅助 ─────────────────────────────────────────────────────────

        private TestSystem AttachSystem()
        {
            var sys = new TestSystem();
            _ctx.AttachTo(sys);
            return sys;
        }
    }
}
