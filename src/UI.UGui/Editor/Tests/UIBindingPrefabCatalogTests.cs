using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor.Tests
{
    /// <summary>锁定 UI Binding 候选 Prefab 的完整基线、增量更新与脚本域重载复用契约。</summary>
    public sealed class UIBindingPrefabCatalogTests
    {
        [Test]
        public void Catalog_IndexesOnlyBindingPrefabsAndRestoresSessionWithoutFullRescan()
        {
            string folderName = "__UIBindingCatalogTests_" + Guid.NewGuid().ToString("N");
            string folderPath = "Assets/" + folderName;
            string plainPath = folderPath + "/Plain.prefab";
            string bindingPath = folderPath + "/Binding.prefab";
            AssetDatabase.CreateFolder("Assets", folderName);

            try
            {
                SavePrefab(plainPath, withBindingData: false);
                SavePrefab(bindingPath, withBindingData: true);

                UIBindingPrefabCatalog.Refresh();
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Contain(bindingPath));
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Not.Contain(plainPath));

                int fullScanCount = UIBindingPrefabCatalog.FullScanCount;
                string[] expected = UIBindingPrefabCatalog.GetPaths().ToArray();
                UIBindingPrefabCatalog.ForgetMemoryForTests();

                Assert.That(UIBindingPrefabCatalog.GetPaths(), Is.EqualTo(expected));
                Assert.That(UIBindingPrefabCatalog.FullScanCount, Is.EqualTo(fullScanCount),
                    "脚本域重载后的首次 claim 读取应恢复 SessionState，不能再次加载全工程 Prefab。 ");

                SavePrefab(plainPath, withBindingData: true);
                UIBindingPrefabCatalog.ApplyAssetChanges(
                    new[] { plainPath }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Contain(plainPath));

                UIBindingPrefabCatalog.ApplyAssetChanges(
                    Array.Empty<string>(), new[] { plainPath }, Array.Empty<string>(), Array.Empty<string>());
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Not.Contain(plainPath));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
                UIBindingPrefabCatalog.Refresh();
            }
        }

        [Test]
        public void BasePrefabChange_RevalidatesVariantWhenOnlyBasePathIsReported()
        {
            string folderName = "__UIBindingVariantCatalogTests_" + Guid.NewGuid().ToString("N");
            string folderPath = "Assets/" + folderName;
            string basePath = folderPath + "/Base.prefab";
            string variantPath = folderPath + "/Variant.prefab";
            AssetDatabase.CreateFolder("Assets", folderName);

            try
            {
                SavePrefab(basePath, withBindingData: false);
                SaveVariant(basePath, variantPath);
                Assert.That(
                    PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(variantPath)),
                    Is.EqualTo(PrefabAssetType.Variant),
                    "测试资产必须是真实 Prefab Variant，不能用普通副本冒充依赖关系。");

                UIBindingPrefabCatalog.Refresh();
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Not.Contain(basePath));
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Not.Contain(variantPath));

                SetBindingData(basePath, enabled: true);
                UIBindingPrefabCatalog.ReplaceCandidatePathsForTests(Array.Empty<string>());
                UIBindingPrefabCatalog.ApplyAssetChanges(
                    new[] { basePath }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Contain(basePath));
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Contain(variantPath),
                    "即使导入回调只报告基 Prefab，继承 UIBindingData 的 Variant 也必须进入索引。");

                SetBindingData(basePath, enabled: false);
                UIBindingPrefabCatalog.ReplaceCandidatePathsForTests(new[] { basePath, variantPath });
                UIBindingPrefabCatalog.ApplyAssetChanges(
                    new[] { basePath }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Not.Contain(basePath));
                Assert.That(UIBindingPrefabCatalog.GetPaths(), Does.Not.Contain(variantPath),
                    "基 Prefab 移除绑定后，Variant 不能残留陈旧 claim 候选。");
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
                UIBindingPrefabCatalog.Refresh();
            }
        }

        private static void SavePrefab(string path, bool withBindingData)
        {
            var root = new GameObject(System.IO.Path.GetFileNameWithoutExtension(path));
            try
            {
                if (withBindingData) root.AddComponent<UIBindingData>();
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void SaveVariant(string basePath, string variantPath)
        {
            GameObject basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(basePath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(instance, variantPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void SetBindingData(string prefabPath, bool enabled)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                UIBindingData existing = root.GetComponent<UIBindingData>();
                if (enabled && existing == null) root.AddComponent<UIBindingData>();
                if (!enabled && existing != null) UnityEngine.Object.DestroyImmediate(existing);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
