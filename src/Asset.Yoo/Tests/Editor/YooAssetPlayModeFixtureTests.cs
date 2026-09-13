using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YooAsset.Editor;

namespace Game.Framework.Asset.Yoo.Tests
{
    public sealed class YooAssetPlayModeFixtureTests
    {
        [Test]
        public void Fixture_OwnsLoadableResourcesAndRestoresCollectorWithoutSavingIt()
        {
            var field = typeof(BundleCollectorSettingData)
                .GetField("s_setting", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = (BundleCollectorSetting)field.GetValue(null);
            string previousJson = previous != null ? EditorJsonUtility.ToJson(previous) : null;
            string root;
            var fixture = new YooAssetPlayModeFixture();
            try
            {
                root = fixture.AssetRoot;
                Assert.That(BundleCollectorSettingData.Setting.Packages, Has.Count.EqualTo(1));
                Assert.That(BundleCollectorSettingData.Setting.Packages[0].PackageName,
                    Is.EqualTo(fixture.PackageName));
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(
                    AssetDatabase.GUIDToAssetPath(fixture.Config.PrefabReference.AssetGUID)), Is.Not.Null);
                Assert.That(fixture.Config.ImageList.Count, Is.EqualTo(1));
                Assert.That(AssetDatabase.LoadAssetAtPath<Sprite>(
                    AssetDatabase.GUIDToAssetPath(fixture.Config.ImageList[0].AssetGUID)), Is.Not.Null);
                Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(root + "/SuspendedSceneProbe.unity"),
                    Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(BundleCollectorSettingData.Setting), Is.Empty);
            }
            finally { fixture.Dispose(); }
            Assert.That(AssetDatabase.IsValidFolder(root), Is.False);
            Assert.That(field.GetValue(null), Is.SameAs(previous));
            if (previous != null) Assert.That(EditorJsonUtility.ToJson(previous), Is.EqualTo(previousJson));
            Assert.DoesNotThrow(fixture.Dispose);
        }
    }
}
