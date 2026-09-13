using System;
using System.IO;
using System.Reflection;
using Game.Framework.Editor;
using Game.Framework.Test;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using YooAsset.Editor;
using Object = UnityEngine.Object;

namespace Game.Framework.Asset.Yoo.Tests
{
    /// <summary>为单个 EditorSimulate 用例持有临时资产、独占包名和内存收集器配置。</summary>
    public sealed class YooAssetPlayModeFixture : IYooAssetPlayModeFixture
    {
        // YooAsset 3.0.5 的模拟构建器固定读取此静态实例，没有注入入口。
        // 测试临时替换并原样归还指针，不加载/保存或更改使用者的 Collector 资产。
        private static readonly FieldInfo SettingField = typeof(BundleCollectorSettingData)
            .GetField("s_setting", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(BundleCollectorSettingData).FullName, "s_setting");
        private readonly BundleCollectorSetting _previousSetting;
        private BundleCollectorSetting _setting;
        private bool _installed;
        private bool _disposed;
        internal string AssetRoot { get; private set; }
        public string PackageName { get; }
        public YooAssetTestConfig Config { get; private set; }

        public YooAssetPlayModeFixture()
        {
            _previousSetting = (BundleCollectorSetting)SettingField.GetValue(null);
            string identity = Guid.NewGuid().ToString("N");
            PackageName = "SSFrameworkTests_" + identity;
            string folderName = "__SSFrameworkYooTests_" + identity;
            try
            {
                string guid = AssetDatabase.CreateFolder("Assets", folderName);
                AssetRoot = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetRoot != "Assets/" + folderName)
                    throw new InvalidOperationException("无法创建独占的 YooAsset 测试资源目录。");

                string prefabPath = AssetRoot + "/TestPrefab.prefab";
                var prefab = new GameObject("TestPrefab");
                try { PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath); }
                finally { Object.DestroyImmediate(prefab); }

                string spritePath = CreateSprite();
                string asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(
                    "Game.Framework.Asset.Yoo.PlayMode.Tests");
                string testsRoot = Path.GetDirectoryName(Path.GetDirectoryName(asmdef)).Replace('\\', '/');
                var sceneSource = FrameworkModuleSourceCatalog.Resolve(
                    testsRoot + "/Fixtures/SuspendedSceneProbe.unity");
                if (!AssetDatabase.CopyAsset(sceneSource.AssetPath, AssetRoot + "/SuspendedSceneProbe.unity"))
                    throw new InvalidOperationException("无法复制挂起场景测试夹具：" + sceneSource.AssetPath);

                Config = ScriptableObject.CreateInstance<YooAssetTestConfig>();
                Config.AssetPaths = new[] { "TestPrefab" };
                Config.PrefabReference = new AssetReference<GameObject>();
                Config.ImageList = new AssetReferenceList<Sprite>();
                using (var serialized = new SerializedObject(Config))
                {
                    serialized.FindProperty("PrefabReference._assetGUID").stringValue =
                        AssetDatabase.AssetPathToGUID(prefabPath);
                    var images = serialized.FindProperty("ImageList._items");
                    images.arraySize = 1;
                    images.GetArrayElementAtIndex(0).FindPropertyRelative("_assetGUID").stringValue =
                        AssetDatabase.AssetPathToGUID(spritePath);
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }

                _setting = ScriptableObject.CreateInstance<BundleCollectorSetting>();
                _setting.hideFlags = HideFlags.HideAndDontSave;
                var package = new BundleCollectorPackage
                {
                    PackageName = PackageName,
                    EnableAddressable = true,
                    IncludeAssetGUID = true
                };
                var group = new BundleCollectorGroup { GroupName = "Fixture" };
                group.Collectors.Add(new BundleCollector
                {
                    CollectPath = AssetRoot,
                    CollectorGUID = AssetDatabase.AssetPathToGUID(AssetRoot),
                    AddressRuleName = nameof(AddressByFileName),
                    PackRuleName = nameof(PackSeparately),
                    FilterRuleName = nameof(CollectAll)
                });
                package.Groups.Add(group);
                _setting.Packages.Add(package);
                SettingField.SetValue(null, _setting);
                _installed = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private string CreateSprite()
        {
            string path = AssetRoot + "/TestSprite.png";
            var texture = new Texture2D(2, 2);
            try
            {
                texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
                texture.Apply();
                File.WriteAllBytes(Path.Combine(Application.dataPath, path.Substring("Assets/".Length)),
                    texture.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            if (AssetDatabase.LoadAssetAtPath<Sprite>(path) == null)
                throw new InvalidOperationException("无法导入 Sprite 测试夹具。");
            return path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_installed) SettingField.SetValue(null, _previousSetting);
            if (Config != null) Object.DestroyImmediate(Config);
            if (_setting != null) Object.DestroyImmediate(_setting);
            if (!string.IsNullOrEmpty(AssetRoot) && AssetDatabase.IsValidFolder(AssetRoot))
            {
                if (!AssetDatabase.DeleteAsset(AssetRoot))
                    throw new IOException("无法删除本用例持有的临时资产：" + AssetRoot);
            }
        }
    }
}
