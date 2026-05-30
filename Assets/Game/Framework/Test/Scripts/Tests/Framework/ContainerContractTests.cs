using System;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Model;
using Game.Framework.System;
using Game.Framework.Utility;
using NUnit.Framework;

namespace Game.Framework.Test
{
    /// <summary>
    /// 测试 Container 注册契约：构建期 vs 运行期、同层 vs 跨层、覆盖 vs 抛异常。
    /// 这些行为是 AGENTS §12 的合同，必须有测试固化以防回归。
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

            var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
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

            var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
            Assert.AreSame(modelB, ctx.Resolve(typeof(IModel)),
                "ContainerBuilder 构建期 Factory 重复注册也应后注册胜出");
        }

        // ── 运行期：同层重复注册 → 抛 InvalidOperationException ──────────

        [Test]
        public void RegisterModel_DuplicateAtRuntime_Throws()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

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
            var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

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
            var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

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
            var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

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
            var parentCtx = new GameContext(parentBuilder.Build(), inheritFromGlobal: false);

            // 子级 Container 设置 parent，运行期再注册一个 ModelA
            var childBuilder = new ContainerBuilder();
            childBuilder.SetParent(ContextInternals.GetContainer(parentCtx));
            var childCtx = new GameContext(childBuilder.Build(), inheritFromGlobal: false);

            var childModel = new ModelA { Tag = "child" };
            childCtx.RegisterModel(childModel);

            // 子级解析应当拿到子级实例（不抛异常）
            Assert.AreSame(childModel, childCtx.GetModel<ModelA>(),
                "子 Context 运行期 RegisterModel 应当覆盖父级 InstallBindings 同型注册");
            // 父级解析依然拿到父级实例
            Assert.AreSame(parentModel, parentCtx.GetModel<ModelA>(),
                "父 Context 不受子级运行期注册影响");
        }
    }
}
