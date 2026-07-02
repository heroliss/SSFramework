using System;
using Game.Framework.Context;
using Game.Framework.Common;
using Game.Framework.Internal;
using Game.Framework.Command;
using Game.Framework.Event;
using Game.Framework.System;
using Game.Framework.Utility;
using Game.Framework.Model;
using Game.Framework.View;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 测试 Architecture 初始化、GetModel、GetSystem、GetUtility、Inject、SendEvent
    /// </summary>
    public class ArchitectureTests
    {
        private GameContext _gameContext;
        private TestModel _testModel;
        private TestSystem _testSystem;
        private TestUtility _testUtility;

        [SetUp]
        public void SetUp()
        {
            _testModel = new TestModel { Value = "test_value" };
            _testSystem = new TestSystem();
            _testUtility = new TestUtility { Name = "test_utility" };

            var builder = new ContainerBuilder();
            builder.RegisterValue(_testModel, new[] { typeof(Game.Framework.Model.IModel), typeof(TestModel) });
            builder.RegisterValue(_testSystem, new[] { typeof(ISystem), typeof(TestSystem) });
            builder.RegisterValue(_testUtility, new[] { typeof(IUtility), typeof(TestUtility) });
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            _gameContext = new GameContext(builder.Build());

            _gameContext.Inject(_testSystem);
            _gameContext.AttachTo(_testSystem);
        }

        [Test]
        public void Initialize_ShouldSetContainer()
        {
            Assert.IsNotNull(_gameContext.Container);
        }

        [Test]
        public void GetModel_ShouldReturnRegisteredModel()
        {
            var model = _gameContext.GetModel<TestModel>();
            Assert.IsNotNull(model);
            Assert.AreEqual("test_value", model.Value);
        }

        [Test]
        public void GetSystem_ShouldReturnRegisteredSystem()
        {
            var system = _gameContext.GetSystem<TestSystem>();
            Assert.IsNotNull(system);
        }

        [Test]
        public void Inject_ShouldInjectDependenciesIntoObject()
        {
            var command = new TestInjectCommand { Name = "injected" };
            _gameContext.Inject(command);

            Assert.IsNotNull(command.TestSystem);
            Assert.IsNotNull(command.TestModel);
            Assert.AreEqual("test_value", command.TestModel.Value);
        }

        [Test]
        public void GetModel_ShouldReturnSameInstance()
        {
            var model1 = _gameContext.GetModel<TestModel>();
            var model2 = _gameContext.GetModel<TestModel>();

            Assert.AreSame(model1, model2);
        }

        [Test]
        public void GetSystem_ShouldReturnSameInstance()
        {
            var system1 = _gameContext.GetSystem<TestSystem>();
            var system2 = _gameContext.GetSystem<TestSystem>();

            Assert.AreSame(system1, system2);
        }

        [Test]
        public void GetUtility_ShouldReturnRegisteredUtility()
        {
            var utility = _gameContext.GetUtility<TestUtility>();

            Assert.IsNotNull(utility);
            Assert.AreEqual("test_utility", utility.Name);
        }

        [Test]
        public void GetUtility_ShouldReturnSameInstance()
        {
            var utility1 = _gameContext.GetUtility<TestUtility>();
            var utility2 = _gameContext.GetUtility<TestUtility>();

            Assert.AreSame(utility1, utility2);
        }

        [Test]
        public void SendEvent_ShouldDeliverToSubscriber()
        {
            string received = null;
            var testSystem = new TestSystem();
            _gameContext.Inject(testSystem);
            _gameContext.AttachTo(testSystem);
            var subscription = testSystem.RegisterEvent<TestEvent>(evt => received = evt.Message);

            _gameContext.SendEvent(new TestEvent("arch_event", 99));

            Assert.AreEqual("arch_event", received);

            subscription.Dispose();
        }

        /// <summary>
        /// 多 Context 共存：两个独立 GameContext，各自拥有独立的 Model/System，互不干扰。
        /// </summary>
        [Test]
        public void MultipleContexts_ShouldBeIsolated()
        {
            // Context A
            var modelA = new TestModel { Value = "A" };
            var systemA = new TestSystem();
            var builderA = new ContainerBuilder();
            builderA.RegisterValue(modelA, new[] { typeof(Game.Framework.Model.IModel), typeof(TestModel) });
            builderA.RegisterValue(systemA, new[] { typeof(ISystem), typeof(TestSystem) });
            builderA.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctxA = new GameContext(builderA.Build());
            ctxA.Inject(systemA);
            ctxA.AttachTo(systemA);

            // Context B
            var modelB = new TestModel { Value = "B" };
            var systemB = new TestSystem();
            var builderB = new ContainerBuilder();
            builderB.RegisterValue(modelB, new[] { typeof(Game.Framework.Model.IModel), typeof(TestModel) });
            builderB.RegisterValue(systemB, new[] { typeof(ISystem), typeof(TestSystem) });
            builderB.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctxB = new GameContext(builderB.Build());
            ctxB.Inject(systemB);
            ctxB.AttachTo(systemB);

            // Execute in A → only A changes
            ctxA.ExecuteCommand(new TestCommand { Name = "A_cmd" });
            Assert.AreEqual("A_cmd", modelA.Value);
            Assert.AreEqual("B", modelB.Value);

            // Execute in B → only B changes
            ctxB.ExecuteCommand(new TestCommand { Name = "B_cmd" });
            Assert.AreEqual("A_cmd", modelA.Value);
            Assert.AreEqual("B_cmd", modelB.Value);

            // Dispose
            ctxA.Dispose();
            ctxB.Dispose();
        }

        /// <summary>
        /// GetModel 未注册类型应抛异常。
        /// </summary>
        [Test]
        public void GetModel_UnregisteredType_ShouldThrow()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            using var ctx = new GameContext(builder.Build());
            Assert.Throws<InvalidOperationException>(() => ctx.GetModel<TestModel>());
        }

        /// <summary>
        /// Dispose 后 SendEvent 不抛异常，静默忽略。
        /// </summary>
        [Test]
        public void SendEvent_AfterDispose_ShouldNotThrow()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());
            ctx.Dispose();

            Assert.DoesNotThrow(() => ctx.SendEvent(new TestEvent("x", 1)));
        }

        /// <summary>
        /// RegisterFor 排除 TLayer 标记接口：ISystem 本身不能作为解析 key，多个 System 可共存。
        /// </summary>
        [Test]
        public void RegisterFor_ShouldExcludeTLayerMarkerInterface()
        {
            var systemA = new TestSystem();
            var systemB = new DependentSystem();

            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterValue(systemA, new[] { typeof(TestSystem) });
            builder.RegisterValue(systemB, new[] { typeof(DependentSystem) });
            var ctx = new GameContext(builder.Build());

            // 具体类型可解析
            Assert.AreSame(systemA, ctx.GetSystem<TestSystem>());

            // ISystem 标记接口未注册，多个 System 共存时不会冲突
            Assert.Throws<InvalidOperationException>(() => ctx.Container.Resolve(typeof(ISystem)));
        }

        /// <summary>
        /// AttachTo 对 IHasGameContext 走反射字段赋值路径。
        /// </summary>
        [Test]
        public void AttachTo_HasGameContext_ShouldSetContextField()
        {
            var system = new TestSystem();
            _gameContext.AttachTo(system);

            Assert.AreSame(_gameContext, ((IHasGameContext)system).Context);
        }

        /// <summary>
        /// GameContext 多次 Dispose 安全，不抛异常。
        /// </summary>
        [Test]
        public void Dispose_MultipleCalls_ShouldNotThrow()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            ctx.Dispose();
            Assert.DoesNotThrow(() => ctx.Dispose());
        }

        /// <summary>
        /// IEventBus 接口：通过接口引用发送和接收事件。
        /// </summary>
        [Test]
        public void IEventBus_ShouldSendAndReceiveEvents()
        {
            IEventBus bus = _gameContext;
            string received = null;
            var subscription = bus.RegisterEvent<TestEvent>(evt => received = evt.Message);

            bus.SendEvent(new TestEvent("bus_test", 0));

            Assert.AreEqual("bus_test", received);
            subscription.Dispose();
        }

        // ---- 自实现 Container 测试 ----

        /// <summary>
        /// Container 父级回退：子容器未命中时向父级查找。
        /// </summary>
        [Test]
        public void Container_TryResolve_WithParent_ShouldFallback()
        {
            var parentBuilder = new ContainerBuilder();
            parentBuilder.RegisterValue("shared", new[] { typeof(string) });
            var parentContainer = parentBuilder.Build();

            var childBuilder = new ContainerBuilder().SetParent(parentContainer);
            childBuilder.RegisterValue(42, new[] { typeof(int) });
            var childContainer = childBuilder.Build();

            Assert.AreEqual(42, childContainer.Resolve(typeof(int)));
            Assert.AreEqual("shared", childContainer.Resolve(typeof(string)));
        }

        /// <summary>
        /// Container 覆盖语义：同一契约多次注册，后注册覆盖先注册。
        /// </summary>
        [Test]
        public void Container_TryResolve_LastRegistrationWins()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue("first", new[] { typeof(string) });
            builder.RegisterValue("second", new[] { typeof(string) });
            var container = builder.Build();

            Assert.AreEqual("second", container.Resolve(typeof(string)));
        }

        // ---- GameContext.Main 全局回退测试 ----

        /// <summary>
        /// GameContext.Main 设置后，子上下文解析不到时回退到主上下文。
        /// </summary>
        [Test]
        public void GameContextMain_Set_ShouldProvideGlobalFallback()
        {
            var mainModel = new TestModel { Value = "main_model" };
            var mainBuilder = new ContainerBuilder();
            mainBuilder.RegisterValue(mainModel, new[] { typeof(Game.Framework.Model.IModel), typeof(TestModel) });
            mainBuilder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var mainCtx = new GameContext(mainBuilder.Build());
            GameContext.Main = mainCtx;

            try
            {
                var childBuilder = new ContainerBuilder();
                childBuilder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
                var childCtx = new GameContext(childBuilder.Build());

                var model = childCtx.GetModel<TestModel>();
                Assert.AreEqual("main_model", model.Value);
            }
            finally
            {
                GameContext.Main = null;
            }
        }

        /// <summary>
        /// GameContext.Main 为空时，子上下文不回退（应抛异常）。
        /// </summary>
        [Test]
        public void GameContextMain_Null_ShouldNotFallback()
        {
            GameContext.Main = null;

            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            Assert.Throws<InvalidOperationException>(() => ctx.GetModel<TestModel>());
        }

        // ---- GameContext 动态注册测试 ----

        /// <summary>
        /// RegisterModel：运行时动态注册 Model 实例。
        /// </summary>
        [Test]
        public void RegisterModel_Dynamic_ShouldBeResolvable()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            var model = new TestModel { Value = "dynamic" };
            ctx.RegisterModel(model);

            var resolved = ctx.GetModel<TestModel>();
            Assert.AreSame(model, resolved);
            Assert.AreEqual("dynamic", resolved.Value);
        }

        /// <summary>
        /// UnregisterModel：运行时动态注销 Model 实例。
        /// </summary>
        [Test]
        public void UnregisterModel_Dynamic_ShouldRemove()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            var model = new TestModel { Value = "dynamic" };
            ctx.RegisterModel(model);
            ctx.UnregisterModel(model);

            Assert.Throws<InvalidOperationException>(() => ctx.GetModel<TestModel>());
        }

        /// <summary>
        /// RegisterSystem：运行时动态注册 System 实例。
        /// </summary>
        [Test]
        public void RegisterSystem_Dynamic_ShouldBeResolvable()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            var system = new TestSystem();
            ctx.RegisterSystem(system);

            var resolved = ctx.GetSystem<TestSystem>();
            Assert.AreSame(system, resolved);
        }

        /// <summary>
        /// RegisterUtility：运行时动态注册 Utility 实例。
        /// </summary>
        [Test]
        public void RegisterUtility_Dynamic_ShouldBeResolvable()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            var utility = new TestUtility { Name = "dynamic_utility" };
            ctx.RegisterUtility(utility);

            var resolved = ctx.GetUtility<TestUtility>();
            Assert.AreSame(utility, resolved);
            Assert.AreEqual("dynamic_utility", resolved.Name);
        }

        /// <summary>
        /// 动态注册后通过 [Inject] 注入应能解析到新注册的实例。
        /// </summary>
        [Test]
        public void RegisterDynamic_AfterInject_ShouldResolve()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            var model = new TestModel { Value = "injected_dynamic" };
            ctx.RegisterModel(model);

            var command = new TestInjectCommand { Name = "dynamic_test" };
            ctx.Inject(command);

            Assert.IsNotNull(command.TestModel);
            Assert.AreEqual("injected_dynamic", command.TestModel.Value);
        }

        /// <summary>
        /// RegisterFactory 多契约：两个契约应解析到同一个实例（Singleton 语义）。
        /// </summary>
        [Test]
        public void RegisterFactory_MultipleContracts_ShouldReturnSameInstance()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            builder.RegisterFactory(
                _ => new TestModel { Value = "factory_shared" },
                Resolution.Lazy,
                typeof(Game.Framework.Model.IModel), typeof(TestModel));
            var ctx = new GameContext(builder.Build());

            var resolvedAsInterface = ctx.GetModel<Game.Framework.Model.IModel>();
            var resolvedAsConcrete  = ctx.GetModel<TestModel>();

            Assert.AreSame(resolvedAsInterface, resolvedAsConcrete, "Multi-contract factory must return the same instance");
            ctx.Dispose();
        }

        /// <summary>
        /// Dispose 后 ExecuteCommand 应抛 ObjectDisposedException，与 SendEvent 行为保持对称。
        /// </summary>
        [Test]
        public void ExecuteCommand_AfterDispose_ShouldThrow()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());
            ctx.Dispose();

            Assert.Throws<ObjectDisposedException>(() => ctx.ExecuteCommand(new TestCommand { Name = "x" }));
        }

        /// <summary>
        /// [Inject] IGameContext 字段应被 forbidden-type 检测跳过，字段保持 null。
        /// InjectionPlan 按类型缓存：首次构建计划时会打 LogError，后续复用缓存不再打。
        /// 因此用 ignoreFailingMessages 屏蔽 LogError，只验证字段未被注入这一核心行为。
        /// </summary>
        [Test]
        public void Inject_IGameContext_FieldShouldRemainNull()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            var target = new ForbiddenInjectTarget();
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.DoesNotThrow(() => ctx.Inject(target));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            Assert.IsNull(target.Ctx, "IGameContext must not be injected (forbidden type)");
            ctx.Dispose();
        }

        // ---- [Inject] 注入期权限校验：宿主有对应 ICanGetX 权限才能注入该层（与 this.GetXxx 同源；Command 经 ctx 有完整访问权）----

        /// <summary>View 无 ICanGetModel：注入 Model 应被拦下、字段保持 null（容器里已注册 Model，排除"解析不到"的干扰）。</summary>
        [Test]
        public void Inject_ViewCannotInjectModel_FieldRemainsNull()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new TestModel(), new[] { typeof(TestModel) });
            var ctx = new GameContext(builder.Build());

            var target = new ViewInjectsModel();
            LogAssert.ignoreFailingMessages = true;   // 越权注入会打 LogError（按类型缓存，仅首次）
            try { Assert.DoesNotThrow(() => ctx.Inject(target)); }
            finally { LogAssert.ignoreFailingMessages = false; }

            Assert.IsNull(target.Model, "View 无 GetModel 权限，[Inject] Model 应被注入期校验拦下");
            ctx.Dispose();
        }

        /// <summary>View 有 ICanGetUtility：注入 Utility 应放行——对照组，证明校验按权限、不是一刀切禁 View。</summary>
        [Test]
        public void Inject_ViewCanInjectUtility()
        {
            var builder = new ContainerBuilder();
            var utility = new TestUtility { Name = "u" };
            builder.RegisterValue(utility, new[] { typeof(TestUtility) });
            var ctx = new GameContext(builder.Build());

            var target = new ViewInjectsUtility();
            ctx.Inject(target);

            Assert.AreSame(utility, target.Utility, "View 有 GetUtility 权限，[Inject] Utility 应放行");
            ctx.Dispose();
        }

        /// <summary>Model 无 ICanGetSystem：注入 System 应被拦下。</summary>
        [Test]
        public void Inject_ModelCannotInjectSystem_FieldRemainsNull()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new TestSystem(), new[] { typeof(TestSystem) });
            var ctx = new GameContext(builder.Build());

            var target = new ModelInjectsSystem();
            LogAssert.ignoreFailingMessages = true;
            try { Assert.DoesNotThrow(() => ctx.Inject(target)); }
            finally { LogAssert.ignoreFailingMessages = false; }

            Assert.IsNull(target.System, "Model 无 GetSystem 权限，[Inject] System 应被拦下");
            ctx.Dispose();
        }

        /// <summary>Model 有 ICanGetUtility：注入 Utility 应放行。</summary>
        [Test]
        public void Inject_ModelCanInjectUtility()
        {
            var builder = new ContainerBuilder();
            var utility = new TestUtility();
            builder.RegisterValue(utility, new[] { typeof(TestUtility) });
            var ctx = new GameContext(builder.Build());

            var target = new ModelInjectsUtility();
            ctx.Inject(target);

            Assert.AreSame(utility, target.Utility, "Model 有 GetUtility 权限，[Inject] Utility 应放行");
            ctx.Dispose();
        }

        /// <summary>Utility 有 ICanGetUtility（IUtility : ICanGetUtility，基础设施互相组合，如配置服务取资源服务）：注入其他 Utility 应放行；Model/System 仍被拦（见下）。</summary>
        [Test]
        public void Inject_UtilityCanInjectUtility()
        {
            var builder = new ContainerBuilder();
            var utility = new TestUtility();
            builder.RegisterValue(utility, new[] { typeof(TestUtility) });
            var ctx = new GameContext(builder.Build());

            var target = new UtilityInjectsUtility();
            ctx.Inject(target);

            Assert.AreSame(utility, target.Utility, "IUtility : ICanGetUtility，[Inject] 其他 Utility 应放行");
            ctx.Dispose();
        }

        /// <summary>Utility 只有 ICanGetUtility：注入 Model 仍应被拦下（基础设施不反向依赖业务状态）。</summary>
        [Test]
        public void Inject_UtilityCannotInjectModel_FieldRemainsNull()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new TestModel { Value = "m" }, new[] { typeof(TestModel) });
            var ctx = new GameContext(builder.Build());

            var target = new UtilityInjectsModel();
            LogAssert.ignoreFailingMessages = true;
            try { Assert.DoesNotThrow(() => ctx.Inject(target)); }
            finally { LogAssert.ignoreFailingMessages = false; }

            Assert.IsNull(target.Model, "Utility 无 GetModel 权限，[Inject] Model 应被拦下");
            ctx.Dispose();
        }

        /// <summary>System 有 ICanGetModel：注入 Model 应放行。</summary>
        [Test]
        public void Inject_SystemCanInjectModel()
        {
            var builder = new ContainerBuilder();
            var model = new TestModel { Value = "m" };
            builder.RegisterValue(model, new[] { typeof(TestModel) });
            var ctx = new GameContext(builder.Build());

            var target = new SystemInjectsModel();
            ctx.Inject(target);

            Assert.AreSame(model, target.Model, "System 有 GetModel 权限，[Inject] Model 应放行");
            ctx.Dispose();
        }

        /// <summary>Command 不实现 ICanXxx，但经 ctx 有完整层访问权：宿主无 ICanGetModel 也应放行 Model 注入（Command 特判）。</summary>
        [Test]
        public void Inject_CommandCanInjectModel_DespiteNoICanGet()
        {
            var builder = new ContainerBuilder();
            var model = new TestModel { Value = "cmd" };
            builder.RegisterValue(model, new[] { typeof(TestModel) });
            var ctx = new GameContext(builder.Build());

            var target = new CommandInjectsModel();
            ctx.Inject(target);

            Assert.AreSame(model, target.Model, "Command 经 ctx 有完整访问权，[Inject] Model 应放行");
            ctx.Dispose();
        }

        /// <summary>
        /// Container.HasBinding：本地注册时返回 true，未注册时返回 false。
        /// </summary>
        [Test]
        public void Container_HasBinding_ShouldReflectRegistrations()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue("hello", new[] { typeof(string) });
            var container = builder.Build();

            Assert.IsTrue(container.HasBinding(typeof(string)));
            Assert.IsFalse(container.HasBinding(typeof(int)));
        }

        /// <summary>
        /// CancellationToken 在 Dispose 时自动取消。
        /// </summary>
        [Test]
        public void CancellationToken_AfterDispose_ShouldBeCancelled()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            var token = ctx.CancellationToken;
            Assert.IsFalse(token.IsCancellationRequested);

            ctx.Dispose();
            Assert.IsTrue(token.IsCancellationRequested);
        }

        /// <summary>
        /// 重复注册运行时覆盖应抛 InvalidOperationException。
        /// </summary>
        [Test]
        public void DuplicateRuntimeRegistration_ShouldThrow()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            var ctx = new GameContext(builder.Build());

            var model1 = new TestModel { Value = "first" };
            var model2 = new TestModel { Value = "second" };
            ctx.RegisterModel(model1);

            Assert.Throws<InvalidOperationException>(() => ctx.RegisterModel(model2));
            ctx.Dispose();
        }

        // ---- 辅助类型 ----

        private class ForbiddenInjectTarget
        {
            [Inject] public IGameContext Ctx;
        }

        // 注入期权限校验用：各层宿主声明一个 [Inject] 层字段（实现层标记接口即可，无需 IHasGameContext）。
        private class ViewInjectsModel : IView { [Inject] public TestModel Model; }
        private class ViewInjectsUtility : IView { [Inject] public TestUtility Utility; }
        private class ModelInjectsSystem : IModel { [Inject] public TestSystem System; }
        private class ModelInjectsUtility : IModel { [Inject] public TestUtility Utility; }
        private class UtilityInjectsUtility : IUtility { [Inject] public TestUtility Utility; }
        private class UtilityInjectsModel : IUtility { [Inject] public TestModel Model; }
        private class SystemInjectsModel : ISystem { [Inject] public TestModel Model; }
        private class CommandInjectsModel : ICommand
        {
            [Inject] public TestModel Model;
            public void Execute(ICommandContext ctx) { }
        }
    }
}
