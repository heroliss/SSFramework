using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Flow;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.Systems;
using Game.Framework.Utility;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证游戏流程状态机（ADR-0023）：每状态一个子 Context（父链解析 / 整棵撤）、
    /// 串行转换与最新意图胜（在途取消 / 排队顶替 / 忽略 ct 的状态正常进入后再退出）、
    /// 成功事件跳过未完成候选但保留最后已发布来源、Enter 失败无状态、OnExit 异常不阻断、
    /// 一次性实例守卫、宿主在 Enter / Exit 期间 Dispose 的任务终态与整棵撤收尾。
    /// 状态机自身无 Unity 对象；仅迟到任务观察用例等待一帧交付 continuation，其余不依赖场景或帧推进，batchmode 无风险。
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
            builder.RegisterOwnedSystem(_flow);
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
            public int DisposeCount;
            public int DisposeThread;

            public void Dispose()
            {
                Disposed = true;
                DisposeCount++;
                DisposeThread = Thread.CurrentThread.ManagedThreadId;
            }
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
        public void Contract_IsSystemAndDoesNotExposeGoToThroughUtilityPermission()
        {
            Assert.IsTrue(typeof(ISystem).IsAssignableFrom(typeof(IGameFlow)));
            Assert.IsFalse(typeof(IUtility).IsAssignableFrom(typeof(IGameFlow)),
                "View 与 Utility 不应通过宽权限 GetUtility 取得可写的游戏流程");
        }

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
        public IEnumerator GoToDuringEnter_CancellationCallbackThrows_StillReturnsTaskAndContinues()
            => UniTask.ToCoroutine(async () =>
            {
                var previousSinks = new List<ILogSink>(Log.Sinks);
                var previousMinLevel = Log.MinLevel;
                var sink = new CapturingSink();
                var enterGate = new UniTaskCompletionSource();
                var cancellationFailure = new InvalidOperationException("go-to-cancel-callback-boom");
                var probe = new Probe();
                bool enterReleased = false;

                try
                {
                    Log.ClearSinks();
                    Log.AddSink(sink);
                    Log.MinLevel = LogLevel.Trace;

                    var a = State("A",
                        install: b => b.RegisterOwned(probe, typeof(Probe)),
                        enter: ct =>
                        {
                            ct.Register(() => throw cancellationFailure);
                            return enterGate.Task; // 故意忽略取消，验证 Cancel 抛错后 flow 仍保留既定等待语义
                        });

                    var first = _flow.GoTo(a);
                    UniTask latest = default;
                    Assert.DoesNotThrow(() => latest = _flow.GoTo(State("B")),
                        "取消回调异常应进入日志，不能让 GoTo 在已经建立 pending request 后从同步调用点抛出");
                    Assert.AreEqual(UniTaskStatus.Pending, first.Status);
                    Assert.AreEqual(UniTaskStatus.Pending, latest.Status);

                    enterReleased = enterGate.TrySetResult();
                    await latest;

                    Assert.AreEqual(UniTaskStatus.Succeeded, first.Status,
                        "忽略 token 的 A 按既有契约先完成进入，再由排队的 B 正常退出它");
                    Assert.IsTrue(probe.Disposed, "取消回调报错不能阻断 A 最终退出时的 scope 清理");
                    Assert.AreEqual(1, sink.Entries.Count);
                    Assert.AreEqual("GameFlow", sink.Entries[0].Category);
                    Assert.AreSame(cancellationFailure, sink.Entries[0].Exception.InnerException);
                }
                finally
                {
                    if (!enterReleased) enterGate.TrySetResult();
                    Log.ClearSinks();
                    foreach (var previousSink in previousSinks) Log.AddSink(previousSink);
                    Log.MinLevel = previousMinLevel;
                }
            });

        [Test]
        public void GoToDuringEnter_FinalEventKeepsLastPublishedStateAsFrom()
        {
            var events = new List<FlowChangedEvent>();
            using var subscription = _host.RegisterEvent<FlowChangedEvent>(e => events.Add(e));
            var a = State("A");
            var b2 = State("B", enter: HangUntilCanceled);
            var c = State("C");

            _flow.GoTo(a);
            var superseded = _flow.GoTo(b2);
            var latest = _flow.GoTo(c);

            Assert.AreEqual(UniTaskStatus.Canceled, superseded.Status);
            Assert.AreEqual(UniTaskStatus.Succeeded, latest.Status);
            Assert.AreSame(c, _flow.Current);
            Assert.AreEqual(2, events.Count, "未完整进入的 B 不应发布 FlowChangedEvent");
            Assert.AreSame(a, events[1].From,
                "连续转换应从最后一个已发布状态 A 直接报告到 C，而不是伪造 null → C");
            Assert.AreSame(c, events[1].To);
        }

        [Test]
        public void SuccessfulEnter_CleansTokenBeforeEventHandlerReentersGoTo()
        {
            int cancellationCallbacks = 0;
            UniTask nextTask = default;
            bool reentered = false;
            TestState a = null;
            var b2 = State("B");
            a = State("A", enter: ct =>
            {
                ct.Register(() => cancellationCallbacks++);
                return UniTask.CompletedTask;
            });
            using var subscription = _host.RegisterEvent<FlowChangedEvent>(evt =>
            {
                if (!ReferenceEquals(evt.To, a)) return;
                reentered = true;
                nextTask = _flow.GoTo(b2);
            });

            UniTask firstTask = _flow.GoTo(a);

            Assert.IsTrue(reentered);
            Assert.AreEqual(UniTaskStatus.Succeeded, firstTask.Status);
            Assert.AreEqual(UniTaskStatus.Succeeded, nextTask.Status);
            Assert.Zero(cancellationCallbacks,
                "完整进入的 token 必须先摘除再发事件；正常切到 B 不能回头取消已提交的 A。");
            Assert.AreSame(b2, _flow.Current);
            CollectionAssert.AreEqual(new[] { "enter:A", "exit:A", "enter:B" }, _log);
        }

        [UnityTest]
        public IEnumerator GoToDuringExit_FinalEventKeepsDepartedStateAsFrom() => UniTask.ToCoroutine(async () =>
        {
            var exitGate = new UniTaskCompletionSource();
            var events = new List<FlowChangedEvent>();
            using var subscription = _host.RegisterEvent<FlowChangedEvent>(e => events.Add(e));
            var a = State("A", exit: () => exitGate.Task);
            var b2 = State("B");
            var c = State("C");

            await _flow.GoTo(a);
            var superseded = _flow.GoTo(b2); // 卡在 A.OnExit
            var latest = _flow.GoTo(c);      // B 尚未进入便被 C 顶替

            Assert.AreEqual(UniTaskStatus.Pending, superseded.Status);
            exitGate.TrySetResult();
            await latest;

            Assert.AreEqual(UniTaskStatus.Canceled, superseded.Status);
            Assert.AreSame(c, _flow.Current);
            Assert.AreEqual(2, events.Count, "从未进入的 B 不应发布 FlowChangedEvent");
            Assert.AreSame(a, events[1].From,
                "A 已退出但仍是本轮连续转换最后一个已发布状态，最终事件应报告 A → C");
            Assert.AreSame(c, events[1].To);
        });

        [UnityTest]
        public IEnumerator EnterFailsWithPendingIntent_FinalEventKeepsLastPublishedStateAsFrom() => UniTask.ToCoroutine(async () =>
        {
            var enterGate = new UniTaskCompletionSource();
            var events = new List<FlowChangedEvent>();
            using var subscription = _host.RegisterEvent<FlowChangedEvent>(e => events.Add(e));
            var a = State("A");
            var broken = State("Broken", enter: async _ =>
            {
                await enterGate.Task; // 刻意忽略 flow 的取消 token，模拟物理操作最终失败
                throw new InvalidOperationException("pending-enter-boom");
            });
            var c = State("C");

            await _flow.GoTo(a);
            var failed = _flow.GoTo(broken);
            var latest = _flow.GoTo(c);
            enterGate.TrySetResult();

            Exception error = null;
            try { await failed; }
            catch (Exception e) { error = e; }

            Assert.IsInstanceOf<InvalidOperationException>(error);
            Assert.AreEqual("pending-enter-boom", error.Message);
            await latest;

            Assert.AreEqual(2, events.Count);
            Assert.AreSame(a, events[1].From,
                "同一轮连续转换里的失败候选不应切断最后一个已发布来源");
            Assert.AreSame(c, events[1].To);
        });

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

        [UnityTest]
        public IEnumerator PendingCancellationContinuation_ReentrantGoToRemainsLatest()
            => UniTask.ToCoroutine(async () =>
            {
                var exitGate = new UniTaskCompletionSource();
                var a = State("A", exit: () => exitGate.Task);
                var b2 = State("B");
                var c = State("C");
                var displaced = State("Displaced");
                var latest = State("LatestFromCancellation");
                UniTask latestTask = default;
                bool reentered = false;

                async UniTask ReenterAfterCancellation(UniTask superseded)
                {
                    try
                    {
                        await superseded;
                        Assert.Fail("排队中的 C 应被后续意图顶替。");
                    }
                    catch (OperationCanceledException)
                    {
                        reentered = true;
                        latestTask = _flow.GoTo(latest);
                    }
                }

                await _flow.GoTo(a);
                UniTask active = _flow.GoTo(b2);          // 卡在 A.OnExit
                UniTask superseded = _flow.GoTo(c);       // 单格 pending
                UniTask observer = ReenterAfterCancellation(superseded);
                UniTask displacedTask = _flow.GoTo(displaced); // 取消 C；其 continuation 重入 Latest

                Assert.IsTrue(reentered, "UniTaskCompletionSource 的取消 continuation 应同步交付。");
                Assert.AreEqual(UniTaskStatus.Canceled, superseded.Status);
                Assert.AreEqual(UniTaskStatus.Canceled, displacedTask.Status,
                    "取消 continuation 中的请求才是最终意图，外层请求必须被正常顶替。");

                exitGate.TrySetResult();
                await observer;
                await UniTask.Yield();

                Assert.AreEqual(UniTaskStatus.Canceled, active.Status);
                Assert.AreEqual(UniTaskStatus.Succeeded, latestTask.Status,
                    "重入产生的最新请求不能被外层 GoTo 覆盖后永久 Pending。");
                Assert.AreSame(latest, _flow.Current);
            });

        [UnityTest]
        public IEnumerator PendingCancellationContinuation_AdvancingEnterCannotCancelNewOwner()
            => UniTask.ToCoroutine(async () =>
            {
                var firstEnterGate = new UniTaskCompletionSource();
                var latestEnterGate = new UniTaskCompletionSource();
                CancellationToken latestToken = default;
                var first = State("First", enter: _ => firstEnterGate.Task);
                var queued = State("Queued");
                var displaced = State("Displaced");
                var latest = State("Latest", enter: ct =>
                {
                    latestToken = ct;
                    return latestEnterGate.Task;
                });
                UniTask latestTask = default;

                async UniTask ReenterAndAdvance(UniTask superseded)
                {
                    try
                    {
                        await superseded;
                        Assert.Fail("排队请求应被外层请求顶替。");
                    }
                    catch (OperationCanceledException)
                    {
                        latestTask = _flow.GoTo(latest);
                        firstEnterGate.TrySetResult();
                    }
                }

                UniTask firstTask = _flow.GoTo(first);       // OnEnter 忽略 token，等待 gate
                UniTask queuedTask = _flow.GoTo(queued);     // pending
                UniTask observer = ReenterAndAdvance(queuedTask);
                UniTask displacedTask = _flow.GoTo(displaced); // 取消 queued，continuation 内发布并推进 latest

                await observer;
                Assert.AreEqual(UniTaskStatus.Canceled, displacedTask.Status,
                    "continuation 中发布的 latest 应正常顶替外层 displaced。");
                Assert.AreEqual(UniTaskStatus.Pending, latestTask.Status,
                    "外层旧 GoTo 返回后不能取消已被 runner 消费的新 owner。");
                Assert.IsTrue(latestToken.CanBeCanceled);
                Assert.IsFalse(latestToken.IsCancellationRequested);

                latestEnterGate.TrySetResult();
                await latestTask;

                Assert.AreEqual(UniTaskStatus.Succeeded, firstTask.Status,
                    "忽略取消的 First 按既有契约完整进入后再退出。");
                Assert.AreSame(latest, _flow.Current);
            });

        [Test]
        public void InstallBindings_ReentrantGoToSkipsStaleEnterAndDisposesBuiltScope()
        {
            var probe = new Probe();
            int staleEnterCalls = 0;
            UniTask latestTask = default;
            var latest = State("Latest");
            var stale = State("Stale",
                install: builder =>
                {
                    builder.RegisterOwned(probe, typeof(Probe));
                    latestTask = _flow.GoTo(latest);
                },
                enter: _ =>
                {
                    staleEnterCalls++;
                    return UniTask.CompletedTask;
                });

            UniTask staleTask = _flow.GoTo(stale);

            Assert.AreEqual(UniTaskStatus.Canceled, staleTask.Status);
            Assert.AreEqual(UniTaskStatus.Succeeded, latestTask.Status);
            Assert.Zero(staleEnterCalls,
                "InstallBindings 期间出现更新意图后，旧 scope 只能回滚，不能继续调用 OnEnter。");
            Assert.AreEqual(1, probe.DisposeCount);
            Assert.AreSame(latest, _flow.Current);
        }

        [Test]
        public void InstallBindings_ReentrantHostDisposeSkipsEnterAndReleasesOwnedOnce()
        {
            var probe = new Probe();
            int enterCalls = 0;
            var state = State("DisposedDuringInstall",
                install: builder =>
                {
                    builder.RegisterOwned(probe, typeof(Probe));
                    _host.Dispose();
                },
                enter: _ =>
                {
                    enterCalls++;
                    return UniTask.CompletedTask;
                });

            UniTask task = _flow.GoTo(state);

            Assert.AreEqual(UniTaskStatus.Canceled, task.Status);
            Assert.Zero(enterCalls);
            Assert.AreEqual(1, probe.DisposeCount,
                "宿主在构建回调中释放时，Builder 或临时 scope 只能有一个 owner 完成回滚。");
            Assert.IsFalse(_flow.IsTransitioning);
        }

        [UnityTest]
        public IEnumerator WorkerCompletedHooks_PublishAndDisposeOnMainThread()
            => UniTask.ToCoroutine(async () =>
            {
                int mainThread = Thread.CurrentThread.ManagedThreadId;
                int enterWorker = -1;
                int exitWorker = -1;
                var eventThreads = new List<int>();
                var probe = new Probe();
                using var subscription = _host.RegisterEvent<FlowChangedEvent>(_ =>
                    eventThreads.Add(Thread.CurrentThread.ManagedThreadId));
                var a = State("A",
                    install: builder => builder.RegisterOwned(probe, typeof(Probe)),
                    enter: async _ =>
                    {
                        await UniTask.SwitchToThreadPool();
                        enterWorker = Thread.CurrentThread.ManagedThreadId;
                    },
                    exit: async () =>
                    {
                        await UniTask.SwitchToThreadPool();
                        exitWorker = Thread.CurrentThread.ManagedThreadId;
                    });

                await _flow.GoTo(a);
                Assert.AreNotEqual(mainThread, enterWorker);
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "OnEnter 在 worker 完成后，GoTo awaiter 必须恢复 Unity 主线程。");
                Assert.AreEqual(mainThread, eventThreads[0]);

                await _flow.GoTo(State("B"));
                Assert.AreNotEqual(mainThread, exitWorker);
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "OnExit 在 worker 完成后，后续状态提交与 GoTo awaiter 必须回主线程。");
                Assert.AreEqual(mainThread, probe.DisposeThread,
                    "状态 Context / owned 资源只能在主线程撤除。");
                Assert.That(eventThreads, Has.All.EqualTo(mainThread));
            });

        [UnityTest]
        public IEnumerator WorkerEnterFailure_FaultAndRollbackReturnToMainThread()
            => UniTask.ToCoroutine(async () =>
            {
                int mainThread = Thread.CurrentThread.ManagedThreadId;
                int failureThread = -1;
                var probe = new Probe();
                var failure = new InvalidOperationException("worker-enter-boom");
                var broken = State("Broken",
                    install: builder => builder.RegisterOwned(probe, typeof(Probe)),
                    enter: async _ =>
                    {
                        await UniTask.SwitchToThreadPool();
                        failureThread = Thread.CurrentThread.ManagedThreadId;
                        throw failure;
                    });

                try
                {
                    await _flow.GoTo(broken);
                    Assert.Fail("worker OnEnter 失败必须传播。");
                }
                catch (InvalidOperationException error)
                {
                    Assert.AreSame(failure, error);
                    Assert.AreNotEqual(mainThread, failureThread);
                    Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
                }

                Assert.AreEqual(mainThread, probe.DisposeThread);
                Assert.IsNull(_flow.Current);
                Assert.IsFalse(_flow.IsTransitioning);
            });

        // ── 失败语义 ─────────────────────────────────────────────────────────

        [Test]
        public void InstallBindingsThrows_TaskFaults_PreBuildOwnedDisposed_FlowRemainsUsable()
        {
            var probe = new Probe();
            var broken = State("Broken", install: builder =>
            {
                builder.RegisterOwned(probe, typeof(Probe));
                throw new InvalidOperationException("install-boom");
            });

            var task = _flow.GoTo(broken);

            Assert.AreEqual(UniTaskStatus.Faulted, task.Status);
            var error = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            StringAssert.Contains("install-boom", error.Message);
            Assert.IsTrue(probe.Disposed, "Build 前失败应由 using Builder 回滚状态私有 owned 服务");
            Assert.IsNull(_flow.Current);
            Assert.IsFalse(_flow.IsTransitioning);

            var healthy = State("Healthy");
            Assert.DoesNotThrow(() => _flow.GoTo(healthy).GetAwaiter().GetResult());
            Assert.AreSame(healthy, _flow.Current, "一次 Install 失败不能毒化流程转换循环");
        }

        [Test]
        public void EnterThrows_CurrentNull_TaskFaults_ScopeDisposed()
        {
            var events = new List<FlowChangedEvent>();
            using var subscription = _host.RegisterEvent<FlowChangedEvent>(e => events.Add(e));
            var probe = new Probe();
            var a = State("A",
                install: b => b.RegisterOwned(probe, typeof(Probe)),
                enter: _ => throw new InvalidOperationException("enter-boom"));

            var t = _flow.GoTo(a);

            Assert.AreEqual(UniTaskStatus.Faulted, t.Status);

            // ⚠ 必须把这个异常「观测」掉。UniTask 对 faulted 却从未被 await 的任务，会在 GC 时经
            // UniTaskScheduler.UnobservedTaskException 兜底 Debug.LogException——**时机不确定**，
            // 于是这条 enter-boom 会落到当时恰好在跑的**其它**用例头上，让无辜用例以
            // 「Unhandled log message」失败（实际坑到过 BattleSimRegressionTests）。
            // GetResult() 既消费掉异常（标记已观测），又顺带断言了异常类型——比单看 Status 更严格。
            Assert.Throws<InvalidOperationException>(() => t.GetAwaiter().GetResult());

            Assert.IsNull(_flow.Current, "Enter 失败 = 明确的无状态");
            Assert.IsFalse(_flow.IsTransitioning);
            Assert.IsTrue(probe.Disposed);

            var b2 = State("B");             // 失败后流程仍可用
            _flow.GoTo(b2);
            Assert.AreSame(b2, _flow.Current);
            Assert.AreEqual(1, events.Count);
            Assert.IsNull(events[0].From,
                "失败已经结束并稳定处于无状态后，后来另起的转换应明确从 null 开始");
            Assert.AreSame(b2, events[0].To);
        }

        [Test]
        public void EnterCancelsWithoutFlowRequest_IsReportedAsFailureInsteadOfNormalSupersession()
        {
            var probe = new Probe();
            var unexpectedCancellation = new OperationCanceledException("provider-canceled-itself");
            var broken = State("UnexpectedCancel",
                install: b => b.RegisterOwned(probe, typeof(Probe)),
                enter: _ => throw unexpectedCancellation);

            var task = _flow.GoTo(broken);

            Assert.AreEqual(UniTaskStatus.Faulted, task.Status,
                "只有 GameFlow 自己请求的顶替/销毁取消才属于正常控制流");
            var error = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            StringAssert.Contains("GameFlow 未请求取消", error.Message);
            Assert.IsInstanceOf<OperationCanceledException>(error.InnerException,
                "UniTask 的 async builder 会把业务 OCE 规范化为取消结果，框架应保留取消类型而非承诺对象身份");
            Assert.IsTrue(probe.Disposed, "非预期取消同样属于进入失败，半建状态作用域必须撤除");
            Assert.IsNull(_flow.Current);
            Assert.IsFalse(_flow.IsTransitioning);

            var healthy = State("Healthy");
            Assert.DoesNotThrow(() => _flow.GoTo(healthy).GetAwaiter().GetResult());
            Assert.AreSame(healthy, _flow.Current, "一次下游自发取消不能毒化后续转换");
        }

        [Test]
        public void ExitThrows_UsesLoggingSeamAndTransitionContinues()
        {
            var previousSinks = new List<ILogSink>(Log.Sinks);
            var previousMinLevel = Log.MinLevel;
            var sink = new CapturingSink();
            var probe = new Probe();
            var a = State("A",
                install: b => b.RegisterOwned(probe, typeof(Probe)),
                exit: () => throw new InvalidOperationException("exit-boom"));
            var b2 = State("B");

            try
            {
                Log.ClearSinks();
                Log.AddSink(sink);
                Log.MinLevel = LogLevel.Trace;

                _flow.GoTo(a);
                _flow.GoTo(b2);

                Assert.AreSame(b2, _flow.Current);
                Assert.IsTrue(probe.Disposed, "OnExit 抛异常不影响旧子 Context 整棵撤");
                Assert.AreEqual(1, sink.Entries.Count);
                Assert.AreEqual(LogLevel.Error, sink.Entries[0].Level);
                Assert.AreEqual("GameFlow", sink.Entries[0].Category);
                StringAssert.Contains(nameof(TestState), sink.Entries[0].Message);
                StringAssert.Contains("流程清理将继续", sink.Entries[0].Message);
                Assert.AreEqual("exit-boom", sink.Entries[0].Exception.Message);
            }
            finally
            {
                Log.ClearSinks();
                foreach (var previousSink in previousSinks) Log.AddSink(previousSink);
                Log.MinLevel = previousMinLevel;
            }
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

        [UnityTest]
        public IEnumerator HostDispose_DuringExit_CancelsAcceptedTasksAndSweepsExitingScope() => UniTask.ToCoroutine(async () =>
        {
            var previousSinks = new List<ILogSink>(Log.Sinks);
            var previousMinLevel = Log.MinLevel;
            var sink = new CapturingSink();
            var exitGate = new UniTaskCompletionSource();
            var lateFailure = new InvalidOperationException("late-exit-boom");
            var probe = new Probe();
            var events = new List<FlowChangedEvent>();
            bool exitReleased = false;

            try
            {
                Log.ClearSinks();
                Log.AddSink(sink);
                Log.MinLevel = LogLevel.Trace;

                using var subscription = _host.RegisterEvent<FlowChangedEvent>(e => events.Add(e));
                var a = State("A",
                    install: b => b.RegisterOwned(probe, typeof(Probe)),
                    exit: () => exitGate.Task);

                await _flow.GoTo(a);
                var active = _flow.GoTo(State("B")); // 卡在 A.OnExit；A 已不再是 Current
                var pending = _flow.GoTo(State("C")); // 退出期间的最新意图，占据单格 pending
                Assert.AreEqual(UniTaskStatus.Pending, active.Status);
                Assert.AreEqual(UniTaskStatus.Pending, pending.Status);
                Assert.IsTrue(_flow.IsTransitioning);

                _host.Dispose();

                Assert.AreEqual(UniTaskStatus.Canceled, active.Status,
                    "宿主释放必须让 active GoTo 立即到达取消终态，不能被无 token 的 OnExit 永久拖住");
                Assert.AreEqual(UniTaskStatus.Canceled, pending.Status,
                    "尚在单格队列中的最新 GoTo 也必须由 flow owner 立即取消");
                Assert.IsTrue(probe.Disposed,
                    "正在退出的状态也仍归 flow 所有，宿主释放必须立即撤掉它的子 Context");
                Assert.IsFalse(_flow.IsTransitioning, "Dispose 后对外不应继续报告仍在转换");

                exitReleased = exitGate.TrySetException(lateFailure);
                await UniTask.DelayFrame(1);

                Assert.AreEqual(1, events.Count, "迟到的 OnExit 完成不得再进入 B 或发布新的 FlowChangedEvent");
                Assert.AreSame(a, events[0].To);
                CollectionAssert.AreEqual(new[] { "enter:A", "exit:A" }, _log,
                    "B/C 都不应开始进入，迟到 OnExit 只能结束自己的物理任务");
                Assert.AreEqual(UniTaskStatus.Canceled, active.Status);
                Assert.AreEqual(UniTaskStatus.Canceled, pending.Status);
                Assert.AreEqual(1, sink.Entries.Count,
                    "宿主已释放后迟到的 OnExit 异常仍应被 owner 观察并进入统一日志，而不是成为无人观察异常");
                Assert.AreEqual("GameFlow", sink.Entries[0].Category);
                Assert.AreSame(lateFailure, sink.Entries[0].Exception);
            }
            finally
            {
                if (!exitReleased) exitGate.TrySetResult();
                Log.ClearSinks();
                foreach (var previousSink in previousSinks) Log.AddSink(previousSink);
                Log.MinLevel = previousMinLevel;
            }
        });

        [UnityTest]
        public IEnumerator HostDispose_ReenteredSynchronouslyFromExit_StillOwnsLateFailure()
            => UniTask.ToCoroutine(async () =>
            {
                var previousSinks = new List<ILogSink>(Log.Sinks);
                var previousMinLevel = Log.MinLevel;
                var sink = new CapturingSink();
                var exitGate = new UniTaskCompletionSource();
                var lateFailure = new InvalidOperationException("reentrant-late-exit-boom");
                var probe = new Probe();
                bool exitReleased = false;

                try
                {
                    Log.ClearSinks();
                    Log.AddSink(sink);
                    Log.MinLevel = LogLevel.Trace;

                    var a = State("A",
                        install: b => b.RegisterOwned(probe, typeof(Probe)),
                        exit: () =>
                        {
                            _host.Dispose(); // OnExit 同步前缀重入 Dispose，再返回一个会迟到失败的物理任务
                            return exitGate.Task;
                        });

                    await _flow.GoTo(a);
                    var transition = _flow.GoTo(State("B"));

                    Assert.AreEqual(UniTaskStatus.Canceled, transition.Status);
                    Assert.IsTrue(probe.Disposed);
                    Assert.IsFalse(_flow.IsTransitioning);

                    exitReleased = exitGate.TrySetException(lateFailure);
                    await UniTask.DelayFrame(1);

                    Assert.AreEqual(1, sink.Entries.Count,
                        "即使 OnExit 在 Attach 建立前同步重入 Dispose，迟到异常仍必须由物理 owner 观察");
                    Assert.AreEqual("GameFlow", sink.Entries[0].Category);
                    Assert.AreSame(lateFailure, sink.Entries[0].Exception);
                }
                finally
                {
                    if (!exitReleased) exitGate.TrySetResult();
                    Log.ClearSinks();
                    foreach (var previousSink in previousSinks) Log.AddSink(previousSink);
                    Log.MinLevel = previousMinLevel;
                }
            });

        [UnityTest]
        public IEnumerator HostDispose_DuringEnter_CancellationCallbackThrows_StillSweepsScope()
            => UniTask.ToCoroutine(async () =>
            {
                var previousSinks = new List<ILogSink>(Log.Sinks);
                var previousMinLevel = Log.MinLevel;
                var sink = new CapturingSink();
                var enterGate = new UniTaskCompletionSource();
                var cancellationFailure = new InvalidOperationException("dispose-cancel-callback-boom");
                var probe = new Probe();
                bool enterReleased = false;

                try
                {
                    Log.ClearSinks();
                    Log.AddSink(sink);
                    Log.MinLevel = LogLevel.Trace;

                    var a = State("A",
                        install: b => b.RegisterOwned(probe, typeof(Probe)),
                        enter: ct =>
                        {
                            ct.Register(() => throw cancellationFailure);
                            return enterGate.Task; // 不响应 token，scope sweep 必须由 Dispose 主动完成
                        });

                    var transition = _flow.GoTo(a);
                    _host.Dispose();

                    Assert.AreEqual(UniTaskStatus.Canceled, transition.Status);
                    Assert.IsTrue(probe.Disposed,
                        "取消回调抛错后仍必须继续撤半进入状态，不能因 GameFlow.Dispose 已幂等锁死而永久泄漏");
                    Assert.IsFalse(_flow.IsTransitioning);
                    Assert.AreEqual(1, sink.Entries.Count);
                    Assert.AreEqual("GameFlow", sink.Entries[0].Category);
                    StringAssert.Contains("取消回调执行失败", sink.Entries[0].Message);
                    StringAssert.Contains("作用域清理将继续", sink.Entries[0].Message);
                    Assert.AreSame(cancellationFailure, sink.Entries[0].Exception.InnerException);

                    enterReleased = enterGate.TrySetResult();
                    await UniTask.DelayFrame(1); // 让迟到 OnEnter continuation 做幂等收尾，不能产生额外日志
                    Assert.AreEqual(1, sink.Entries.Count);
                }
                finally
                {
                    if (!enterReleased) enterGate.TrySetResult();
                    Log.ClearSinks();
                    foreach (var previousSink in previousSinks) Log.AddSink(previousSink);
                    Log.MinLevel = previousMinLevel;
                }
            });

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
                    b.RegisterOwnedSystem(subFlow);
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

        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }
    }
}
