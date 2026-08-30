using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Framework.Editor.Tests
{
    /// <summary>验证 AI PlayMode 预检的保存契约，防止自动化再次被原生模态弹窗锁死。</summary>
    public sealed class FrameworkAutomationPreflightTests
    {
        private string _tempFolder;
        private string _tempScenePath;
        private bool _ownsTempFolder;
        private Scene _assetScene;
        private Scene _previewScene;

        [SetUp]
        public void SetUp()
        {
            _tempFolder = $"Assets/__SSFrameworkAutomationTests_{Guid.NewGuid():N}";
            _tempScenePath = _tempFolder + "/DirtyScene.unity";
            _ownsTempFolder = false;
            _assetScene = default;
            _previewScene = default;
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (_previewScene.IsValid() && _previewScene.isLoaded)
                    EditorSceneManager.ClosePreviewScene(_previewScene);
                if (_assetScene.IsValid() && _assetScene.isLoaded)
                    EditorSceneManager.CloseScene(_assetScene, removeScene: true);
            }
            finally
            {
                // 只清理由本用例成功创建并持有的唯一目录，绝不碰同名的既有用户资产。
                if (_ownsTempFolder && AssetDatabase.IsValidFolder(_tempFolder))
                    AssetDatabase.DeleteAsset(_tempFolder);

                AssetDatabase.Refresh();
                _assetScene = default;
                _previewScene = default;
                _ownsTempFolder = false;
            }
        }

        [Test]
        public void DirtySceneCollectionAndSave_SaveOwnedAssetBackedScene()
        {
            EnsureTempFolder();
            string sourceScenePath = FindAnyProjectScene();
            Assert.That(sourceScenePath, Is.Not.Null, "项目至少需要一个 Scene 资产作为无关内容的保存模板");
            Assert.That(AssetDatabase.CopyAsset(sourceScenePath, _tempScenePath), Is.True);
            _assetScene = EditorSceneManager.OpenScene(_tempScenePath, OpenSceneMode.Additive);

            var previousActiveScene = SceneManager.GetActiveScene();
            Assert.That(SceneManager.SetActiveScene(_assetScene), Is.True);
            _ = new GameObject("UnsavedMarker");
            Assert.That(SceneManager.SetActiveScene(previousActiveScene), Is.True);
            EditorSceneManager.MarkSceneDirty(_assetScene);
            Assert.That(_assetScene.isDirty, Is.True);

            Assert.That(
                FrameworkAutomationPreflight.CollectDirtyScenes().Select(scene => scene.path),
                Does.Contain(_tempScenePath),
                "预检必须从已加载场景中发现本用例拥有的脏场景。 ");
            var savedPaths = FrameworkAutomationPreflight.SaveDirtyScenesAfterValidation(
                new[] { _assetScene });

            Assert.That(savedPaths, Does.Contain(_tempScenePath));
            Assert.That(_assetScene.isDirty, Is.False);
            Assert.That(File.Exists(_tempScenePath), Is.True);
        }

        [Test]
        public void PreparePlayModeTests_RejectsUntitledSceneBeforeSavingAnything()
        {
            EnsureTempFolder();
            string sourceScenePath = FindAnyProjectScene();
            Assert.That(sourceScenePath, Is.Not.Null, "项目至少需要一个 Scene 资产作为无关内容的保存模板");
            Assert.That(AssetDatabase.CopyAsset(sourceScenePath, _tempScenePath), Is.True);
            _assetScene = EditorSceneManager.OpenScene(_tempScenePath, OpenSceneMode.Additive);

            var previousActiveScene = SceneManager.GetActiveScene();
            Assert.That(SceneManager.SetActiveScene(_assetScene), Is.True);
            _ = new GameObject("MustRemainUnsaved");
            Assert.That(SceneManager.SetActiveScene(previousActiveScene), Is.True);
            EditorSceneManager.MarkSceneDirty(_assetScene);

            _previewScene = EditorSceneManager.NewPreviewScene();
            Assert.That(_previewScene.path, Is.Empty);
            Assert.That(_assetScene.isDirty, Is.True);

            var exception = Assert.Throws<InvalidOperationException>(
                () => FrameworkAutomationPreflight.SaveDirtyScenesAfterValidation(
                    new[] { _assetScene, _previewScene }));

            Assert.That(exception.Message, Does.Contain("未命名"));
            Assert.That(_assetScene.isDirty, Is.True, "整批验证失败时，有路径的场景也不得被提前保存");
            Assert.That(_previewScene.path, Is.Empty);
        }

        [Test]
        public void ReadyMarker_RemainsVisibleWhenFrameworkSinksAreEmpty()
        {
            var previousSinks = Log.Sinks.ToArray();
            try
            {
                Log.ClearSinks();
                LogAssert.Expect(LogType.Log,
                    new Regex(@"\[SSFramework\.Automation\] READY — 无 sink 契约测试"));

                FrameworkAutomationPreflight.ReportReady("无 sink 契约测试");
            }
            finally
            {
                Log.ClearSinks();
                foreach (var sink in previousSinks) Log.AddSink(sink);
            }
        }

        private void EnsureTempFolder()
        {
            string guid = AssetDatabase.CreateFolder("Assets", Path.GetFileName(_tempFolder));
            if (string.IsNullOrEmpty(guid))
                throw new InvalidOperationException($"无法创建测试隔离目录：{_tempFolder}");
            _ownsTempFolder = true;
        }

        private string FindAnyProjectScene()
            => AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(path => !path.StartsWith(_tempFolder, StringComparison.Ordinal));
    }
}
