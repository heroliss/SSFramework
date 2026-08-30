#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
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
        private InputSettings.BackgroundBehavior _previousInputBackgroundBehavior;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return UniTask.ToCoroutine(async () =>
            {
                _previousRunInBackground = Application.runInBackground;
                Application.runInBackground = true;
                // Input System 把“PlayerLoop 是否继续”和“失焦设备是否收事件”分成两项设置。
                // MCP 可在后台跑测试；这里只让 fixture 的合成设备忽略焦点，TearDown 必须恢复产品设置。
                _previousInputBackgroundBehavior = InputSystem.settings.backgroundBehavior;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;

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
                    InputSystem.settings.backgroundBehavior = _previousInputBackgroundBehavior;
                    Application.runInBackground = _previousRunInBackground;
                }
            });
        }

        [UnityTest]
        public IEnumerator SemanticChapterTraversal_BuildsEveryModuleInTheRealSceneAndSatisfiesItsTeachingContract()
        {
            yield return UniTask.ToCoroutine(async () =>
            {
                var shell = FindDemoObject<DemoShellController>();
                Assert.IsNotNull(shell);
                Assert.AreEqual(32, shell.Modules.Count);

                // 这里不模拟鼠标，也不逐个派发 Button 点击事件。测试直接走 Shell 与导航按钮共用的语义入口，
                // 让真实选中态、标题、内容和 Catalog 生命周期保持一份真源；每章让出一帧，用于暴露切章后的清理/挂载问题。
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
        public void SidebarNavigationButton_ClickableRoutesToTheSharedSemanticEntry()
        {
            var shell = FindDemoObject<DemoShellController>();
            Assert.IsNotNull(shell);
            Assert.Greater(shell.Modules.Count, 1);
            var target = shell.Modules[1];

            VisualElement root = shell.GetComponent<UIDocument>().rootVisualElement;
            Button button = root.Query<Button>(className: "demo-nav-item").ToList()
                .Single(candidate => candidate.text == target.Title);

            SimulateClick(button);

            Assert.AreSame(target, shell.CurrentModule,
                "左侧导航按钮必须路由到 SelectChapter 语义入口");
            Assert.IsTrue(button.ClassListContains("demo-nav-item--active"),
                "按钮链路除了切换内容，还必须更新真实选中态");
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

        [Test]
        public void ScoreModelNavigation_UsesTheInstanceOwnedByTheRequestedContext()
        {
            var rootContext = FindDemoObject<MonoDemoContext>();
            var subContext = FindDemoObject<DemoSubContext>();
            var rootScore = DemoEditorNav.FindComponentOwnedBy<MonoScoreModel>(rootContext);
            var subScore = DemoEditorNav.FindComponentOwnedBy<MonoScoreModel>(subContext);

            Assert.IsNotNull(rootScore);
            Assert.IsNotNull(subScore);
            Assert.AreNotSame(rootScore, subScore,
                "父、子 Context 的 Inspector 导航必须各自解析自己的 MonoScoreModel。");
            Assert.IsNull(rootScore.GetComponentInParent<DemoSubContext>(true),
                "根作用域导航不能因为 FindFirstObjectByType 的顺序而误选子作用域实例。");
            Assert.AreSame(subContext, subScore.GetComponentInParent<DemoSubContext>(true));
        }

        [Test]
        public void UIToolkitView_PopupSlotImmediatelyFollowsItsTrigger()
        {
            var shell = FindDemoObject<DemoShellController>();
            var module = shell.Modules.Single(candidate => candidate.Id == "uitoolkit-view");
            shell.SelectChapter(module);

            VisualElement root = shell.GetComponent<UIDocument>().rootVisualElement;
            Button trigger = root.Query<Button>().ToList()
                .Single(button => button.text == "弹出 UIToolkit View（无 prefab）");
            VisualElement slot = root.Q<VisualElement>(className: "demo-inline-view-slot");
            VisualElement actionRow = trigger.parent;

            Assert.IsNotNull(slot, "动态 View 应有稳定的就地挂载插槽。");
            Assert.AreSame(actionRow.parent, slot.parent);
            Assert.AreEqual(actionRow.parent.IndexOf(actionRow) + 1, actionRow.parent.IndexOf(slot),
                "弹出的 View 必须紧跟触发按钮，不能再追加到整章最下方。");
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

        // 只在一个窄测试里驱动 UI Toolkit 自己的 Clickable；32 章广度验证继续直达语义入口。
        // 这不移动系统鼠标、不依赖 Editor 焦点，也不会抢用户前台窗口。
        private static void SimulateClick(Button button)
        {
            MethodInfo simulate = typeof(Clickable).GetMethod(
                "SimulateSingleClick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(simulate, "Unity UI Toolkit Clickable 的测试驱动入口已变更。");
            simulate.Invoke(button.clickable, new object[] { null, 0 });
        }

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
