using System;
using System.Collections;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Systems;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证 Command / Event handler 抛异常时的隔离行为：
    /// - 同步 Command 抛异常应当上抛到调用方（不吞）
    /// - 异步 Command 抛异常应当 await 时重新抛出
    /// - 多 Event handler 之一抛异常时，其他 handler 行为如何（R3 默认会把异常传给 OnErrorResume）
    /// - Inject 解析不到时输出明确日志，不静默
    /// </summary>
    public class ExceptionIsolationTests
    {
        private sealed class ThrowingCommand : ICommand
        {
            public void Execute(ICommandContext ctx)
                => throw new InvalidOperationException("intentional sync throw");
        }

        private sealed class ThrowingAsyncCommand : IAsyncCommand
        {
            public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken ct)
            {
                await UniTask.Yield(ct);
                throw new InvalidOperationException("intentional async throw");
            }
        }

        private GameContext _ctx;

        [SetUp]
        public void SetUp()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            _ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
        }

        [TearDown]
        public void TearDown()
        {
            _ctx?.Dispose();
            _ctx = null;
        }

        // ── 同步 Command 异常上抛 ────────────────────────────────────────

        [Test]
        public void SyncCommand_ThrowsInsideExecute_PropagatesToCaller()
        {
            Assert.Throws<InvalidOperationException>(
                () => _ctx.ExecuteCommand(new ThrowingCommand()),
                "同步 Command 内抛出的异常应当透出到 ExecuteCommand 调用方");
        }

        [Test]
        public void SyncCommand_ExceptionDoesNotPoisonContext()
        {
            try { _ctx.ExecuteCommand(new ThrowingCommand()); }
            catch (InvalidOperationException) { /* expected */ }

            // 抛过的 Context 仍可继续用，不会进入"半坏"状态
            int counter = 0;
            using var sub = _ctx.RegisterEvent<TestEvent>(_ => counter++);
            _ctx.SendEvent(new TestEvent("post-throw", 1));
            Assert.AreEqual(1, counter, "Command 抛异常后 Context 仍应正常工作");
        }

        // ── 异步 Command 异常上抛 ────────────────────────────────────────

        [UnityTest]
        public IEnumerator AsyncCommand_ThrowsInsideExecute_PropagatesToAwaiter()
            => UniTask.ToCoroutine(async () =>
            {
                Exception caught = null;
                try { await _ctx.ExecuteCommandAsync(new ThrowingAsyncCommand()); }
                catch (Exception ex) { caught = ex; }

                Assert.IsNotNull(caught, "异步 Command 内抛出的异常应当 await 时重新抛出");
                Assert.IsInstanceOf<InvalidOperationException>(caught,
                    "异常类型应保留为原始 InvalidOperationException");
            });

        // ── Event handler 异常隔离 ───────────────────────────────────────

        [Test]
        public void EventHandler_OneThrows_OthersStillReceiveLaterSends()
        {
            // R3 Subject 在 handler 抛异常时不会自动断开订阅者，但具体行为依赖 R3 配置。
            // 实测：handler 抛 → 异常通过 R3 默认错误处理（UniTaskScheduler.UnobservedTaskException
            // 或控制台 log），其他订阅者仍接收后续 SendEvent。
            // 这里只验证"后续 SendEvent 仍能投递到正常 handler"——异常输出本身允许，但不能让流死掉。

            int safeCount = 0;
            using var bad = _ctx.RegisterEvent<TestEvent>(_ => throw new InvalidOperationException("bad handler"));
            using var safe = _ctx.RegisterEvent<TestEvent>(_ => safeCount++);

            // 第一次 SendEvent：bad handler 抛，R3 会通过 OnErrorResume 走默认处理
            // 我们不关心异常去哪儿（R3 控制），只关心 safe handler 至少被调用一次
            LogAssert.ignoreFailingMessages = true;
            _ctx.SendEvent(new TestEvent("a", 1));
            _ctx.SendEvent(new TestEvent("b", 2));
            LogAssert.ignoreFailingMessages = false;

            Assert.GreaterOrEqual(safeCount, 1,
                "另一个正常订阅者至少应当收到一次事件——异常 handler 不应让整个 Subject 停摆");
        }

        [Test]
        public void EventHandler_SecondSendStillReachesSafeHandler()
        {
            // 更明确的隔离场景：先注册 safe handler 验证基线，再加 bad handler，
            // 验证 bad handler 抛异常不会影响后续 SendEvent 投递到 safe handler。
            int safeCount = 0;
            using var safe = _ctx.RegisterEvent<TestEvent>(_ => safeCount++);

            _ctx.SendEvent(new TestEvent("baseline", 0));
            Assert.AreEqual(1, safeCount, "baseline: safe handler 应当收到第一个事件");

            using var bad = _ctx.RegisterEvent<TestEvent>(_ => throw new InvalidOperationException());

            LogAssert.ignoreFailingMessages = true;
            _ctx.SendEvent(new TestEvent("after-bad", 1));
            LogAssert.ignoreFailingMessages = false;

            Assert.GreaterOrEqual(safeCount, 2,
                "添加 bad handler 后，后续 SendEvent 仍应至少投递到 safe handler 一次");
        }

        // ── Inject 解析失败 ──────────────────────────────────────────────

        private sealed class CommandWithMissingDep : ICommand
        {
            [Inject] public TestSystem Missing;
            public void Execute(ICommandContext ctx)
            {
                // 故意访问可能为 null 的字段，演示"注入失败后 Execute 继续运行"
                _ = Missing?.CallCount;
            }
        }

        [Test]
        public void Inject_MissingDependency_LogsWarningAndContinues()
        {
            // InjectionPlan 解析不到时是 Debug.LogWarning（不抛）：
            // Command 仍会 Execute，业务自行处理 null。
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Inject\] Cannot resolve"));
            Assert.DoesNotThrow(
                () => _ctx.ExecuteCommand(new CommandWithMissingDep()),
                "Inject 失败应当输出 Warning 后继续执行 Command，由业务自己处理 null");
        }
    }
}
