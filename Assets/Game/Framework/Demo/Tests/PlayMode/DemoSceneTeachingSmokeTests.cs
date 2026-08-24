#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Demo.Modules;
using Game.Framework.Internal;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
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
