using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.Utility;
using NUnit.Framework;
using UnityEngine.TestTools;
using LogType = UnityEngine.LogType;

namespace Game.Framework.Test
{
    /// <summary>
    /// 测试 Container 注册契约：构建期 vs 运行期、同层 vs 跨层、覆盖 vs 抛异常。
    /// 这些行为是 <c>Assets/Game/AGENTS.md</c>「Container 与所有权」的合同，必须有测试固化以防回归。
    /// </summary>
    public class ContainerContractTests
    {
        private sealed class ModelA : IModel { public string Tag = "A"; }
        private sealed class ModelB : IModel { public string Tag = "B"; }

        private sealed class SystemA : ISystem, IHasGameContext
        {
            private GameContext _ctx;
            IGameContext IHasGameContext.Context => _ctx;
        }

        private sealed class SystemB : ISystem, IHasGameContext
        {
            private GameContext _ctx;
            IGameContext IHasGameContext.Context => _ctx;
        }

        private interface ILayerModel : IModel { }
        private sealed class LayerModel : ILayerModel { }

        private interface ILayerSystem : ISystem { }
        private sealed class LayerSystem : ILayerSystem { }

        private interface ILayerUtility : IUtility { }
        private sealed class LayerUtility : ILayerUtility { }

        private interface IOwnedLayerModel : IModel { }
        private sealed class OwnedLayerModel : IOwnedLayerModel, IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private interface IOwnedLayerSystem : ISystem { }
        private sealed class OwnedLayerSystem : IOwnedLayerSystem, IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private interface IOwnedLayerUtility : IUtility { }
        private sealed class OwnedLayerUtility : IOwnedLayerUtility, IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private sealed class InvalidMultiLayer : IModel, ISystem { }

        [Test]
        public void Builder_LayerAwareRegistration_RegistersConcreteAndLayerInterfaces()
        {
            var model = new LayerModel();
            var system = new LayerSystem();
            var utility = new LayerUtility();
            var ownedModel = new OwnedLayerModel();
            var ownedSystem = new OwnedLayerSystem();
            var ownedUtility = new OwnedLayerUtility();
            using var builder = new ContainerBuilder();
            builder.RegisterModel(model);
            builder.RegisterSystem(system);
            builder.RegisterUtility(utility);
            builder.RegisterOwnedModel(ownedModel);
            builder.RegisterOwnedSystem(ownedSystem);
            builder.RegisterOwnedUtility(ownedUtility);
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            Assert.AreSame(model, ctx.GetModel<LayerModel>());
            Assert.AreSame(model, ctx.GetModel<ILayerModel>());
            Assert.AreSame(system, ctx.GetSystem<LayerSystem>());
            Assert.AreSame(system, ctx.GetSystem<ILayerSystem>());
            Assert.AreSame(utility, ctx.GetUtility<LayerUtility>());
            Assert.AreSame(utility, ctx.GetUtility<ILayerUtility>());
            Assert.AreSame(ownedModel, ctx.GetModel<IOwnedLayerModel>());
            Assert.AreSame(ownedSystem, ctx.GetSystem<IOwnedLayerSystem>());
            Assert.AreSame(ownedUtility, ctx.GetUtility<IOwnedLayerUtility>());
            Assert.IsFalse(ctx.TryResolve(typeof(IModel), out _), "层标记本身不应成为可解析契约");
            Assert.IsFalse(ctx.TryResolve(typeof(ISystem), out _), "层标记本身不应成为可解析契约");
            Assert.IsFalse(ctx.TryResolve(typeof(IUtility), out _), "层标记本身不应成为可解析契约");

            ctx.Dispose();
            Assert.AreEqual(1, ownedModel.DisposeCount);
            Assert.AreEqual(1, ownedSystem.DisposeCount);
            Assert.AreEqual(1, ownedUtility.DisposeCount);
        }

        [Test]
        public void Builder_LayerAwareRegistration_MultipleLayerMarkersFailFast()
        {
            using var builder = new ContainerBuilder();

            var error = Assert.Throws<ArgumentException>(
                () => builder.RegisterSystem(new InvalidMultiLayer()));

            StringAssert.Contains("恰好实现一个层标记", error.Message);
            StringAssert.Contains(nameof(InvalidMultiLayer), error.Message);
        }

        // ── 构建期：同 contract 重复注册 → 后注册胜出（不抛） ──────────────

        [Test]
        public void Builder_RegisterValue_SameContract_LastWins()
        {
            var modelA = new ModelA();
            var modelB = new ModelB();

            var builder = new ContainerBuilder();
            builder.RegisterValue(modelA, typeof(IModel));
            builder.RegisterValue(modelB, typeof(IModel));
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));

            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
            Assert.AreSame(modelB, ctx.Resolve(typeof(IModel)),
                "ContainerBuilder 构建期重复 RegisterValue 应当后注册胜出（覆盖）");
        }

        [Test]
        public void Builder_RegisterFactory_SameContract_LastWins()
        {
            var modelA = new ModelA();
            var modelB = new ModelB();

            var builder = new ContainerBuilder();
            builder.RegisterFactory(_ => modelA, typeof(IModel));
            builder.RegisterFactory(_ => modelB, typeof(IModel));
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));

            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
            Assert.AreSame(modelB, ctx.Resolve(typeof(IModel)),
                "ContainerBuilder 构建期 Factory 重复注册也应后注册胜出");
        }

        // ── 运行期：同层重复注册 → 抛 InvalidOperationException ──────────

        [Test]
        public void RegisterModel_DuplicateAtRuntime_Throws()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            var modelA = new ModelA();
            var modelB = new ModelA();
            ctx.RegisterModel(modelA);

            Assert.Throws<InvalidOperationException>(
                () => ctx.RegisterModel(modelB),
                "运行期同层同具体类型重复注册应抛 InvalidOperationException");
        }

        [Test]
        public void RegisterSystem_DuplicateAtRuntime_Throws()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            var systemA = new SystemA();
            var systemA2 = new SystemA();
            ctx.RegisterSystem(systemA);

            Assert.Throws<InvalidOperationException>(
                () => ctx.RegisterSystem(systemA2),
                "运行期同层同具体类型重复 RegisterSystem 应抛 InvalidOperationException");
        }

        [Test]
        public void RegisterUtility_DuplicateAtRuntime_Throws()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            var util1 = new TestUtility();
            var util2 = new TestUtility();
            ctx.RegisterUtility(util1);

            Assert.Throws<InvalidOperationException>(
                () => ctx.RegisterUtility(util2),
                "运行期同层同具体类型重复 RegisterUtility 应抛 InvalidOperationException");
        }

        // ── 运行期：先 Unregister 再注册 → 成功 ──────────────────────────

        [Test]
        public void RegisterModel_AfterUnregister_Succeeds()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            var modelA = new ModelA { Tag = "first" };
            var modelA2 = new ModelA { Tag = "second" };
            ctx.RegisterModel(modelA);
            ctx.UnregisterModel(modelA);
            ctx.RegisterModel(modelA2);

            Assert.AreSame(modelA2, ctx.GetModel<ModelA>(),
                "Unregister 后再 Register 应成功，解析到新实例");
        }

        // ── 跨层级：子级运行期注册覆盖父级 InstallBindings ────────────────

        [Test]
        public void ChildContext_RuntimeRegister_OverridesParentBinding()
        {
            // 父级构建期注册 ModelA
            var parentModel = new ModelA { Tag = "parent" };
            var parentBuilder = new ContainerBuilder();
            parentBuilder.RegisterValue(parentModel, typeof(ModelA), typeof(IModel));
            parentBuilder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            using var parentCtx = new GameContext(parentBuilder.Build(), inheritFromGlobal: false);

            // 子级 Container 设置 parent，运行期再注册一个 ModelA
            var childBuilder = new ContainerBuilder();
            childBuilder.SetParent(ContextInternals.GetContainer(parentCtx));
            using var childCtx = new GameContext(childBuilder.Build(), inheritFromGlobal: false);

            var childModel = new ModelA { Tag = "child" };
            childCtx.RegisterModel(childModel);

            // 子级解析应当拿到子级实例（不抛异常）
            Assert.AreSame(childModel, childCtx.GetModel<ModelA>(),
                "子 Context 运行期 RegisterModel 应当覆盖父级 InstallBindings 同型注册");
            // 父级解析依然拿到父级实例
            Assert.AreSame(parentModel, parentCtx.GetModel<ModelA>(),
                "父 Context 不受子级运行期注册影响");
        }

        // ── owned 注册：Context 拥有的 IDisposable 随 Dispose 释放，普通 RegisterValue 不动 ──

        private sealed class TrackedDisposable : IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private interface ITrackedDisposable { }

        private sealed class MultiContractDisposable : IDisposable, ITrackedDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private sealed class ThrowingInjectedDisposable : IDisposable
        {
            public int DisposeCount;

            [Inject]
            private void Initialize() => throw new InvalidOperationException("inject-boom");

            public void Dispose() => DisposeCount++;
        }

        [Test]
        public void RegisterOwned_DisposedOnContextDispose()
        {
            var owned = new TrackedDisposable();
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            builder.RegisterOwned(owned, typeof(TrackedDisposable));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            Assert.AreSame(owned, ctx.Resolve(typeof(TrackedDisposable)), "RegisterOwned 也应能正常解析");
            Assert.AreEqual(0, owned.DisposeCount, "Dispose 前不应被释放");

            ctx.Dispose();
            Assert.AreEqual(1, owned.DisposeCount, "Context.Dispose 应释放 owned 实例");
        }

        [Test]
        public void RegisterValue_NotDisposedOnContextDispose()
        {
            var notOwned = new TrackedDisposable();
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            builder.RegisterValue(notOwned, typeof(TrackedDisposable));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            ctx.Dispose();
            Assert.AreEqual(0, notOwned.DisposeCount,
                "普通 RegisterValue 不被容器拥有，Dispose 不应释放外部实例");
        }

        [Test]
        public void RegisterOwned_ContextDispose_Idempotent()
        {
            var owned = new TrackedDisposable();
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            builder.RegisterOwned(owned, typeof(TrackedDisposable));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            ctx.Dispose();
            ctx.Dispose();
            Assert.AreEqual(1, owned.DisposeCount, "重复 Dispose 应幂等，owned 只释放一次");
        }

        private sealed class OrderedDisposable : IDisposable
        {
            private readonly List<string> _log;
            private readonly string _id;
            public OrderedDisposable(List<string> log, string id) { _log = log; _id = id; }
            public void Dispose() => _log.Add(_id);
        }

        private sealed class ThrowingOrderedDisposable : IDisposable
        {
            private readonly List<string> _log;
            public ThrowingOrderedDisposable(List<string> log) => _log = log;

            public void Dispose()
            {
                _log.Add("bad");
                throw new InvalidOperationException("owned-dispose-boom");
            }
        }

        [Test]
        public void BuilderDispose_BeforeBuild_ReleasesOwnedInReverseOrder_AndIsIdempotent()
        {
            var log = new List<string>();
            var builder = new ContainerBuilder();
            builder.RegisterOwned(new OrderedDisposable(log, "A"), typeof(OrderedDisposable));
            builder.RegisterOwned(new OrderedDisposable(log, "B"), typeof(OrderedDisposable));

            builder.Dispose();
            builder.Dispose();

            CollectionAssert.AreEqual(new[] { "B", "A" }, log,
                "Build 前 Builder 是临时 owner；放弃事务时也必须 LIFO 且幂等释放");
            Assert.Throws<ObjectDisposedException>(() => builder.Build());
            Assert.Throws<ObjectDisposedException>(
                () => builder.RegisterValue(new ModelA(), typeof(ModelA)));
        }

        [Test]
        public void BuilderRollback_WhenOwnedDisposeThrows_StillReleasesRemainingResources()
        {
            var log = new List<string>();
            var builder = new ContainerBuilder();
            builder.RegisterOwned(new OrderedDisposable(log, "A"), typeof(OrderedDisposable));
            builder.RegisterOwned(new ThrowingOrderedDisposable(log), typeof(ThrowingOrderedDisposable));
            builder.RegisterOwned(new OrderedDisposable(log, "C"), typeof(OrderedDisposable));

            LogAssert.Expect(LogType.Error,
                new Regex(@"\[ContainerBuilder\].*托管服务.*释放期间.*其余服务.*继续释放"));
            LogAssert.Expect(LogType.Exception,
                new Regex(@"InvalidOperationException: owned-dispose-boom"));
            Assert.DoesNotThrow(builder.Dispose);

            CollectionAssert.AreEqual(new[] { "C", "bad", "A" }, log,
                "一个坏 Dispose 不得阻断事务中其余 owned 资源的逆序清理");
        }

        [Test]
        public void Build_TransfersOwnership_BuilderDisposeDoesNotReleaseEarly()
        {
            var owned = new TrackedDisposable();
            var builder = new ContainerBuilder();
            builder.RegisterOwned(owned, typeof(TrackedDisposable));
            var container = builder.Build();

            builder.Dispose();
            Assert.AreEqual(0, owned.DisposeCount, "Build 成功后 owner 已转为 Container");

            using var context = new GameContext(container, inheritFromGlobal: false);
            context.Dispose();
            Assert.AreEqual(1, owned.DisposeCount);
        }

        [Test]
        public void GameContextConstructor_WhenInjectionThrows_RollsBackContainerOwnership()
        {
            var owned = new ThrowingInjectedDisposable();
            using var builder = new ContainerBuilder();
            builder.RegisterOwned(owned, typeof(ThrowingInjectedDisposable));

            var error = Assert.Catch<Exception>(
                () => _ = new GameContext(builder.Build(), inheritFromGlobal: false));

            StringAssert.Contains("inject-boom", error.ToString());
            Assert.AreEqual(1, owned.DisposeCount,
                "GameContext 构造未返回时，必须自行回滚已经接手的 Container owned 资源");
        }

        [Test]
        public void RegisterOwned_DisposedInReverseRegistrationOrder()
        {
            var log = new List<string>();
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            builder.RegisterOwned(new OrderedDisposable(log, "A"), typeof(OrderedDisposable));
            builder.RegisterOwned(new OrderedDisposable(log, "B"), typeof(OrderedDisposable));
            builder.RegisterOwned(new OrderedDisposable(log, "C"), typeof(OrderedDisposable));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            ctx.Dispose();
            Assert.AreEqual(new[] { "C", "B", "A" }, log.ToArray(),
                "owned 应逆序（LIFO）释放——后注册的先 Dispose");
        }

        [Test]
        public void RegisterOwned_ParentAndChild_DisposeIndependently()
        {
            var log = new List<string>();
            var parentBuilder = new ContainerBuilder();
            parentBuilder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            parentBuilder.RegisterOwned(new OrderedDisposable(log, "parent"), typeof(OrderedDisposable));
            using var parentCtx = new GameContext(parentBuilder.Build(), inheritFromGlobal: false);

            var childBuilder = new ContainerBuilder();
            childBuilder.SetParent(ContextInternals.GetContainer(parentCtx));
            childBuilder.RegisterOwned(new OrderedDisposable(log, "child"), typeof(OrderedDisposable));
            using var childCtx = new GameContext(childBuilder.Build(), inheritFromGlobal: false);

            childCtx.Dispose();
            Assert.AreEqual(new[] { "child" }, log.ToArray(),
                "子 Context Dispose 只释放自己的 owned，不连带父级");

            parentCtx.Dispose();
            Assert.AreEqual(new[] { "child", "parent" }, log.ToArray(),
                "父 Context Dispose 释放自己的 owned，不受子级影响");
        }

        [Test]
        public void RegisterValue_IncompatibleContract_FailsAtRegistration()
        {
            var builder = new ContainerBuilder();

            var error = Assert.Throws<ArgumentException>(
                () => builder.RegisterValue(new ModelA(), typeof(SystemA)));

            StringAssert.Contains("不能赋给契约", error.Message,
                "错误应在注册边界暴露，而不是拖到后续 Resolve/强转位置");
            StringAssert.Contains(nameof(ModelA), error.Message);
            StringAssert.Contains(nameof(SystemA), error.Message);
        }

        [Test]
        public void RegisterFactory_IncompatibleResult_FailsOnFirstResolve()
        {
            var builder = new ContainerBuilder();
            builder.RegisterFactory(_ => new ModelA(), typeof(SystemA));
            var container = builder.Build();

            var error = Assert.Throws<InvalidOperationException>(() => container.Resolve(typeof(SystemA)));

            StringAssert.Contains("不能赋给契约", error.Message);
            StringAssert.Contains(nameof(ModelA), error.Message);
            StringAssert.Contains(nameof(SystemA), error.Message);
        }

        [Test]
        public void RegisterValue_DelegateWithFactorySignature_RemainsAValue()
        {
            Func<Container, object> callback = _ => new ModelA();
            var builder = new ContainerBuilder();
            builder.RegisterValue(callback, typeof(Func<Container, object>));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            Assert.AreSame(callback, ctx.Resolve(typeof(Func<Container, object>)),
                "值/工厂必须由绑定类型显式区分，不能因为值恰好是 Func<Container, object> 就误执行");
        }

        [Test]
        public void RegisterFactory_CircularResolution_FailsAtCycleBoundary()
        {
            var builder = new ContainerBuilder();
            builder.RegisterFactory(
                c => c.Resolve(typeof(ModelA)),
                typeof(ModelA));
            var container = builder.Build();

            var error = Assert.Throws<InvalidOperationException>(() => container.Resolve(typeof(ModelA)));

            StringAssert.Contains("工厂循环解析", error.Message,
                "循环依赖应在工厂边界给出可诊断错误，而不是递归到栈溢出");
            StringAssert.Contains(nameof(ModelA), error.Message);
        }

        [Test]
        public void RegisterOwned_SameInstanceAcrossCalls_DisposedExactlyOnce()
        {
            var owned = new MultiContractDisposable();
            var builder = new ContainerBuilder();
            builder.RegisterOwned(owned, typeof(MultiContractDisposable));
            builder.RegisterOwned(owned, typeof(ITrackedDisposable));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            ctx.Dispose();

            Assert.AreEqual(1, owned.DisposeCount,
                "所有权按对象身份去重；补注册另一个 contract 不应重复登记 Dispose");
        }

        [Test]
        public void RegisterOwnedFactory_LazySingleton_DisposedWithContext()
        {
            var owned = new MultiContractDisposable();
            int createCount = 0;
            var builder = new ContainerBuilder();
            builder.RegisterOwnedFactory(_ =>
                {
                    createCount++;
                    return owned;
                },
                typeof(MultiContractDisposable), typeof(ITrackedDisposable));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            Assert.AreEqual(0, createCount, "Lazy owned factory 不应在 Build 时构造");
            Assert.AreSame(owned, ctx.Resolve(typeof(ITrackedDisposable)));
            Assert.AreSame(owned, ctx.Resolve(typeof(MultiContractDisposable)));
            Assert.AreEqual(1, createCount, "多 contract 仍共享一个 Singleton");

            ctx.Dispose();
            Assert.AreEqual(1, owned.DisposeCount, "工厂产物应随 Context 释放且只释放一次");
        }

        [Test]
        public void RegisterOwnedFactory_MultiContract_DiagnosticsShareResolvedState()
        {
            var builder = new ContainerBuilder();
            builder.RegisterOwnedFactory(
                _ => new MultiContractDisposable(),
                typeof(MultiContractDisposable), typeof(ITrackedDisposable));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
            var container = ContextInternals.GetContainer(ctx);

            Assert.IsTrue(container.LocalRegistrationDetails.All(d => d.IsPendingFactory));
            var resolved = ctx.Resolve(typeof(ITrackedDisposable));
            var details = container.LocalRegistrationDetails.ToArray();

            Assert.IsTrue(details.All(d => !d.IsPendingFactory),
                "多 contract 共用一个 Binding；任一 contract 首次解析后，诊断不应把其余 contract 误报为待构造");
            Assert.IsTrue(details.All(d => ReferenceEquals(resolved, d.Instance)));
        }

        [Test]
        public void RegisterOwnedFactory_NonDisposableResult_FailsOnResolve()
        {
            var builder = new ContainerBuilder();
            builder.RegisterOwnedFactory(_ => new ModelA(), typeof(ModelA));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            var error = Assert.Throws<InvalidOperationException>(() => ctx.Resolve(typeof(ModelA)));

            StringAssert.Contains("未实现 IDisposable", error.Message);
            StringAssert.Contains(nameof(ModelA), error.Message);
        }

        [Test]
        public void Build_WhenLaterEagerFactoryFails_ReleasesCreatedOwnedFactoryResult()
        {
            var owned = new MultiContractDisposable();
            using var builder = new ContainerBuilder();
            builder.RegisterOwnedFactory(_ => owned, Resolution.Eager, typeof(MultiContractDisposable));
            builder.RegisterFactory(
                _ => throw new InvalidOperationException("boom"),
                Resolution.Eager,
                typeof(ModelA));

            Assert.Throws<InvalidOperationException>(() => builder.Build());
            builder.Dispose();
            Assert.AreEqual(1, owned.DisposeCount,
                "Build 失败时 Container 回滚 owned；外层 Builder.Dispose 不得二次释放");
        }

        [Test]
        public void Resolve_AfterContextDispose_ThrowsWithoutConstructingLazyFactory()
        {
            int createCount = 0;
            var builder = new ContainerBuilder();
            builder.RegisterOwnedFactory(_ =>
                {
                    createCount++;
                    return new MultiContractDisposable();
                },
                typeof(MultiContractDisposable));
            var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
            ctx.Dispose();

            Assert.Throws<ObjectDisposedException>(() => ctx.Resolve(typeof(MultiContractDisposable)));
            Assert.AreEqual(0, createCount,
                "已 Dispose Context 不得通过未解析的 Lazy factory 复活新服务");
        }
    }
}
