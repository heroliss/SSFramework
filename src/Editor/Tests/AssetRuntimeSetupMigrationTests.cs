#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using Game.Framework.Context;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Framework.Editor.Tests
{
#pragma warning disable CS0618 // 测试对象就是旧版场景接线。
    public sealed class AssetRuntimeSetupMigrationTests
    {
        [Test]
        public void Migration_CopiesSettingsAndRemovesLegacyComponents()
        {
            var host = new GameObject("LegacyAssetRuntime");
            try
            {
                AssetUtility utility = host.AddComponent<AssetUtility>();
                AssetSystemConfigModel legacy = host.AddComponent<AssetSystemConfigModel>();
                host.AddComponent<AssetInitSystem>();
                var sourcePackages = new List<AssetPackageConfig>
                {
                    new("Base", autoInitialize: true, enableOnDemandDownload: true),
                    new("Dlc", autoInitialize: false, enableOnDemandDownload: false),
                };
                var sourceCdnUrls = new List<string> { " https://cdn-a/ ", "https://cdn-a" };
                SetField(legacy, "_packages", sourcePackages);
                SetField(legacy, "_defaultPackageName", "Base");
                SetField(legacy, "_playMode", AssetPlayMode.Host);
                SetField(legacy, "_playerPlayMode", AssetPlayMode.Web);
                SetField(legacy, "_cdnUrls", sourceCdnUrls);
                SetField(legacy, "_downloadingMaxNumber", 7);
                SetField(legacy, "_failedTryAgain", 2);
                SetField(legacy, "_fileOffset", 16UL);

                bool migrated = AssetRuntimeSetupMigration.TryMigrate(
                    legacy,
                    recordUndo: false,
                    out string error,
                    markSceneDirty: false);

                Assert.That(migrated, Is.True, error);
                Assert.That(host.GetComponent<AssetSystemConfigModel>(), Is.Null);
                Assert.That(host.GetComponent<AssetInitSystem>(), Is.Null);
                Assert.That(host.GetComponent<AssetUtility>(), Is.SameAs(utility));
                Assert.That(utility.Settings.DefaultPackageName, Is.EqualTo("Base"));
                Assert.That(utility.Settings.Packages.Count, Is.EqualTo(2));
                Assert.That(utility.Settings.ShouldAutoInitialize("Base"), Is.True);
                Assert.That(utility.Settings.ShouldAutoInitialize("Dlc"), Is.False);
                Assert.That(utility.Settings.PlayMode, Is.EqualTo(AssetPlayMode.Host));
                Assert.That(GetField<AssetPlayMode>(utility.Settings, "_playerPlayMode"),
                    Is.EqualTo(AssetPlayMode.Web));
                Assert.That(utility.Settings.Packages, Is.Not.SameAs(sourcePackages));
                Assert.That(utility.Settings.Packages[0], Is.Not.SameAs(sourcePackages[0]));
                Assert.That(utility.Settings.CdnUrls, Is.Not.SameAs(sourceCdnUrls));

                sourcePackages.Clear();
                sourceCdnUrls.Clear();
                Assert.That(utility.Settings.Packages.Count, Is.EqualTo(2),
                    "迁移后的包配置必须深拷贝，不能继续引用即将删除的旧组件集合。");
                Assert.That(utility.Settings.CdnUrls.Count, Is.EqualTo(2));

                AssetProviderConfig provider = utility.Settings.ToProviderConfig();
                CollectionAssert.AreEqual(new[] { "https://cdn-a/" }, provider.CdnUrls);
                Assert.That(provider.DownloadingMaxNumber, Is.EqualTo(7));
                Assert.That(provider.FailedTryAgain, Is.EqualTo(2));
                Assert.That(provider.FileOffset, Is.EqualTo(16UL));
                Assert.That(provider.EnableOnDemandDownloadByPackage["Dlc"], Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Migration_RemovesInitializerOnSiblingNodeInSameContext()
        {
            var root = new GameObject("RuntimeContext");
            var assetHost = new GameObject("AssetHost");
            var initHost = new GameObject("InitHost");
            assetHost.transform.SetParent(root.transform, false);
            initHost.transform.SetParent(root.transform, false);
            root.AddComponent<MonoGameContextBase>();
            try
            {
                AssetUtility utility = assetHost.AddComponent<AssetUtility>();
                AssetSystemConfigModel legacy = assetHost.AddComponent<AssetSystemConfigModel>();
                AssetInitSystem initializer = initHost.AddComponent<AssetInitSystem>();
                SetField(legacy, "_defaultPackageName", "SiblingPackage");

                bool migrated = AssetRuntimeSetupMigration.TryMigrate(
                    legacy,
                    recordUndo: false,
                    out string error,
                    markSceneDirty: false);

                Assert.That(migrated, Is.True, error);
                Assert.That(assetHost.GetComponent<AssetSystemConfigModel>(), Is.Null);
                Assert.That(initHost.GetComponent<AssetInitSystem>(), Is.Null);
                Assert.That(initializer == null, Is.True);
                Assert.That(utility.Settings.DefaultPackageName, Is.EqualTo("SiblingPackage"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Migration_WithUnscopedSiblingInitializer_HasNoSideEffects()
        {
            var assetHost = new GameObject("UnscopedAssetHost");
            var initHost = new GameObject("UnscopedInitHost");
            try
            {
                AssetUtility utility = assetHost.AddComponent<AssetUtility>();
                AssetRuntimeSettings originalSettings = utility.Settings;
                AssetSystemConfigModel legacy = assetHost.AddComponent<AssetSystemConfigModel>();
                AssetInitSystem initializer = initHost.AddComponent<AssetInitSystem>();

                bool migrated = AssetRuntimeSetupMigration.TryMigrate(
                    legacy,
                    recordUndo: false,
                    out string error,
                    markSceneDirty: false);

                Assert.That(migrated, Is.False);
                StringAssert.Contains("没有明确的 Context 归属", error);
                Assert.That(assetHost.GetComponent<AssetSystemConfigModel>(), Is.SameAs(legacy));
                Assert.That(initHost.GetComponent<AssetInitSystem>(), Is.SameAs(initializer));
                Assert.That(utility.Settings, Is.SameAs(originalSettings));
            }
            finally
            {
                Object.DestroyImmediate(assetHost);
                Object.DestroyImmediate(initHost);
            }
        }

        [Test]
        public void Migration_WhenConfigAndUtilityResolveDifferentContexts_HasNoSideEffects()
        {
            var firstRoot = new GameObject("FirstContext");
            var secondRoot = new GameObject("SecondContext");
            var assetHost = new GameObject("AssetHost");
            assetHost.transform.SetParent(firstRoot.transform, false);
            firstRoot.AddComponent<MonoGameContextBase>();
            MonoGameContextBase secondContext = secondRoot.AddComponent<MonoGameContextBase>();
            try
            {
                AssetUtility utility = assetHost.AddComponent<AssetUtility>();
                AssetRuntimeSettings originalSettings = utility.Settings;
                AssetSystemConfigModel legacy = assetHost.AddComponent<AssetSystemConfigModel>();
                SetTargetContext(utility, secondContext);

                bool migrated = AssetRuntimeSetupMigration.TryMigrate(
                    legacy,
                    recordUndo: false,
                    out string error,
                    markSceneDirty: false);

                Assert.That(migrated, Is.False);
                StringAssert.Contains("指向不同 Context", error);
                Assert.That(assetHost.GetComponent<AssetSystemConfigModel>(), Is.SameAs(legacy));
                Assert.That(utility.Settings, Is.SameAs(originalSettings));
            }
            finally
            {
                Object.DestroyImmediate(firstRoot);
                Object.DestroyImmediate(secondRoot);
            }
        }

        [Test]
        public void Migration_WithMultipleLegacyConfigsInSameContext_HasNoSideEffects()
        {
            var root = new GameObject("AmbiguousRuntimeContext");
            var firstHost = new GameObject("FirstAssetHost");
            var secondHost = new GameObject("SecondAssetHost");
            var initHost = new GameObject("InitHost");
            firstHost.transform.SetParent(root.transform, false);
            secondHost.transform.SetParent(root.transform, false);
            initHost.transform.SetParent(root.transform, false);
            root.AddComponent<MonoGameContextBase>();
            try
            {
                AssetUtility utility = firstHost.AddComponent<AssetUtility>();
                AssetRuntimeSettings originalSettings = utility.Settings;
                AssetSystemConfigModel selected = firstHost.AddComponent<AssetSystemConfigModel>();
                AssetSystemConfigModel other = secondHost.AddComponent<AssetSystemConfigModel>();
                AssetInitSystem initializer = initHost.AddComponent<AssetInitSystem>();

                bool migrated = AssetRuntimeSetupMigration.TryMigrate(
                    selected,
                    recordUndo: false,
                    out string error,
                    markSceneDirty: false);

                Assert.That(migrated, Is.False);
                StringAssert.Contains("2 个 AssetSystemConfigModel", error);
                Assert.That(firstHost.GetComponent<AssetSystemConfigModel>(), Is.SameAs(selected));
                Assert.That(secondHost.GetComponent<AssetSystemConfigModel>(), Is.SameAs(other));
                Assert.That(initHost.GetComponent<AssetInitSystem>(), Is.SameAs(initializer));
                Assert.That(utility.Settings, Is.SameAs(originalSettings),
                    "歧义预检必须发生在写入 Utility 或删除任一旧组件之前。");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Migration_WithMultipleUtilitiesInSameContext_HasNoSideEffects()
        {
            var root = new GameObject("AmbiguousUtilityContext");
            var firstHost = new GameObject("FirstAssetHost");
            var secondHost = new GameObject("SecondAssetHost");
            var initHost = new GameObject("InitHost");
            firstHost.transform.SetParent(root.transform, false);
            secondHost.transform.SetParent(root.transform, false);
            initHost.transform.SetParent(root.transform, false);
            root.AddComponent<MonoGameContextBase>();
            try
            {
                AssetUtility selectedUtility = firstHost.AddComponent<AssetUtility>();
                AssetRuntimeSettings originalSettings = selectedUtility.Settings;
                AssetUtility otherUtility = secondHost.AddComponent<AssetUtility>();
                AssetSystemConfigModel legacy = firstHost.AddComponent<AssetSystemConfigModel>();
                AssetInitSystem initializer = initHost.AddComponent<AssetInitSystem>();

                bool migrated = AssetRuntimeSetupMigration.TryMigrate(
                    legacy,
                    recordUndo: false,
                    out string error,
                    markSceneDirty: false);

                Assert.That(migrated, Is.False);
                StringAssert.Contains("2 个 AssetUtility", error);
                Assert.That(firstHost.GetComponent<AssetSystemConfigModel>(), Is.SameAs(legacy));
                Assert.That(firstHost.GetComponent<AssetUtility>(), Is.SameAs(selectedUtility));
                Assert.That(secondHost.GetComponent<AssetUtility>(), Is.SameAs(otherUtility));
                Assert.That(initHost.GetComponent<AssetInitSystem>(), Is.SameAs(initializer));
                Assert.That(selectedUtility.Settings, Is.SameAs(originalSettings),
                    "多入口歧义必须在写入 Utility 或删除任一旧组件之前失败。");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Migration_IgnoresUnscopedComponentsInOtherPreviewScene()
        {
            var host = new GameObject("MainStageAssetHost");
            Scene previewScene = EditorSceneManager.NewPreviewScene();
            var previewPeer = new GameObject("PreviewOnlyInitializer");
            SceneManager.MoveGameObjectToScene(previewPeer, previewScene);
            previewPeer.AddComponent<AssetInitSystem>();
            try
            {
                AssetUtility utility = host.AddComponent<AssetUtility>();
                AssetSystemConfigModel legacy = host.AddComponent<AssetSystemConfigModel>();
                host.AddComponent<AssetInitSystem>();
                SetField(legacy, "_defaultPackageName", "MainStagePackage");

                bool migrated = AssetRuntimeSetupMigration.TryMigrate(
                    legacy,
                    recordUndo: false,
                    out string error,
                    markSceneDirty: false);

                Assert.That(migrated, Is.True, error);
                Assert.That(host.GetComponent<AssetSystemConfigModel>(), Is.Null);
                Assert.That(host.GetComponent<AssetInitSystem>(), Is.Null);
                Assert.That(utility.Settings.DefaultPackageName, Is.EqualTo("MainStagePackage"));
                Assert.That(previewPeer.GetComponent<AssetInitSystem>(), Is.Not.Null,
                    "其它预览 Scene 的组件既不应阻断迁移，也不属于当前迁移的删除范围。");
            }
            finally
            {
                Object.DestroyImmediate(host);
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        [Test]
        public void Migration_OnPersistentPrefabAsset_RequiresPrefabModeAndLeavesAssetUntouched()
        {
            string prefabPath = $"Assets/__SSFramework_AssetMigration_{System.Guid.NewGuid():N}.prefab";
            var source = new GameObject("PersistentLegacyAssetRuntime");
            try
            {
                source.AddComponent<AssetUtility>();
                source.AddComponent<AssetSystemConfigModel>();
                source.AddComponent<AssetInitSystem>();
                PrefabUtility.SaveAsPrefabAsset(source, prefabPath);

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                AssetUtility utility = prefab.GetComponent<AssetUtility>();
                AssetRuntimeSettings originalSettings = utility.Settings;
                AssetSystemConfigModel legacy = prefab.GetComponent<AssetSystemConfigModel>();

                bool migrated = AssetRuntimeSetupMigration.TryMigrate(
                    legacy,
                    recordUndo: false,
                    out string error,
                    markSceneDirty: false);

                Assert.That(migrated, Is.False);
                StringAssert.Contains("Prefab Mode", error);
                Assert.That(prefab.GetComponent<AssetSystemConfigModel>(), Is.SameAs(legacy));
                Assert.That(prefab.GetComponent<AssetInitSystem>(), Is.Not.Null);
                Assert.That(utility.Settings, Is.SameAs(originalSettings));
            }
            finally
            {
                Object.DestroyImmediate(source);
                AssetDatabase.DeleteAsset(prefabPath);
            }
        }

        [Test]
        public void Migration_WithoutCoLocatedUtility_LeavesLegacyDataUntouched()
        {
            var host = new GameObject("IncompleteLegacyAssetRuntime");
            try
            {
                AssetSystemConfigModel legacy = host.AddComponent<AssetSystemConfigModel>();

                bool migrated = AssetRuntimeSetupMigration.TryMigrate(
                    legacy,
                    recordUndo: false,
                    out string error,
                    markSceneDirty: false);

                Assert.That(migrated, Is.False);
                StringAssert.Contains("缺少 AssetUtility", error);
                Assert.That(host.GetComponent<AssetSystemConfigModel>(), Is.SameAs(legacy));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            return (T)field.GetValue(target);
        }

        private static void SetTargetContext(Object component, MonoGameContextBase context)
        {
            var serializedObject = new SerializedObject(component);
            SerializedProperty targetContext = serializedObject.FindProperty("_targetContext");
            Assert.That(targetContext, Is.Not.Null, "MonoLayerBase._targetContext");
            targetContext.objectReferenceValue = context;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }
    }
#pragma warning restore CS0618
}
#endif
