using System.Collections;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Command;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.Utility;
using NUnit.Framework;
using UnityEngine.TestTools;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Test
{
    /// <summary>
    /// 测试各层的访问权限是否正确
    /// </summary>
    public class LayerAccessTests
    {
        private GameContext _gameContext;

        [SetUp]
        public void SetUp()
        {
            var testModel = new TestModel { Value = "layer_test" };
            var testSystem = new TestSystem();
            var dependentSystem = new DependentSystem();
            var testUtility = new TestUtility { Name = "layer_utility" };

            var builder = new ContainerBuilder();
            builder.RegisterValue(testModel, new[] { typeof(Game.Framework.Model.IModel), typeof(TestModel) });
            builder.RegisterValue(testSystem, new[] { typeof(Game.Framework.Systems.ISystem), typeof(TestSystem) });
            builder.RegisterValue(dependentSystem, new[] { typeof(DependentSystem) });
            builder.RegisterValue(testUtility, new[] { typeof(Game.Framework.Utility.IUtility), typeof(TestUtility) });
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            _gameContext = new GameContext(builder.Build());

            _gameContext.Inject(testSystem);
            _gameContext.AttachTo(testSystem);
            _gameContext.Inject(dependentSystem);
            _gameContext.AttachTo(dependentSystem);
        }

        [Test]
        public void ISystem_ShouldHaveCorrectPermissions()
        {
            var system = new TestSystem();
            Assert.IsInstanceOf<ICanGetModel>(system);
            Assert.IsInstanceOf<ICanGetSystem>(system);
            Assert.IsInstanceOf<ICanGetUtility>(system);
            Assert.IsInstanceOf<ICanSendEvent>(system);
            Assert.IsInstanceOf<ICanRegisterEvent>(system);
            Assert.IsNotInstanceOf<ICanSendCommand>(system);
        }

        [Test]
        public void AllCommandTypes_ShouldHaveNoPermissionInterfaces()
        {
            object[] commands = { new TestCommand(), new TestAsyncCommand(), new TestResultCommand(), new TestAsyncResultCommand() };
            foreach (var cmd in commands)
            {
                Assert.IsNotInstanceOf<ICanGetModel>(cmd, $"{cmd.GetType().Name} should not have ICanGetModel");
                Assert.IsNotInstanceOf<ICanGetSystem>(cmd, $"{cmd.GetType().Name} should not have ICanGetSystem");
                Assert.IsNotInstanceOf<ICanGetUtility>(cmd, $"{cmd.GetType().Name} should not have ICanGetUtility");
                Assert.IsNotInstanceOf<ICanSendCommand>(cmd, $"{cmd.GetType().Name} should not have ICanSendCommand");
                Assert.IsNotInstanceOf<ICanSendEvent>(cmd, $"{cmd.GetType().Name} should not have ICanSendEvent");
                Assert.IsNotInstanceOf<ICanRegisterEvent>(cmd, $"{cmd.GetType().Name} should not have ICanRegisterEvent");
            }
        }

        [Test]
        public void IView_ShouldHaveCorrectPermissions()
        {
            var view = new TestView();
            Assert.IsInstanceOf<ICanSendCommand>(view);
            Assert.IsInstanceOf<ICanRegisterEvent>(view);
            Assert.IsInstanceOf<ICanGetUtility>(view);
            Assert.IsNotInstanceOf<ICanGetModel>(view);
            Assert.IsNotInstanceOf<ICanGetSystem>(view);
            Assert.IsNotInstanceOf<ICanSendEvent>(view);
        }

        [Test]
        public void IModel_ShouldHaveCorrectPermissions()
        {
            var model = new TestModel();
            Assert.IsInstanceOf<ICanGetUtility>(model);
            Assert.IsNotInstanceOf<ICanGetModel>(model);
            Assert.IsNotInstanceOf<ICanGetSystem>(model);
            Assert.IsNotInstanceOf<ICanSendCommand>(model);
            Assert.IsNotInstanceOf<ICanSendEvent>(model);
            Assert.IsNotInstanceOf<ICanRegisterEvent>(model);
        }

        [Test]
        public void System_CanGetOtherSystem()
        {
            var system = new DependentSystem();
            _gameContext.Inject(system);
            _gameContext.AttachTo(system);
            var result = system.GetCombinedGreeting("Test");
            StringAssert.Contains("Hello Test", result);
        }

        [Test]
        public void System_CanGetModel()
        {
            var system = new TestSystem();
            _gameContext.Inject(system);
            _gameContext.AttachTo(system);
            var model = system.GetModel<TestModel>();
            Assert.IsNotNull(model);
            Assert.AreEqual("layer_test", model.Value);
        }

        [Test]
        public void View_CanSendCommand()
        {
            var view = new TestView();
            _gameContext.Inject(view);
            _gameContext.AttachTo(view);
            view.DoExecuteCommand("view_test");

            var model = _gameContext.GetModel<TestModel>();
            Assert.AreEqual("view_test", model.Value);
        }

        [Test]
        public void View_CanSendCommandWithReturnValue()
        {
            var view = new TestView();
            _gameContext.Inject(view);
            _gameContext.AttachTo(view);
            var result = view.DoExecuteResultCommand("ViewUser");

            StringAssert.Contains("Hello ViewUser", result);
        }

        // Utility 功能测试

        [Test]
        public void System_CanGetUtility()
        {
            var system = new TestSystem();
            _gameContext.Inject(system);
            _gameContext.AttachTo(system);
            var utility = system.GetUtility<TestUtility>();

            Assert.IsNotNull(utility);
            Assert.AreEqual("layer_utility", utility.Name);
        }

        // 异步 Command 功能测试

        [UnityTest]
        public IEnumerator View_CanExecuteAsyncResultCommand()
        {
            var view = new TestView();
            _gameContext.Inject(view);
            _gameContext.AttachTo(view);
            string result = null;

            yield return UniTask.ToCoroutine(async () =>
            {
                result = await view.DoExecuteAsyncResultCommand("AsyncView", 50);
            });

            StringAssert.Contains("Hello AsyncView", result);
        }
    }
}
