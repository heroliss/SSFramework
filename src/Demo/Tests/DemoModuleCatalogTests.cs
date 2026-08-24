using System;
using System.Collections.Generic;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Internal;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Tests
{
    /// <summary>直接穿过 DemoModuleCatalog Interface 锁定同实例生命周期、乱序拒绝和 Build 失败回滚。</summary>
    public sealed class DemoModuleCatalogTests
    {
        [Test]
        public void Lifecycle_UsesSameAdapterAndAllowsRepeatedBuildTeardownPairs()
        {
            var calls = new List<string>();
            var module = new RecordingModule("recording", 0, calls);
            using var catalog = new DemoModuleCatalog(new[] { module });

            Assert.AreSame(module, catalog.Modules[0], "目录必须暴露 InstallBindings 时使用的同一个 Adapter 实例。");
            using var builder = new ContainerBuilder();
            catalog.InstallBindings(builder);
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            catalog.Initialize(context);

            catalog.Activate(module, new VisualElement());
            catalog.Deactivate();
            catalog.Activate(module, new VisualElement());
            catalog.Deactivate();

            CollectionAssert.AreEqual(
                new[] { "install", "initialize", "build", "teardown", "build", "teardown" },
                calls);
            Assert.AreSame(context, module.InitializedContext);
        }

        [Test]
        public void Lifecycle_RejectsOutOfOrderReentryAndForeignAdapters()
        {
            var first = new RecordingModule("first", 0);
            var second = new RecordingModule("second", 1);
            var foreign = new RecordingModule("foreign", 2);
            using var catalog = new DemoModuleCatalog(new[] { first, second });
            using var builder = new ContainerBuilder();

            using (var prematureBuilder = new ContainerBuilder())
            using (var prematureContext = new GameContext(prematureBuilder.Build(), inheritFromGlobal: false))
                Assert.Throws<InvalidOperationException>(() => catalog.Initialize(prematureContext));
            catalog.InstallBindings(builder);
            Assert.Throws<InvalidOperationException>(() => catalog.InstallBindings(builder));

            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            catalog.Initialize(context);
            Assert.Throws<InvalidOperationException>(() => catalog.Initialize(context));
            Assert.Throws<ArgumentException>(() => catalog.Activate(foreign, new VisualElement()));

            catalog.Activate(first, new VisualElement());
            Assert.Throws<InvalidOperationException>(() => catalog.Activate(second, new VisualElement()));
            catalog.Deactivate();
            catalog.Deactivate(); // 幂等：Unity 父子节点销毁先后不确定时允许重复收尾。

            catalog.Dispose();
            Assert.Throws<InvalidOperationException>(() => catalog.Activate(first, new VisualElement()));
        }

        [Test]
        public void BuildFailure_TearsDownSameAdapterAndDoesNotLeaveStickyActiveState()
        {
            var expected = new InvalidOperationException("build-boom");
            var broken = new RecordingModule("broken", 0) { BuildException = expected };
            var healthy = new RecordingModule("healthy", 1);
            using var catalog = new DemoModuleCatalog(new[] { broken, healthy });
            using var builder = new ContainerBuilder();
            catalog.InstallBindings(builder);
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            catalog.Initialize(context);

            var thrown = Assert.Throws<InvalidOperationException>(
                () => catalog.Activate(broken, new VisualElement()));
            Assert.AreSame(expected, thrown, "清理不得覆盖最初的 Build 异常。");
            Assert.AreEqual(1, broken.TeardownCount, "半构建章节也必须 Teardown。");

            catalog.Activate(healthy, new VisualElement());
            catalog.Deactivate();
            Assert.AreEqual(1, healthy.BuildCount, "失败章节不能把 active 状态黏住并阻断下一章。");
        }

        [Test]
        public void Deactivate_RejectsTeardownReentryThenAllowsActivationAfterCleanup()
        {
            var first = new RecordingModule("first", 0);
            var second = new RecordingModule("second", 1);
            using var catalog = CreateInitializedCatalog(new[] { first, second }, out var context);
            using (context)
            {
                Exception reentryException = null;
                first.TeardownAction = () => reentryException = CaptureException(
                    () => catalog.Activate(second, new VisualElement()));

                catalog.Activate(first, new VisualElement());
                catalog.Deactivate();

                Assert.IsInstanceOf<InvalidOperationException>(reentryException);
                Assert.AreEqual(0, second.BuildCount, "Teardown 回调不能在外层清理完成前偷跑下一章。");
                catalog.Activate(second, new VisualElement());
                catalog.Deactivate();
                Assert.AreEqual(1, second.BuildCount, "外层清理完成后应恢复为可激活状态。");
            }
        }

        [Test]
        public void DisposeWhileActive_RejectsTeardownReentryAndLeavesCatalogDisposed()
        {
            var first = new RecordingModule("first", 0);
            var second = new RecordingModule("second", 1);
            using var catalog = CreateInitializedCatalog(new[] { first, second }, out var context);
            using (context)
            {
                Exception reentryException = null;
                first.TeardownAction = () => reentryException = CaptureException(
                    () => catalog.Activate(second, new VisualElement()));

                catalog.Activate(first, new VisualElement());
                catalog.Dispose();

                Assert.IsInstanceOf<InvalidOperationException>(reentryException);
                Assert.AreEqual(1, first.TeardownCount);
                Assert.AreEqual(0, second.BuildCount, "Dispose 期间不得遗留一个新激活但无人拥有的章节。");
                Assert.Throws<InvalidOperationException>(() => catalog.Activate(second, new VisualElement()));
            }
        }

        private static DemoModuleCatalog CreateInitializedCatalog(
            IEnumerable<IDemoModule> modules,
            out GameContext context)
        {
            var catalog = new DemoModuleCatalog(modules);
            using var builder = new ContainerBuilder();
            catalog.InstallBindings(builder);
            context = new GameContext(builder.Build(), inheritFromGlobal: false);
            catalog.Initialize(context);
            return catalog;
        }

        private static Exception CaptureException(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        private sealed class RecordingModule : IDemoModule
        {
            private readonly List<string> _calls;

            internal RecordingModule(string id, int order, List<string> calls = null)
            {
                Id = id;
                Order = order;
                _calls = calls;
            }

            public string Id { get; }
            public string Title => Id;
            public string Category => "入门";
            public int Order { get; }
            public string Summary => "记录生命周期。";
            public bool IsComingSoon => false;
            internal IGameContext InitializedContext { get; private set; }
            internal Exception BuildException { get; set; }
            internal int BuildCount { get; private set; }
            internal int TeardownCount { get; private set; }
            internal Action TeardownAction { get; set; }

            public void InstallBindings(ContainerBuilder builder) => _calls?.Add("install");

            public void Initialize(IGameContext context)
            {
                InitializedContext = context;
                _calls?.Add("initialize");
            }

            public void Build(DemoModuleHost host)
            {
                BuildCount++;
                _calls?.Add("build");
                if (BuildException != null) throw BuildException;
            }

            public void Teardown()
            {
                TeardownCount++;
                _calls?.Add("teardown");
                TeardownAction?.Invoke();
            }
        }
    }
}
