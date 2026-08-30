using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Context;
using Game.Framework.Diagnostics;
using Game.Framework.Internal;
using Game.Framework.Pool;
using Game.Framework.Systems;
using NUnit.Framework;
using UnityEngine.TestTools;
using DisposableBag = Game.Framework.DisposableBag; // 与 R3.DisposableBag 同名，显式指向框架版

namespace Game.Framework.Test
{
    /// <summary>
    /// 框架诊断数据面测试（ADR-0026）：存活 Context 登记表、DebugName、事件订阅计数、Bag 计数、
    /// 池借出计数，以及 LoggingCommandSystem 的流水记录与「装饰不改变执行语义」。
    /// 登记表 / 计数是进程级静态（其他测试也会增减），断言一律用相对差值。
    /// </summary>
    public class DiagnosticsTests
    {
        private static GameContext CreateContext(ICommandSystem commandSystem = null, Container parent = null)
        {
            var builder = new ContainerBuilder();
            if (parent != null) builder.SetParent(parent);
            var model = new TestModel { Value = "diag_test" };
            var system = new TestSystem();
            builder.RegisterValue(model, new[] { typeof(Game.Framework.Model.IModel), typeof(TestModel) });
            builder.RegisterValue(system, new[] { typeof(Game.Framework.Systems.ISystem), typeof(TestSystem) });
            builder.RegisterValue(commandSystem ?? new CommandSystem(), new[] { typeof(ICommandSystem) });
            return new GameContext(builder.Build(), inheritFromGlobal: false);
        }

        // ── 存活 Context 登记表 ─────────────────────────────────────────────
        // 登记表 / 订阅计数 / Bag 计数只在 Editor 采集（ADR-0026），相关测试同样只在 Editor 编译——
        // 真机跑 PlayMode 测试时这些 internal 成员不存在。

#if UNITY_EDITOR
        [Test]
        public void ContextRegistry_TracksCreateAndDispose()
        {
            int before = FrameworkDiagnostics.LiveContexts.Count;

            var ctx = CreateContext();
            Assert.AreEqual(before + 1, FrameworkDiagnostics.LiveContexts.Count);
            Assert.IsTrue(FrameworkDiagnostics.LiveContexts.Contains(ctx));

            ctx.Dispose();
            Assert.AreEqual(before, FrameworkDiagnostics.LiveContexts.Count);
            Assert.IsFalse(FrameworkDiagnostics.LiveContexts.Contains(ctx));

            ctx.Dispose(); // 幂等：二次 Dispose 不再减
            Assert.AreEqual(before, FrameworkDiagnostics.LiveContexts.Count);
        }

        [Test]
        public void ContextRegistry_ParentChainIsRecoverable()
        {
            // 诊断面板经 Container.Parent 链还原作用域树——断言这条链的连通性。
            var parent = CreateContext();
            var child = CreateContext(parent: parent.Container);
            try
            {
                Assert.AreSame(parent.Container, child.Container.Parent);
                Assert.IsTrue(FrameworkDiagnostics.LiveContexts.Contains(parent));
                Assert.IsTrue(FrameworkDiagnostics.LiveContexts.Contains(child));
            }
            finally
            {
                child.Dispose();
                parent.Dispose();
            }
        }

        [Test]
        public void ResolutionEvidence_TracksOnlySuccessfulParentFallbacks()
        {
            using var parentBuilder = new ContainerBuilder();
            parentBuilder.RegisterValue("from-parent", typeof(string));
            using var parent = new GameContext(parentBuilder.Build(), inheritFromGlobal: false)
            {
                DebugName = "Parent",
            };

            using var childBuilder = new ContainerBuilder();
            childBuilder.SetParent(parent.Container);
            childBuilder.RegisterValue(42, typeof(int));
            using var child = new GameContext(childBuilder.Build(), inheritFromGlobal: false)
            {
                DebugName = "Child",
            };

            Assert.AreEqual(42, child.Resolve(typeof(int)), "本地命中不应成为回退证据");
            Assert.IsFalse(child.TryResolve(typeof(Guid), out _), "失败探测不应成为回退证据");
            Assert.AreEqual("from-parent", child.Resolve(typeof(string)));
            Assert.AreEqual("from-parent", child.Resolve(typeof(string)));

            var evidence = child.Container.FallbackResolutionDetails.ToList();
            Assert.That(evidence, Has.Count.EqualTo(1));
            Assert.That(evidence[0].Contract, Is.EqualTo(typeof(string)));
            Assert.That(evidence[0].Source, Is.SameAs(parent.Container));
            Assert.That(evidence[0].Kind, Is.EqualTo(ContainerFallbackKind.ParentChain));
            Assert.That(evidence[0].Count, Is.EqualTo(2));
        }

        [Test]
        public void ResolutionEvidence_MultiLevelFallbackBelongsOnlyToOriginalRequester()
        {
            using var rootBuilder = new ContainerBuilder();
            rootBuilder.RegisterValue("from-root", typeof(string));
            using var root = new GameContext(rootBuilder.Build(), inheritFromGlobal: false);

            using var middleBuilder = new ContainerBuilder();
            middleBuilder.SetParent(root.Container);
            using var middle = new GameContext(middleBuilder.Build(), inheritFromGlobal: false);

            using var leafBuilder = new ContainerBuilder();
            leafBuilder.SetParent(middle.Container);
            using var leaf = new GameContext(leafBuilder.Build(), inheritFromGlobal: false);

            Assert.AreEqual("from-root", leaf.Resolve(typeof(string)));

            var evidence = leaf.Container.FallbackResolutionDetails.Single();
            Assert.That(evidence.Source, Is.SameAs(root.Container));
            Assert.That(evidence.Count, Is.EqualTo(1));
            Assert.That(middle.Container.FallbackResolutionDetails, Is.Empty,
                "递归经过的中间 Container 没有发起解析，不应被重复记账");
            Assert.That(root.Container.FallbackResolutionDetails, Is.Empty);
        }

        [Test]
        public void ResolutionEvidence_TracksParentDependencyResolvedInsideFactory()
        {
            using var parentBuilder = new ContainerBuilder();
            parentBuilder.RegisterValue(7, typeof(int));
            using var parent = new GameContext(parentBuilder.Build(), inheritFromGlobal: false);

            using var childBuilder = new ContainerBuilder();
            childBuilder.SetParent(parent.Container);
            childBuilder.RegisterFactory(
                container => new Version((int)container.Resolve(typeof(int)), 0),
                typeof(Version));
            using var child = new GameContext(childBuilder.Build(), inheritFromGlobal: false);

            Assert.That(child.Resolve(typeof(Version)), Is.EqualTo(new Version(7, 0)));

            var evidence = child.Container.FallbackResolutionDetails.Single();
            Assert.That(evidence.Contract, Is.EqualTo(typeof(int)),
                "应记录工厂真正跨层解析的依赖，而不是本地工厂产物");
            Assert.That(evidence.Source, Is.SameAs(parent.Container));
            Assert.That(evidence.Count, Is.EqualTo(1));
        }

        [Test]
        public void ResolutionEvidence_DistinguishesAllowedFromActualMainFallback()
        {
            GameContext previousMain = GameContext.Main;
            using var mainBuilder = new ContainerBuilder();
            mainBuilder.RegisterValue("from-main", typeof(string));
            using var main = new GameContext(mainBuilder.Build(), inheritFromGlobal: false)
            {
                DebugName = "Main",
            };
            GameContext.Main = main;

            try
            {
                using var childBuilder = new ContainerBuilder();
                using var child = new GameContext(childBuilder.Build(), inheritFromGlobal: true)
                {
                    DebugName = "Orphan",
                };

                Assert.That(child.InheritsFromGlobal, Is.True);
                Assert.That(child.Container.FallbackResolutionDetails, Is.Empty,
                    "允许回退只是策略，尚未解析时不能冒充运行证据");
                Assert.That(child.TryResolve(typeof(Guid), out _), Is.False,
                    "Main 未命中不应产生回退证据");

                Assert.AreEqual("from-main", child.Resolve(typeof(string)));
                Assert.AreEqual("from-main", child.Resolve(typeof(string)));

                using var isolatedBuilder = new ContainerBuilder();
                using var isolated = new GameContext(isolatedBuilder.Build(), inheritFromGlobal: false);
                Assert.That(isolated.TryResolve(typeof(string), out _), Is.False,
                    "显式隔离的 Context 不应查询 Main");
                Assert.That(isolated.Container.FallbackResolutionDetails, Is.Empty);

                var evidence = child.Container.FallbackResolutionDetails.ToList();
                Assert.That(evidence, Has.Count.EqualTo(1));
                Assert.That(evidence[0].Contract, Is.EqualTo(typeof(string)));
                Assert.That(evidence[0].Source, Is.SameAs(main.Container));
                Assert.That(evidence[0].Kind, Is.EqualTo(ContainerFallbackKind.Main));
                Assert.That(evidence[0].Count, Is.EqualTo(2));
            }
            finally
            {
                GameContext.Main = previousMain;
            }
        }

        [Test]
        public void DebugName_DefaultsNullAndSettable()
        {
            var ctx = CreateContext();
            try
            {
                Assert.IsNull(ctx.DebugName);
                ctx.DebugName = "MyScope";
                Assert.AreEqual("MyScope", ctx.DebugName);
            }
            finally { ctx.Dispose(); }
        }

        // ── 事件订阅计数 ────────────────────────────────────────────────────

        [Test]
        public void EventSubscriptionCounts_TrackSubscribeAndDispose()
        {
            var ctx = CreateContext();
            try
            {
                Assert.IsNull(ctx.EventSubscriptionCounts); // 惰性：无订阅不分配

                var d1 = ctx.RegisterEvent<TestEvent>(_ => { });
                var d2 = ctx.RegisterEvent<TestEvent>(_ => { });
                var d3 = ctx.RegisterEvent<EmptyEvent>(_ => { });
                Assert.AreEqual(2, ctx.EventSubscriptionCounts[typeof(TestEvent)]);
                Assert.AreEqual(1, ctx.EventSubscriptionCounts[typeof(EmptyEvent)]);

                d1.Dispose();
                Assert.AreEqual(1, ctx.EventSubscriptionCounts[typeof(TestEvent)]);
                d1.Dispose(); // 幂等：重复退订只减一次
                Assert.AreEqual(1, ctx.EventSubscriptionCounts[typeof(TestEvent)]);

                d2.Dispose();
                d3.Dispose();
                Assert.AreEqual(0, ctx.EventSubscriptionCounts[typeof(TestEvent)]);
                Assert.AreEqual(0, ctx.EventSubscriptionCounts[typeof(EmptyEvent)]);
            }
            finally { ctx.Dispose(); }
        }

        [Test]
        public void EventSubscriptionCounts_CountedSubscriptionStillDelivers()
        {
            // 计数包装不得改变订阅语义：事件照常送达、退订后不再送达。
            var ctx = CreateContext();
            try
            {
                int received = 0;
                var d = ctx.RegisterEvent<TestEvent>(e => received = e.Value);
                ctx.SendEvent(new TestEvent("m", 42));
                Assert.AreEqual(42, received);

                d.Dispose();
                ctx.SendEvent(new TestEvent("m", 99));
                Assert.AreEqual(42, received);
            }
            finally { ctx.Dispose(); }
        }

        // ── DisposableBag 计数 ──────────────────────────────────────────────

        [Test]
        public void BagCounters_TrackCreateAndDispose()
        {
            int aliveBefore = FrameworkDiagnostics.BagsAlive;
            long createdBefore = FrameworkDiagnostics.BagsCreated;

            var bag1 = new DisposableBag();
            var ctx = CreateContext();
            var bag2 = new DisposableBag(ctx);
            Assert.AreEqual(aliveBefore + 2, FrameworkDiagnostics.BagsAlive);
            Assert.AreEqual(createdBefore + 2, FrameworkDiagnostics.BagsCreated);

            bag1.Dispose();
            bag1.Dispose(); // 幂等
            bag2.Dispose();
            ctx.Dispose();
            Assert.AreEqual(aliveBefore, FrameworkDiagnostics.BagsAlive);
            Assert.AreEqual(createdBefore + 2, FrameworkDiagnostics.BagsCreated); // 累计不回退
        }
#endif

        // ── 池借出计数 ──────────────────────────────────────────────────────

        [Test]
        public void ObjectPool_CountActive_TracksRentAndReturn()
        {
            var pool = new ObjectPool<TestModel>(() => new TestModel());
            Assert.AreEqual(0, pool.CountActive);

            var a = pool.Rent();
            var b = pool.Rent();
            Assert.AreEqual(2, pool.CountActive);
            Assert.AreEqual(0, pool.CountInactive);

            pool.Return(a);
            Assert.AreEqual(1, pool.CountActive);
            Assert.AreEqual(1, pool.CountInactive);

            pool.Return(b);
            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(2, pool.CountInactive);
        }

        // ── LoggingCommandSystem ────────────────────────────────────────────

        [Test]
        public void LoggingCommandSystem_SyncCommand_ExecutesAndRecords()
        {
            LoggingCommandSystem.ClearLog();
            var ctx = CreateContext(new LoggingCommandSystem());
            try
            {
                ctx.DebugName = "LogTest";
                ctx.ExecuteCommand(new TestCommand { Name = "logged" });

                Assert.AreEqual("logged", ctx.GetModel<TestModel>().Value); // 语义保持：命令真的执行了

                var entries = new List<LoggingCommandSystem.Entry>();
                LoggingCommandSystem.CopyRecent(entries);
                Assert.AreEqual(1, entries.Count);
                Assert.AreEqual(nameof(TestCommand), entries[0].CommandType);
                Assert.AreEqual("LogTest", entries[0].ContextName);
                Assert.IsNull(entries[0].Error);
                Assert.IsFalse(entries[0].IsAsync);
            }
            finally { ctx.Dispose(); }
        }

        [Test]
        public void LoggingCommandSystem_ResultAndStructPaths_ReturnResults()
        {
            LoggingCommandSystem.ClearLog();
            var ctx = CreateContext(new LoggingCommandSystem());
            try
            {
                string r1 = ctx.ExecuteCommand(new TestResultCommand { Name = "A" });
                string r2 = ctx.ExecuteCommand<TestStructResultCommand, string>(new TestStructResultCommand("B"));
                StringAssert.Contains("A", r1);
                StringAssert.Contains("B", r2);

                var entries = new List<LoggingCommandSystem.Entry>();
                LoggingCommandSystem.CopyRecent(entries);
                Assert.AreEqual(2, entries.Count); // 旧 → 新
                Assert.AreEqual(nameof(TestResultCommand), entries[0].CommandType);
                Assert.AreEqual(nameof(TestStructResultCommand), entries[1].CommandType);
            }
            finally { ctx.Dispose(); }
        }

        [Test]
        public void LoggingCommandSystem_Exception_PropagatesAndRecords()
        {
            LoggingCommandSystem.ClearLog();
            var ctx = CreateContext(new LoggingCommandSystem());
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => ctx.ExecuteCommand(new ThrowingCommand()));

                var entries = new List<LoggingCommandSystem.Entry>();
                LoggingCommandSystem.CopyRecent(entries);
                Assert.AreEqual(1, entries.Count);
                StringAssert.Contains(nameof(InvalidOperationException), entries[0].Error);
            }
            finally { ctx.Dispose(); }
        }

        [UnityTest]
        public IEnumerator LoggingCommandSystem_AsyncCommand_RecordsOnCompletion() => UniTask.ToCoroutine(async () =>
        {
            LoggingCommandSystem.ClearLog();
            var ctx = CreateContext(new LoggingCommandSystem());
            try
            {
                await ctx.ExecuteCommandAsync(new TestAsyncCommand { Name = "async_logged", DelayMs = 30 });
                string echoed = await ctx.ExecuteCommandAsync(new TestAsyncResultCommand { Name = "R", DelayMs = 10 });
                StringAssert.Contains("R", echoed); // 语义保持：返回值原样透传

                var entries = new List<LoggingCommandSystem.Entry>();
                LoggingCommandSystem.CopyRecent(entries);
                Assert.AreEqual(2, entries.Count);
                Assert.IsTrue(entries.All(e => e.IsAsync && e.Error == null));
                Assert.GreaterOrEqual(entries[0].DurationMs, 20f); // 30ms delay 的耗时被计入（留裕量）
            }
            finally { ctx.Dispose(); }
        });

        [UnityTest]
        public IEnumerator LoggingCommandSystem_AsyncCancel_RecordsCanceled() => UniTask.ToCoroutine(async () =>
        {
            LoggingCommandSystem.ClearLog();
            var ctx = CreateContext(new LoggingCommandSystem());
            try
            {
                using var cts = new CancellationTokenSource();
                var task = ctx.ExecuteCommandAsync(new TestAsyncCommand { Name = "never", DelayMs = 5000 }, cts.Token);
                cts.Cancel();
                bool canceled = await task.SuppressCancellationThrow();
                Assert.IsTrue(canceled);

                var entries = new List<LoggingCommandSystem.Entry>();
                LoggingCommandSystem.CopyRecent(entries);
                Assert.AreEqual(1, entries.Count);
                Assert.AreEqual("已取消", entries[0].Error);
            }
            finally { ctx.Dispose(); }
        });

        [Test]
        public void LoggingCommandSystem_RingBuffer_KeepsOnlyRecent()
        {
            LoggingCommandSystem.ClearLog();
            var ctx = CreateContext(new LoggingCommandSystem());
            try
            {
                for (int i = 0; i < LoggingCommandSystem.Capacity + 10; i++)
                    ctx.ExecuteCommand(new TestStructCommand("x"));

                Assert.AreEqual(LoggingCommandSystem.Capacity + 10, LoggingCommandSystem.TotalRecorded);

                var entries = new List<LoggingCommandSystem.Entry>();
                LoggingCommandSystem.CopyRecent(entries);
                Assert.AreEqual(LoggingCommandSystem.Capacity, entries.Count); // 旧的被覆盖

                LoggingCommandSystem.CopyRecent(entries, max: 5);
                Assert.AreEqual(5, entries.Count);
            }
            finally { ctx.Dispose(); }
        }

        /// <summary>测试用：执行即抛，验证装饰器异常透传 + 落账。</summary>
        private sealed class ThrowingCommand : ICommand
        {
            public void Execute(ICommandContext ctx) => throw new InvalidOperationException("boom");
        }
    }
}
