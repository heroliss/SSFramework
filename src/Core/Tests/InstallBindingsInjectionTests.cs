using System;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.Utility;
using NUnit.Framework;

namespace Game.Framework.Test
{
    /// <summary>
    /// 测试构建期值绑定的自动注入语义（ADR-0019）：RegisterValue / RegisterOwned 的实例在 GameContext
    /// 构造时统一 Inject + AttachTo（与 Mono 路径「注册即注入」对称）；工厂产物与被覆盖的孤儿实例不注入。
    /// 生成的服务安装器（ServiceInstallerGenerator）依赖这套语义开箱可用。
    /// </summary>
    public class InstallBindingsInjectionTests
    {
        private sealed class ProbeUtility : IUtility { }

        /// <summary>探针服务：[Inject] 字段 + [Inject] 方法（计数验证去重）+ IHasGameContext（验证 AttachTo）。</summary>
        private sealed class ProbeService : ISystem, IHasGameContext
        {
            private GameContext _ctx;
            IGameContext IHasGameContext.Context => _ctx;

            [Inject] public ProbeUtility Utility;

            public int InjectMethodCalls;
            [Inject] private void OnInjected(ProbeUtility _) => InjectMethodCalls++;
        }

        private sealed class DisposableProbeService : ISystem, IHasGameContext, IDisposable
        {
            private GameContext _ctx;
            IGameContext IHasGameContext.Context => _ctx;

            [Inject] public ProbeUtility Utility;
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class AffinityModel : IModel, IHasGameContext
        {
            private GameContext _ctx;
            IGameContext IHasGameContext.Context => _ctx;
        }

        private sealed class AffinityUtility : IUtility, IHasGameContext
        {
            private GameContext _ctx;
            IGameContext IHasGameContext.Context => _ctx;
        }

        [Test]
        public void RegisterValue_InstanceIsInjectedAndAttached_OnContextConstruction()
        {
            var utility = new ProbeUtility();
            var service = new ProbeService();

            var builder = new ContainerBuilder();
            builder.RegisterValue(utility, typeof(ProbeUtility));
            builder.RegisterValue(service, typeof(ProbeService));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            Assert.AreSame(utility, service.Utility, "值绑定实例的 [Inject] 字段应在 GameContext 构造时注入");
            Assert.AreSame(ctx, ((IHasGameContext)service).Context, "值绑定实例应被 AttachTo（GameContext 字段回写）");
        }

        [Test]
        public void RegisterOwned_InstanceIsInjectedAndAttached_AndStillDisposedWithContext()
        {
            var utility = new ProbeUtility();
            var service = new DisposableProbeService();

            var builder = new ContainerBuilder();
            builder.RegisterValue(utility, typeof(ProbeUtility));
            builder.RegisterOwned(service, typeof(DisposableProbeService));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            Assert.AreSame(utility, service.Utility, "RegisterOwned 实例同样应被自动注入");
            Assert.AreSame(ctx, ((IHasGameContext)service).Context, "RegisterOwned 实例同样应被 AttachTo");

            ctx.Dispose();
            Assert.IsTrue(service.Disposed, "自动注入不应影响 RegisterOwned 的 Dispose 语义");
        }

        [Test]
        public void RegisterValue_MultiContractSameInstance_InjectedExactlyOnce()
        {
            var service = new ProbeService();

            var builder = new ContainerBuilder();
            builder.RegisterValue(new ProbeUtility(), typeof(ProbeUtility));
            builder.RegisterValue(service, typeof(ProbeService), typeof(ISystem));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            Assert.AreEqual(1, service.InjectMethodCalls, "同一实例多契约注册应只注入一次（按引用去重）");
        }

        [Test]
        public void RegisterFactory_ProductIsNotAutoInjected()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new ProbeUtility(), typeof(ProbeUtility));
            builder.RegisterFactory(_ => new ProbeService(), typeof(ProbeService));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            var service = (ProbeService)ctx.Resolve(typeof(ProbeService));
            Assert.IsNull(service.Utility, "工厂产物不自动注入——工厂是显式接线位（经 Container 参数 Resolve）");
            Assert.IsNull(((IHasGameContext)service).Context, "工厂产物也不自动 AttachTo");
        }

        [Test]
        public void RegisterValue_OverriddenOrphan_IsNotInjected()
        {
            var orphan = new ProbeService();
            var effective = new ProbeService();

            var builder = new ContainerBuilder();
            builder.RegisterValue(new ProbeUtility(), typeof(ProbeUtility));
            builder.RegisterValue(orphan, typeof(ProbeService));
            builder.RegisterValue(effective, typeof(ProbeService)); // 后注册覆盖先注册
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            Assert.IsNotNull(effective.Utility, "构建完成时仍生效的实例应被注入");
            Assert.IsNull(orphan.Utility, "被覆盖的孤儿实例不应被注入（不在最终绑定表里）");
        }

        [Test]
        public void ContextConstruction_ValueAlreadyAttachedElsewhere_FailsBeforeAnyInjectionAndRollsBack()
        {
            var utilityA = new ProbeUtility();
            var service = new ProbeService();
            using var builderA = new ContainerBuilder();
            builderA.RegisterValue(utilityA, typeof(ProbeUtility));
            builderA.RegisterValue(service, typeof(ProbeService));
            using var contextA = new GameContext(builderA.Build(), inheritFromGlobal: false);

            Assert.AreSame(utilityA, service.Utility);
            Assert.AreEqual(1, service.InjectMethodCalls);
            Assert.AreSame(contextA, ((IHasGameContext)service).Context);

            var utilityB = new ProbeUtility();
            var rollbackProbe = new DisposableProbeService();
            using var builderB = new ContainerBuilder();
            builderB.RegisterValue(utilityB, typeof(ProbeUtility));
            builderB.RegisterValue(service, typeof(ProbeService));
            builderB.RegisterOwned(rollbackProbe, typeof(DisposableProbeService));

            var error = Assert.Throws<InvalidOperationException>(
                () => _ = new GameContext(builderB.Build(), inheritFromGlobal: false));

            StringAssert.Contains(nameof(ProbeService), error.Message);
            StringAssert.Contains("另一个 Context", error.Message);
            Assert.AreSame(utilityA, service.Utility,
                "Context affinity 冲突必须在整批注入前失败，不能把实例字段改成第二个 Context 的依赖。");
            Assert.AreEqual(1, service.InjectMethodCalls,
                "失败的第二次装配不能执行任何 [Inject] 方法。");
            Assert.AreSame(contextA, ((IHasGameContext)service).Context,
                "失败后扩展方法仍应解析最初 Context，不能形成依赖快照与实时解析两份真相。");
            Assert.IsNull(rollbackProbe.Utility,
                "预检失败前不应注入同批次中的其它值实例，无论字典枚举顺序如何。");
            Assert.IsNull(((IHasGameContext)rollbackProbe).Context);
            Assert.IsTrue(rollbackProbe.Disposed,
                "GameContext 构造失败仍须回滚第二个 Container 已接管的 owned 资源。");
        }

        [Test]
        public void AttachTo_SameContextIsIdempotent_DifferentContextFailsFast()
        {
            var service = new ProbeService();
            using var builderA = new ContainerBuilder();
            builderA.RegisterValue(service, typeof(ProbeService));
            using var contextA = new GameContext(builderA.Build(), inheritFromGlobal: false);
            using var builderB = new ContainerBuilder();
            using var contextB = new GameContext(builderB.Build(), inheritFromGlobal: false);

            Assert.DoesNotThrow(() => contextA.AttachTo(service),
                "重复附着到同一 Context 应保持幂等，便于装配入口安全组合。");

            var error = Assert.Throws<InvalidOperationException>(() => contextB.AttachTo(service));

            StringAssert.Contains(nameof(ProbeService), error.Message);
            StringAssert.Contains("另一个 Context", error.Message);
            Assert.AreSame(contextA, ((IHasGameContext)service).Context);
        }

        [Test]
        public void Inject_ValueAttachedElsewhere_FailsBeforeChangingInjectedSnapshot()
        {
            var utilityA = new ProbeUtility();
            var service = new ProbeService();
            using var builderA = new ContainerBuilder();
            builderA.RegisterValue(utilityA, typeof(ProbeUtility));
            builderA.RegisterValue(service, typeof(ProbeService));
            using var contextA = new GameContext(builderA.Build(), inheritFromGlobal: false);

            var utilityB = new ProbeUtility();
            using var builderB = new ContainerBuilder();
            builderB.RegisterValue(utilityB, typeof(ProbeUtility));
            using var contextB = new GameContext(builderB.Build(), inheritFromGlobal: false);

            var error = Assert.Throws<InvalidOperationException>(() => contextB.Inject(service));

            StringAssert.Contains("另一个 Context", error.Message);
            Assert.AreSame(utilityA, service.Utility,
                "Inject 的 Context 归属预检必须发生在字段写入前，不能把 A 的实例污染成 B 的依赖快照。");
            Assert.AreEqual(1, service.InjectMethodCalls);
            Assert.AreSame(contextA, ((IHasGameContext)service).Context);
        }

        [TestCase("Model")]
        [TestCase("System")]
        [TestCase("Utility")]
        public void RuntimeRegister_ValueAttachedElsewhere_FailsBeforeContainerMutation(string layer)
        {
            using var builderA = new ContainerBuilder();
            using var contextA = new GameContext(builderA.Build(), inheritFromGlobal: false);
            using var builderB = new ContainerBuilder();
            using var contextB = new GameContext(builderB.Build(), inheritFromGlobal: false);

            object instance;
            Type concreteType;
            TestDelegate register;
            switch (layer)
            {
                case "Model":
                    var model = new AffinityModel();
                    instance = model;
                    concreteType = typeof(AffinityModel);
                    register = () => contextB.RegisterModel(model);
                    break;
                case "System":
                    var system = new ProbeService();
                    instance = system;
                    concreteType = typeof(ProbeService);
                    register = () => contextB.RegisterSystem(system);
                    break;
                default:
                    var utility = new AffinityUtility();
                    instance = utility;
                    concreteType = typeof(AffinityUtility);
                    register = () => contextB.RegisterUtility(utility);
                    break;
            }

            contextA.AttachTo(instance);
            var error = Assert.Throws<InvalidOperationException>(register);

            StringAssert.Contains("另一个 Context", error.Message);
            Assert.AreSame(contextA, ((IHasGameContext)instance).Context);
            Assert.IsFalse(contextB.TryResolve(concreteType, out _),
                "归属冲突必须在动态注册的完整 contract 集写入前失败，不能给 Context B 留下 override。");
        }
    }
}
