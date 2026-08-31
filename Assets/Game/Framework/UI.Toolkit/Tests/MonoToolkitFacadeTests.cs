using System;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.Test
{
    /// <summary>
    /// UI Toolkit Mono 外观只负责把公共 API 原样转发给渲染中立核心，包括取消令牌与 Loading 句柄所有权。
    /// </summary>
    public sealed class MonoToolkitFacadeTests
    {
        private static readonly FieldInfo CoreField = typeof(MonoToolkitUI).GetField(
            "_core", BindingFlags.Instance | BindingFlags.NonPublic);

        [Test]
        public void BuiltinCalls_ForwardCancellationAndLoadingHandleOwnership()
        {
            Assert.IsNotNull(CoreField);
            AssertLegacyLoadingDeprecation(typeof(MonoToolkitUI));
            using var builder = new ContainerBuilder();
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            var core = new UIUtility(context, new FakeBackend(), new UIBuiltinWindows
            {
                Toast = typeof(ToastWindow),
                Loading = typeof(LoadingWindow),
            });
            var gameObject = new GameObject("mono-toolkit-facade-probe");
            gameObject.SetActive(false);
            var facade = gameObject.AddComponent<MonoToolkitUI>();
            CoreField.SetValue(facade, core);

            try
            {
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();

                Assert.Throws<OperationCanceledException>(() =>
                    facade.ShowToast("取消", ct: canceled.Token).GetAwaiter().GetResult());
#pragma warning disable CS0618 // 有意锁定迁移期旧入口仍透传取消；生产代码不得调用。
                Assert.Throws<OperationCanceledException>(() =>
                    facade.ShowLoading("取消", canceled.Token).GetAwaiter().GetResult());
#pragma warning restore CS0618
                Assert.Throws<OperationCanceledException>(() =>
                    facade.AcquireLoading("取消", canceled.Token).GetAwaiter().GetResult());

                var handle = facade.AcquireLoading("有效").GetAwaiter().GetResult();
                Assert.IsTrue(handle.IsActive);
                Assert.IsTrue(core.IsOpen<LoadingWindow>());

                handle.Dispose();
                Assert.IsFalse(core.IsOpen<LoadingWindow>());

#pragma warning disable CS0618 // 有意锁定迁移期 Show/Hide 两个转发成员仍组成完整兼容对。
                facade.ShowLoading("兼容 owner").GetAwaiter().GetResult();
                Assert.IsTrue(core.IsOpen<LoadingWindow>());
                facade.HideLoading();
                Assert.IsFalse(core.IsOpen<LoadingWindow>());
#pragma warning restore CS0618
            }
            finally
            {
                CoreField.SetValue(facade, null);
                core.Dispose();
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DestroyedFacade_OldInterfaceCannotCreateLazyCore()
        {
            var contextObject = new GameObject("toolkit-terminal-context");
            var uiObject = new GameObject("toolkit-terminal-facade");
            try
            {
                var context = contextObject.AddComponent<MonoGameContextBase>();
                uiObject.transform.SetParent(contextObject.transform);
                var facade = uiObject.AddComponent<MonoToolkitUI>();
                IUIUtility stale = context.GetUtility<IUIUtility>();
                Assert.AreSame(facade, stale);

                UnityEngine.Object.DestroyImmediate(uiObject);

                Assert.Throws<ObjectDisposedException>(() => stale.IsOpen<TestWindow>(),
                    "从未创建过内核的 Mono 外壳销毁后也必须停在终态，不能补建 UIDocument / UIUtility");
            }
            finally
            {
                if (uiObject != null) UnityEngine.Object.DestroyImmediate(uiObject);
                UnityEngine.Object.DestroyImmediate(contextObject);
            }
        }

        private sealed class FakeBackend : IUIBackend
        {
            public void Initialize() { }

            public UniTask<IUIWindow> CreateWindow(
                UIWindowMeta meta, IGameContext context, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return UniTask.FromResult((IUIWindow)Activator.CreateInstance(meta.WindowType));
            }

            public void BringToFront(IUIWindow window) { }
            public void SetVisible(IUIWindow window, bool visible) { }
            public void SetModalMask(IUIWindow ownerWindow, bool on) { }
            public void DestroyWindow(IUIWindow window) { }
            public void SetInputBlocked(bool blocked) { }
            public void Teardown() { }
        }

        private class TestWindow : IUIWindow
        {
            public void OnCreate() { }
            public void OnOpen(object args) { }
            public void OnClose() { }
            public void OnCover() { }
            public void OnReveal() { }
            public UniTask OnOpenTransition(CancellationToken ct) => UniTask.CompletedTask;
            public UniTask OnCloseTransition(CancellationToken ct) => UniTask.CompletedTask;
        }

        [UIWindow(Layer = UILayer.Top, Cache = UICachePolicy.Cache)]
        private sealed class ToastWindow : TestWindow { }

        [UIWindow(Layer = UILayer.Top, Cache = UICachePolicy.Cache, Modal = true)]
        private sealed class LoadingWindow : TestWindow { }

        private static void AssertLegacyLoadingDeprecation(Type owner)
        {
            foreach (string methodName in new[] { "ShowLoading", "HideLoading" })
            {
                var method = owner.GetMethod(methodName);
                var obsolete = method == null
                    ? null
                    : (ObsoleteAttribute)Attribute.GetCustomAttribute(method, typeof(ObsoleteAttribute));
                Assert.That(obsolete, Is.Not.Null, $"{owner.Name}.{methodName} 必须保留非破坏性迁移提示。 ");
                Assert.That(obsolete.IsError, Is.False);
                Assert.That(obsolete.Message, Does.Contain(nameof(IUIUtility.AcquireLoading)));
            }
        }
    }
}
