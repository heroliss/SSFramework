using System;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Internal;
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
    }
}
