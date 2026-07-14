using System;
using System.Collections;
using Game.Framework.Context;
using Game.Framework.Systems;
using Game.Framework.Common;
using Game.Framework.Internal;
using Game.Framework.Command;
using NUnit.Framework;
using UnityEngine.TestTools;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Test
{
    /// <summary>
    /// 测试 Command 执行：同步/异步、有/无返回值、[Inject]注入、struct Command
    /// </summary>
    public class CommandTests
    {
        private GameContext _gameContext;
        private TestModel _testModel;
        private TestSystem _testSystem;

        [SetUp]
        public void SetUp()
        {
            _testModel = new TestModel { Value = "cmd_test" };
            _testSystem = new TestSystem();

            var builder = new ContainerBuilder();
            builder.RegisterValue(_testModel, new[] { typeof(Game.Framework.Model.IModel), typeof(TestModel) });
            builder.RegisterValue(_testSystem, new[] { typeof(Game.Framework.Systems.ISystem), typeof(TestSystem) });
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            _gameContext = new GameContext(builder.Build());

            _gameContext.Inject(_testSystem);
            _gameContext.AttachTo(_testSystem);
        }

        [TearDown]
        public void TearDown() => _gameContext?.Dispose();

        [Test]
        public void SyncCommand_ShouldExecuteAndModifyModel()
        {
            _gameContext.ExecuteCommand(new TestCommand { Name = "sync_test" });

            Assert.AreEqual("sync_test", _testModel.Value);
            Assert.AreEqual(1, _testModel.Counter);
        }

        [Test]
        public void SyncCommand_WithReturnValue_ShouldReturnCorrectResult()
        {
            var result = _gameContext.ExecuteCommand(new TestResultCommand { Name = "Tester" });

            Assert.AreEqual("Hello Tester, model value: cmd_test", result);
        }

        [Test]
        public void SyncCommand_MultipleExecutions_ShouldAccumulate()
        {
            _gameContext.ExecuteCommand(new TestCommand { Name = "first" });
            _gameContext.ExecuteCommand(new TestCommand { Name = "second" });
            _gameContext.ExecuteCommand(new TestCommand { Name = "third" });

            Assert.AreEqual("third", _testModel.Value);
            Assert.AreEqual(3, _testModel.Counter);
        }

        [Test]
        public void InjectCommand_ShouldInjectDependencies()
        {
            _testSystem.CallCount = 5;
            _gameContext.ExecuteCommand(new TestInjectCommand { Name = "inject_test" });

            Assert.AreEqual("inject_test", _testModel.Value);
            Assert.AreEqual(6, _testModel.Counter); // CallCount(5) + 1
        }

        [UnityTest]
        public IEnumerator AsyncCommand_ShouldExecuteAndModifyModel()
        {
            _gameContext.ExecuteCommandAsync(new TestAsyncCommand { Name = "async_test", DelayMs = 50 });

            yield return new UnityEngine.WaitForSeconds(0.1f);

            Assert.AreEqual("async_test", _testModel.Value);
            Assert.AreEqual(1, _testModel.Counter);
        }

        [UnityTest]
        public IEnumerator AsyncCommand_WithReturnValue_ShouldReturnCorrectResult()
        {
            string result = null;

            yield return UniTask.ToCoroutine(async () =>
            {
                result = await _gameContext.ExecuteCommandAsync(
                    new TestAsyncResultCommand { Name = "AsyncWorld", DelayMs = 50 });
            });

            Assert.AreEqual("Hello AsyncWorld, model value: cmd_test", result);
        }

        [Test]
        public void Command_CanAccessSystem_ThroughContextParameter()
        {
            _gameContext.ExecuteCommand(new TestCommand { Name = "ext_test" });

            Assert.AreEqual("ext_test", _testModel.Value);
        }

        [Test]
        public void Command_CanAccessModel_ThroughContextParameter()
        {
            var result = _gameContext.ExecuteCommand(new TestResultCommand { Name = "ModelTest" });

            StringAssert.Contains("cmd_test", result);
        }

        /// <summary>
        /// struct Command 零装箱测试：struct 不能用 [Inject]，只能通过 ctx 参数访问层。
        /// </summary>
        [Test]
        public void StructCommand_ShouldExecuteWithoutBoxing()
        {
            _gameContext.ExecuteCommand(new TestStructCommand("struct_test"));

            Assert.AreEqual("struct_test", _testModel.Value);
            Assert.AreEqual(1, _testModel.Counter);
        }

        /// <summary>
        /// struct Command + 返回值：双泛型重载 ExecuteCommand&lt;T,TResult&gt; 保持值类型语义，零装箱。
        /// </summary>
        [Test]
        public void StructResultCommand_ShouldReturnValueWithoutBoxing()
        {
            var result = _gameContext.ExecuteCommand<TestStructResultCommand, string>(
                new TestStructResultCommand("StructResult"));

            StringAssert.Contains("Hello StructResult", result);
        }

        /// <summary>
        /// struct Async Command：零装箱异步执行。
        /// </summary>
        [UnityTest]
        public IEnumerator StructAsyncCommand_ShouldExecuteWithoutBoxing()
        {
            _gameContext.ExecuteCommandAsync(new TestStructAsyncCommand("struct_async", 50));

            yield return new UnityEngine.WaitForSeconds(0.1f);

            Assert.AreEqual("struct_async", _testModel.Value);
            Assert.AreEqual(1, _testModel.Counter);
        }

        /// <summary>
        /// class Async Command + [Inject]：异步命令中注入的依赖在 await 后可正常使用。
        /// </summary>
        [UnityTest]
        public IEnumerator InjectAsyncCommand_ShouldExecuteAfterDelay()
        {
            _gameContext.ExecuteCommandAsync(new TestInjectAsyncCommand { Name = "async_inject", DelayMs = 50 });

            yield return new UnityEngine.WaitForSeconds(0.1f);

            Assert.AreEqual("async_inject", _testModel.Value);
        }

        /// <summary>
        /// 链式 Command：Command 通过 ctx 执行子 Command，验证 ctx.ExecuteCommand 在 Command 内可用。
        /// </summary>
        [Test]
        public void ChainCommand_ShouldExecuteSubCommand()
        {
            _gameContext.ExecuteCommand(new TestChainCommand { Name = "chain" });

            // _inner sub-command updated to "chain_inner", then chain updated to "chain"
            Assert.AreEqual("chain", _testModel.Value);
            Assert.AreEqual(2, _testModel.Counter);
        }

        /// <summary>
        /// Dispose 后 ExecuteCommand 抛 ObjectDisposedException。
        /// </summary>
        [Test]
        public void ExecuteCommand_AfterDispose_ShouldThrow()
        {
            _gameContext.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                _gameContext.ExecuteCommand(new TestCommand { Name = "after_dispose" }));
        }
    }
}
