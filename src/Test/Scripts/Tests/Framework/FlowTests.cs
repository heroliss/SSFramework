using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Flow;
using Game.Framework.Internal;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证游戏流程状态机（ADR-0023）：每状态一个子 Context（父链解析 / 整棵撤）、
    /// 串行转换与最新意图胜（在途取消 / 排队顶替 / 忽略 ct 的状态正常进入后再退出）、
    /// Enter 失败无状态、OnExit 异常不阻断、一次性实例守卫、宿主 Dispose 收尾。
    /// 状态机自身无 Unity 对象，全部用例不依赖场景与帧推进，batchmode 无风险。
    /// </summary>
    public class FlowTests
    {
        private GameContext _host;
        private GameFlow _flow;
        private HostMarker _hostMarker;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _hostMarker = new HostMarker();
            var builder = new ContainerBuilder();
            _flow = new GameFlow();
            builder.RegisterOwned(_flow, typeof(IGameFlow));
            builder.RegisterValue(_hostMarker, typeof(HostMarker));
            _host = new GameContext(builder.Build(), inheritFromGlobal: false);
        }

        [TearDown]
        public void TearDown() => _host.Dispose();

        // ── 测试辅助 ─────────────────────────────────────────────────────────

        private sealed class HostMarker { }

        private sealed class Probe : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        /// <summary>可脚本化的流程状态：Install / Enter / Exit 行为由用例注入，进出写入共享日志。</summary>
        private sealed class TestState : FlowState
        {
            public string Name;
            public List<string> Log;
            public Action<ContainerBuilder> Install;
            public Func<CancellationToken, UniTask> EnterBody;
            public Func<UniTask> ExitBody;

            public IGameContext Scope => Context;   // 暴露给断言（业务代码不需要这么做）
            public DisposableBag ScopeBagForTest => Bag;

            protected internal override void InstallBindings(ContainerBuilder builder) => Install?.Invoke(builder);

            protected internal override async UniTask OnEnter(CancellationToken ct)
            {
                Log?.Add($"enter:{Name}");
                if (EnterBody != null) await EnterBody(ct);
            }

            protected internal override async UniTask OnExit()
            {
                Log?.Add($"exit:{Name}");
                if (ExitBody != null) await ExitBody();
            }
        }

        private TestState State(string name, Action<ContainerBuilder> install = null,
            Func<CancellationToken, UniTask> enter = null, Func<UniTask> exit = null)
            => new TestState { Name = name, Log = _log, Install = install, EnterBody = enter, ExitBody = exit };

        // 挂起直到 ct 取消（以 OCE 结束）。协作式取消状态的标准姿势。
        private static async UniTask HangUntilCanceled(CancellationToken ct)
        {
            var tcs = new UniTaskCompletionSource();
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
                await tcs.Task;
        }

        // ── 子 Context：父链解析 / 私有注册不外泄 / 整棵撤 ───────────────────

        [Test]
        public void Enter_BuildsScope_ParentResolves_LocalStaysLocal()
        {
            var probe = new Probe();
            var a = State("A", install: b => b.RegisterOwned(probe, typeof(Probe)));

            _flow.GoTo(a); // 全同步状态：GoTo 返回时已进入完成

            Assert.AreSame(a, _flow.Current);
            Assert.IsTrue(_flow.IsIn<TestState>());
            Assert.IsFalse(_flow.IsTransitioning);
            Assert.AreSame(_hostMarker, a.Scope.Resolve(typeof(HostMarker)));      // 子 Context 回退父链
            Assert.AreSame(probe, a.Scope.Resolve(typeof(Probe)));                 // 状态私有注册
            Assert.IsFalse(_host.TryResolve(typeof(Probe), out _));                // 不外泄到宿主
        }

        [Test]
        public void SecondGoTo_ExitsOld_DisposesScopeAndOwned_FiresEvent()
        {
            var events = new List<FlowChangedEvent>();
            _host.RegisterEvent<FlowChangedEvent>(e => events.Add(e));

            var probe = new Probe();
            var bagProbe = new Probe();
            TestState a = null;
            a = State("A",
                install: b => b.RegisterOwned(probe, typeof(Probe)),
                enter: _ =>
                {
                    a.ScopeBagForTest.Add(bagProbe); // 订阅/资源进 Bag 的代表
                    return UniTask.CompletedTask;
                });
            var b2 = State("B");

            _flow.GoTo(a);
            _flow.GoTo(b2);

            CollectionAssert.AreEqual(new[] { "enter:A", "exit:A", "enter:B" }, _log);
            Assert.AreSame(b2, _flow.Current);
            Assert.IsTrue(probe.Disposed, "状态私有 owned 服务应随子 Context 撤除");
            Assert.IsTrue(bagProbe.Disposed, "状态 Bag 应先于子 Context 释放");
            Assert.AreEqual(2, events.Count);
            Assert.IsNull(events[0].From);
            Assert.AreSame(a, events[0].To);
            Assert.AreSame(a, events[1].From);
            Assert.AreSame(b2, events[1].To);
        }

        // ── 转换语义：在途取消 / 排队顶替 / 忽略 ct ──────────────────────────

        [Test]
        public void GoToDuringEnter_CancelsInflight_NoOnExit_LatestWins()
        {
            var probe = new Probe();
            var a = State("A",
                install: b => b.RegisterOwned(probe, typeof(Probe)),
                enter: HangUntilCanceled);
            var b2 = State("B");

            var t1 = _flow.GoTo(a);          // 进入挂起（等 ct）
            Assert.IsTrue(_flow.IsTransitioning);
            var t2 = _flow.GoTo(b2);         // 顶替：取消 A 的在途进入

            Assert.AreEqual(UniTaskStatus.Canceled, t1.Status);
            Assert.AreEqual(UniTaskStatus.Succeeded, t2.Status);
            Assert.AreSame(b2, _flow.Current);
            Assert.IsTrue(probe.Disposed, "半进入状态的子 Context 应整棵撤");
            CollectionAssert.AreEqual(new[] { "enter:A", "enter:B" }, _log); // 半进入不调 OnExit
        }

        [UnityTest]
        public IEnumerator QueueSlotIsOne_MiddleRequestSuperseded_IgnoredCtEntersThenExits() => UniTask.ToCoroutine(async () =>
        {
            var gate = new UniTaskCompletionSource();
            var a = State("A", enter: _ => gate.Task); // 忽略 ct：顶替请求只能排队等它
            var b2 = State("B");
            var c = State("C");

            var t1 = _flow.GoTo(a);
            var t2 = _flow.GoTo(b2);         // 排队
            var t3 = _flow.GoTo(c);          // 顶替 B（排队槽只有一格）

            Assert.AreEqual(UniTaskStatus.Canceled, t2.Status);
            Assert.AreEqual(UniTaskStatus.Pending, t1.Status);

            gate.TrySetResult();             // A 忽略取消跑完：正常进入，随后被排队的 C 正常退出
            await t3;

            Assert.AreEqual(UniTaskStatus.Succeeded, t1.Status);
            Assert.AreSame(c, _flow.Current);
            CollectionAssert.AreEqual(new[] { "enter:A", "exit:A", "enter:C" }, _log);
        });

        // ── 失败语义 ─────────────────────────────────────────────────────────

        [Test]
        public void EnterThrows_CurrentNull_TaskFaults_ScopeDisposed()
        {
            var probe = new Probe();
            var a = State("A",
                install: b => b.RegisterOwned(probe, typeof(Probe)),
                enter: _ => throw new InvalidOperationException("enter-boom"));

            var t = _flow.GoTo(a);

            Assert.AreEqual(UniTaskStatus.Faulted, t.Status);
            Assert.IsNull(_flow.Current, "Enter 失败 = 明确的无状态");
            Assert.IsFalse(_flow.IsTransitioning);
            Assert.IsTrue(probe.Disposed);

            var b2 = State("B");             // 失败后流程仍可用
            _flow.GoTo(b2);
            Assert.AreSame(b2, _flow.Current);
        }

        [Test]
        public void ExitThrows_LoggedAndTransitionContinues()
        {
            var probe = new Probe();
            var a = State("A",
                install: b => b.RegisterOwned(probe, typeof(Probe)),
                exit: () => throw new InvalidOperationException("exit-boom"));
            var b2 = State("B");

            _flow.GoTo(a);
            LogAssert.Expect(LogType.Exception, new Regex("exit-boom"));
            _flow.GoTo(b2);

            Assert.AreSame(b2, _flow.Current);
            Assert.IsTrue(probe.Disposed, "OnExit 抛异常不影响旧子 Context 整棵撤");
        }

        // ── 误用守卫 ─────────────────────────────────────────────────────────

        [Test]
        public void Misuse_NullState_ConsumedState_DetachedFlow_AllThrow()
        {
            Assert.Throws<ArgumentNullException>(() => _flow.GoTo(null));

            var a = State("A");
            _flow.GoTo(a);
            Assert.Throws<ArgumentException>(() => _flow.GoTo(a), "一次性实例复用应抛");

            var detached = new GameFlow(); // 未经 RegisterOwned 注册：无宿主 Context
            Assert.Throws<InvalidOperationException>(() => detached.GoTo(State("X")));
        }

        // ── 宿主 Dispose 收尾 ────────────────────────────────────────────────

        [Test]
        public void HostDispose_DisposesCurrentScope_GoToAfterThrows()
        {
            var probe = new Probe();
            var a = State("A", install: b => b.RegisterOwned(probe, typeof(Probe)));
            _flow.GoTo(a);

            _host.Dispose(); // RegisterOwned：flow 随宿主释放

            Assert.IsTrue(probe.Disposed, "宿主 Dispose 应连带当前状态子 Context 整棵撤");
            Assert.Throws<ObjectDisposedException>(() => _flow.GoTo(State("B")));
        }

        [Test]
        public void HostDispose_DuringEnter_CancelsAndSweepsHalfEnteredScope()
        {
            var probe = new Probe();
            var a = State("A",
                install: b => b.RegisterOwned(probe, typeof(Probe)),
                enter: HangUntilCanceled);

            var t = _flow.GoTo(a);
            _host.Dispose();

            Assert.AreEqual(UniTaskStatus.Canceled, t.Status);
            Assert.IsTrue(probe.Disposed, "在途进入的子 Context 应随宿主 Dispose 撤除");
            CollectionAssert.AreEqual(new[] { "enter:A" }, _log); // 半进入不调 OnExit
        }

        // ── 组合：状态内子 flow（作用域树嵌套） ──────────────────────────────

        [Test]
        public void SubFlow_InStateScope_ShadowsHostFlow_DiesWithState()
        {
            var innerProbe = new Probe();
            GameFlow subFlow = null;
            TestState outer = null;
            outer = State("Outer",
                install: b =>
                {
                    subFlow = new GameFlow(); // 战斗内子阶段机的姿势：状态作用域里再注册一个 flow
                    b.RegisterOwned(subFlow, typeof(IGameFlow));
                },
                enter: _ =>
                {
                    var resolved = (IGameFlow)outer.Scope.Resolve(typeof(IGameFlow));
                    Assert.AreSame(subFlow, resolved, "状态作用域内应解析到子 flow（遮蔽宿主 flow）");
                    return resolved.GoTo(State("Inner", install: bb => bb.RegisterOwned(innerProbe, typeof(Probe))));
                });

            _flow.GoTo(outer);
            Assert.AreSame(outer, _flow.Current);
            Assert.IsTrue(subFlow.IsIn<TestState>());

            _flow.GoTo(State("Elsewhere")); // 退出 Outer：子 flow 连同 Inner 的子 Context 级联撤

            Assert.IsTrue(innerProbe.Disposed, "子 flow 的当前状态应随外层状态整棵撤");
        }
    }
}
