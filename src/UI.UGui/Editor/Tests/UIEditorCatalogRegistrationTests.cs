using System;
using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.UI.UGui.Editor.Tests
{
    /// <summary>锁定 UGUI Editor Module 自己登记根 Profile 与目录级覆盖的配置卡片。</summary>
    public sealed class UIEditorCatalogRegistrationTests
    {
        [Test]
        public void UGuiModule_RegistersOwnedConfiguration()
        {
            var descriptor = FrameworkConfigRegistry.Snapshot().Single(item => item.Id == "ui-binding");
            Assert.That(descriptor.ProfileType, Is.EqualTo(typeof(UICodeGenProfile)));
            Assert.That(descriptor.SecondaryProfileType, Is.EqualTo(typeof(UICodeGenDirConfig)));
        }

        [Test]
        public void UGuiModule_RegistersOwnedOutputClaims() =>
            Assert.That(
                FrameworkGeneratedOutputClaimCatalog.SnapshotSources().Select(item => item.Id),
                Does.Contain(UIBindingCodeGenerator.OutputClaimSourceId));

        [Test]
        public void OutputClaimCollector_UsesIncrementalBindingPrefabCatalog()
        {
            string generatorSource = ReadScriptSource(nameof(UIBindingCodeGenerator));
            string catalogSource = ReadScriptSource(nameof(UIBindingPrefabCatalog));

            Assert.That(generatorSource, Does.Contain("UIBindingPrefabCatalog.GetPaths()"));
            Assert.That(generatorSource, Does.Not.Contain("AssetDatabase.FindAssets(\"t:Prefab\")"),
                "任何其它生成器写盘前都会刷新 UI claim；collector 不得重新加载全工程所有 Prefab。 ");
            Assert.That(catalogSource, Does.Contain("SessionState.SetString"));
            Assert.That(catalogSource, Does.Contain("OnPostprocessAllAssets"));
        }

        [Test]
        public void CrossModulePreview_DoesNotColdStartBindingPrefabScan()
        {
            UIBindingPrefabCatalog.Invalidate();
            FrameworkGeneratedOutputClaimCatalog.Invalidate();
            int fullScanCount = UIBindingPrefabCatalog.FullScanCount;
            try
            {
                bool ok = FrameworkGeneratedOutputClaimCatalog.TryValidateForPreview(
                    "ui-binding-cold-preview-test",
                    Array.Empty<FrameworkGeneratedOutputClaim>(),
                    out string message);

                Assert.That(ok, Is.True, message);
                Assert.That(UIBindingPrefabCatalog.FullScanCount, Is.EqualTo(fullScanCount),
                    "其它工作台的冷启动预览不得沿 claim collector 扫描全工程 Prefab。");
                Assert.That(UIBindingPrefabCatalog.TryGetPaths(out _), Is.False);
                Assert.That(message, Does.Contain("尚无预览快照"));
            }
            finally
            {
                UIBindingPrefabCatalog.Refresh();
            }
        }

        [Test]
        public void UGuiProfilesAndWorkbench_ConsumeSharedExplicitSnapshots()
        {
            string profileSource = ReadScriptSource(nameof(UICodeGenProfile));
            string directorySource = ReadScriptSource(nameof(UICodeGenDirConfig));
            string windowSource = ReadScriptSource(nameof(UICodeGenConfigOverviewWindow));

            Assert.That(profileSource, Does.Contain("FrameworkEditorProfileCatalog.TryResolveFirst"));
            Assert.That(profileSource, Does.Contain("GetExistingProfileOrThrow"));
            int create = profileSource.IndexOf("AssetDatabase.CreateAsset(profile, path);", StringComparison.Ordinal);
            int firstRefresh = profileSource.IndexOf("FrameworkEditorProfileCatalog.Refresh", StringComparison.Ordinal);
            int lastRefresh = profileSource.LastIndexOf("FrameworkEditorProfileCatalog.Refresh", StringComparison.Ordinal);
            int ensureDirectory = profileSource.IndexOf("FrameworkProjectSettingsLocation.EnsureDirectory", StringComparison.Ordinal);
            int collisionCheck = profileSource.IndexOf("GetExistingProfileOrThrow", StringComparison.Ordinal);
            int effectiveCheck = profileSource.IndexOf("effective != profile", StringComparison.Ordinal);
            Assert.That(create, Is.GreaterThan(firstRefresh), "创建前必须强制刷新，修复尚未送达的 projectChanged。 ");
            Assert.That(ensureDirectory, Is.GreaterThan(firstRefresh), "确认确实缺少 Profile 前不得创建默认目录。 ");
            Assert.That(collisionCheck, Is.GreaterThan(ensureDirectory));
            Assert.That(create, Is.GreaterThan(collisionCheck), "CreateAsset 前必须拒绝固定路径碰撞。 ");
            Assert.That(lastRefresh, Is.GreaterThan(create), "创建后必须刷新并验证实际生效项。 ");
            Assert.That(effectiveCheck, Is.GreaterThan(create), "创建后必须确认新资产就是 stable-first 生效项。 ");
            Assert.That(profileSource, Does.Not.Contain("private static UICodeGenProfile _cached"));
            Assert.That(directorySource, Does.Contain("FrameworkEditorProfileCatalog.GetPaths"));
            Assert.That(windowSource, Does.Contain("FrameworkEditorProfileCatalog.TryGetPaths"));
            Assert.That(windowSource, Does.Contain("UIBindingPrefabCatalog.TryGetPaths"));
            Assert.That(windowSource, Does.Contain("重新扫描"));
            Assert.That(windowSource, Does.Contain("FrameworkEditorProfileCatalog.Invalidate();"));
            Assert.That(windowSource, Does.Contain("UIBindingPrefabCatalog.Invalidate();"),
                "刷新任一步失败都应丢弃两类快照，不能展示跨批次混合证据。");
            Assert.That(windowSource, Does.Not.Contain("AssetDatabase.FindAssets(\"t:\" + nameof(UICodeGenDirConfig))"),
                "CreateGUI 与窗口重绘不得逐次重扫目录配置。 ");
        }

        [Test]
        public void UGuiWorkbench_UsesLightweightCardsAndResponsiveHierarchy()
        {
            FrameworkEditorProfileCatalog.Invalidate();
            var window = ScriptableObject.CreateInstance<UICodeGenConfigOverviewWindow>();
            try
            {
                window.position = new Rect(0f, 0f, 360f, 620f);
                window.CreateGUI();

                VisualElement root = window.rootVisualElement;
                Assert.That(root.Q<VisualElement>("ui-binding-hero"), Is.Not.Null);
                Assert.That(root.Q<VisualElement>("ui-binding-idle"), Is.Not.Null,
                    "首次绘制必须先给轻量说明，不能在 CreateGUI 中扫描全工程。 ");
                Assert.That(root.Q<Button>("ui-binding-rescan")?.text, Is.EqualTo("重新扫描"));
                Assert.That(root.Q<ScrollView>("ui-binding-content")?.horizontalScrollerVisibility,
                    Is.EqualTo(ScrollerVisibility.Hidden));

                window.RefreshForTests();
                Assert.That(root.Q<VisualElement>("ui-binding-summary"), Is.Not.Null);
                Assert.That(
                    root.Q<VisualElement>("ui-binding-profile-default") ??
                    root.Q<VisualElement>("ui-binding-profile-missing"),
                    Is.Not.Null);

                VisualElement actions = root.Q<VisualElement>("ui-binding-actions");
                window.ApplyResponsiveLayoutForTests(360f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));
                window.ApplyResponsiveLayoutForTests(900f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Row));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                FrameworkEditorProfileCatalog.Invalidate();
            }
        }

        private static string ReadScriptSource(string typeName)
        {
            string[] paths = AssetDatabase.FindAssets(typeName + " t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith("/" + typeName + ".cs", StringComparison.Ordinal))
                .ToArray();
            Assert.That(paths, Has.Length.EqualTo(1), "应精确找到 UI.UGui Editor owner Module 内的源码。 ");
            return AssetDatabase.LoadAssetAtPath<MonoScript>(paths[0]).text;
        }
    }
}
