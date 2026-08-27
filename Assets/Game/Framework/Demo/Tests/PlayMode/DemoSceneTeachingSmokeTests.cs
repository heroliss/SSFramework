#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Demo.Modules;
using Game.Framework.Internal;
using Game.Framework.UI.Toolkit;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Game.Framework.Demo.PlayMode.Tests
{
    /// <summary>
    /// 穿过真实 DemoScene Composition Root 与 Shell 逐章构建，验证 32 个章节的运行时教学契约和缺依赖降级路径。
    /// </summary>
    public sealed class DemoSceneTeachingSmokeTests
    {
        private const string DemoScenePath = "Assets/Game/Framework/Demo/Scenes/DemoScene.unity";

        private bool _loadedDemoScene;
        private bool _previousRunInBackground;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return UniTask.ToCoroutine(async () =>
            {
                _previousRunInBackground = Application.runInBackground;
                Application.runInBackground = true;

                Scene demoScene = SceneManager.GetSceneByPath(DemoScenePath);
                if (!demoScene.IsValid() || !demoScene.isLoaded)
                {
                    demoScene = EditorSceneManager.LoadSceneInPlayMode(
                        DemoScenePath,
                        new LoadSceneParameters(LoadSceneMode.Additive));
                    Assert.IsTrue(demoScene.IsValid(), $"无法加载 Demo 冒烟场景：{DemoScenePath}");
                    _loadedDemoScene = true;
                }

                await WaitUntil(
                    () => FindDemoObject<MonoDemoContext>() is { IsDisposed: false } &&
                          FindDemoObject<DemoShellController>() is { enabled: true },
                    "Demo Context 与 Shell 初始化",
                    30f);
            });
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            yield return UniTask.ToCoroutine(async () =>
            {
                try
                {
                    var shell = FindDemoObject<DemoShellController>();
                    if (shell != null && shell.Modules.Count > 0)
                        shell.SelectChapter(shell.Modules[0]);

                    if (_loadedDemoScene)
                    {
                        Scene scene = SceneManager.GetSceneByPath(DemoScenePath);
                        if (scene.IsValid() && scene.isLoaded)
                        {
                            AsyncOperation operation = SceneManager.UnloadSceneAsync(scene);
                            if (operation != null) await operation.ToUniTask();
                        }
                    }
                }
                finally
                {
                    _loadedDemoScene = false;
                    Application.runInBackground = _previousRunInBackground;
                }
            });
        }

        [UnityTest]
        public IEnumerator EveryCatalogChapter_BuildsInTheRealSceneAndSatisfiesItsTeachingContract()
        {
            yield return UniTask.ToCoroutine(async () =>
            {
                var shell = FindDemoObject<DemoShellController>();
                Assert.IsNotNull(shell);
                Assert.AreEqual(32, shell.Modules.Count);

                foreach (var module in shell.Modules)
                {
                    Assert.DoesNotThrow(
                        () => shell.SelectChapter(module),
                        $"真实 DemoScene 构建章节失败：{module.Id} / {module.Title}");
                    Assert.AreSame(module, shell.CurrentModule, $"Shell 未成功切到章节：{module.Id}");
                    await UniTask.Yield(PlayerLoopTiming.Update);
                }
            });
        }

        [Test]
        public void RealUguiAndUiFrameworkModules_FallBackToStructuredUnavailablePages()
        {
            var assets = FindDemoObject<DemoUGuiAssets>();
            Assert.IsNotNull(assets, "真实场景应先具备 UGUI 正常路径资产，测试才有意义。");
            bool wasActive = assets.gameObject.activeSelf;
            try
            {
                assets.gameObject.SetActive(false);
                AssertStandaloneFallbackBuilds(new UGuiViewModule());
                AssertStandaloneFallbackBuilds(new UIFrameworkModule());
            }
            finally
            {
                if (assets != null) assets.gameObject.SetActive(wasActive);
            }
        }

        [Test]
        public void InputSystemBackWiring_LivesBesideTheUiEntryInDemoComposition()
        {
            var ui = FindDemoObject<MonoToolkitUI>();
            var driver = FindDemoObject<DemoInputSystemBackKeyDriver>();

            Assert.IsNotNull(ui, "真实 DemoScene 必须有 Toolkit UI 入口。");
            Assert.IsNotNull(driver,
                "Demo 的 Input System 返回键样板丢失；同时检查场景是否出现 Missing Script。");
            Assert.AreSame(ui.gameObject, driver.gameObject,
                "样板通过同节点解析 IUIUtility；移动组件后必须保持 composition 接线关系。");
        }

        [UnityTest]
        public IEnumerator InputSystemBackWiring_EscapeClosesTheRealTopWindow()
        {
            var ui = FindDemoObject<MonoToolkitUI>();
            Assert.IsNotNull(ui);
            ui.CloseAll();
            DemoCachedPolicyWindow window = null;
            yield return UniTask.ToCoroutine(async () => window = await ui.Open<DemoCachedPolicyWindow>());
            Assert.IsNotNull(window);

            Keyboard keyboard = InputSystem.AddDevice<Keyboard>();
            try
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
                yield return null;

                Assert.AreEqual(1, window.EvidenceCloseCalls,
                    "合成 Esc 应穿过 Demo input composition，调用真实 IUIUtility.Back() 关闭栈顶窗口。 ");
            }
            finally
            {
                if (keyboard != null && keyboard.added)
                    InputSystem.RemoveDevice(keyboard);
                ui.CloseAll();
            }
        }

        [UnityTest]
        public IEnumerator InputSystemBackWiring_MissingUiEntryLogsOnceAndDisablesItself()
        {
            var host = new GameObject("Back Driver Missing UI Test");
            var driver = host.AddComponent<DemoInputSystemBackKeyDriver>();
            Keyboard keyboard = InputSystem.AddDevice<Keyboard>();
            try
            {
                LogAssert.Expect(LogType.Error,
                    new System.Text.RegularExpressions.Regex("同节点上没有 UI 入口.*自动停用"));
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
                yield return null;

                Assert.IsFalse(driver.enabled, "配置错误只报告一次并停用，不能每帧刷 Console。 ");
            }
            finally
            {
                if (keyboard != null && keyboard.added)
                    InputSystem.RemoveDevice(keyboard);
                Object.Destroy(host);
            }
        }

        [UnityTest]
        public IEnumerator RealToolkitBackend_DestroyRecreatesWhileCacheReusesTheSameWindow()
        {
            yield return UniTask.ToCoroutine(async () =>
            {
                var ui = FindDemoObject<MonoToolkitUI>();
                Assert.IsNotNull(ui, "真实 DemoScene 必须有 Toolkit UI Adapter，本测试才覆盖真实窗口实例。");
                ui.CloseAll();
                try
                {
                    var destroyFirst = await ui.Open<DemoDestroyPolicyWindow>();
                    Assert.IsNotNull(destroyFirst);
                    Assert.AreEqual(1, destroyFirst.EvidenceCreateCalls);
                    Assert.AreEqual(1, destroyFirst.EvidenceOpenCalls);
                    ui.Close(destroyFirst);

                    var destroySecond = await ui.Open<DemoDestroyPolicyWindow>();
                    Assert.AreNotSame(destroyFirst, destroySecond, "Destroy 策略重开必须构造新窗口实例");
                    Assert.AreNotEqual(destroyFirst.EvidenceInstanceId, destroySecond.EvidenceInstanceId);
                    Assert.AreEqual(1, destroySecond.EvidenceCreateCalls);
                    Assert.AreEqual(1, destroySecond.EvidenceOpenCalls);
                    ui.Close(destroySecond);

                    var cachedFirst = await ui.Open<DemoCachedPolicyWindow>();
                    Assert.IsNotNull(cachedFirst);
                    Assert.AreEqual(1, cachedFirst.EvidenceCreateCalls);
                    Assert.AreEqual(1, cachedFirst.EvidenceOpenCalls);
                    ui.Close(cachedFirst);
                    Assert.AreEqual(1, cachedFirst.EvidenceCloseCalls);

                    var cachedSecond = await ui.Open<DemoCachedPolicyWindow>();
                    Assert.AreSame(cachedFirst, cachedSecond, "Cache 策略重开必须复用同一窗口实例");
                    Assert.AreEqual(cachedFirst.EvidenceInstanceId, cachedSecond.EvidenceInstanceId);
                    Assert.AreEqual(1, cachedSecond.EvidenceCreateCalls, "缓存重开不能再次 OnCreate");
                    Assert.AreEqual(2, cachedSecond.EvidenceOpenCalls, "缓存重开仍须再次 OnOpen 刷新参数与状态");
                    Assert.AreEqual(1, cachedSecond.EvidenceCloseCalls);
                }
                finally
                {
                    // 即便中途断言失败，也不能把真实窗口残留给同批后续场景用例。
                    ui.CloseAll();
                }
            });
        }

        private static void AssertStandaloneFallbackBuilds(IDemoModule module)
        {
            using var catalog = new DemoModuleCatalog(new[] { module });
            using var builder = new ContainerBuilder();
            catalog.InstallBindings(builder);
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            catalog.Initialize(context);

            var content = new VisualElement();
            Assert.DoesNotThrow(() => catalog.Activate(module, content), $"{module.Id} 降级页未通过教学契约");
            Assert.IsTrue(
                content.Query<Label>().ToList().Any(label => label.text == "本章当前暂不可运行"),
                $"{module.Id} 应明确呈现结构化不可用状态");
            catalog.Deactivate();
        }

        private static T FindDemoObject<T>() where T : Object
            => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(candidate => candidate != null && candidate is Component component &&
                                             component.gameObject.scene.path == DemoScenePath);

        private static async UniTask WaitUntil(Func<bool> condition, string milestone, float timeoutSeconds)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            Exception lastException = null;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                try
                {
                    if (condition()) return;
                    lastException = null;
                }
                catch (Exception exception)
                {
                    lastException = exception;
                }
                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            Assert.Fail(
                $"Demo 冒烟等待“{milestone}”超时（{timeoutSeconds:F0}s）。" +
                (lastException == null ? string.Empty : $" 最后异常：{lastException}"));
        }
    }
}
#endif
