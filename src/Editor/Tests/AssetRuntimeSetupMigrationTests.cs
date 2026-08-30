#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

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
                SetField(legacy, "_packages", new List<AssetPackageConfig>
                {
                    new("Base", autoInitialize: true, enableOnDemandDownload: true),
                    new("Dlc", autoInitialize: false, enableOnDemandDownload: false),
                });
                SetField(legacy, "_defaultPackageName", "Base");
                SetField(legacy, "_playMode", AssetPlayMode.Host);
                SetField(legacy, "_playerPlayMode", AssetPlayMode.Offline);
                SetField(legacy, "_cdnUrls", new List<string> { " https://cdn-a/ ", "https://cdn-a" });
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
    }
#pragma warning restore CS0618
}
#endif
