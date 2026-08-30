using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Game.Framework.Test
{
    public class AssetRuntimeSettingsTests
    {
        [Test]
        public void CollectionViews_AreStructurallyReadOnlyAndStable()
        {
            var settings = CreateSettings();

            IReadOnlyList<AssetPackageConfig> packages = settings.Packages;
            IReadOnlyList<string> cdnUrls = settings.CdnUrls;

            AssertReadOnly(packages);
            AssertReadOnly(cdnUrls);
            Assert.That(settings.Packages, Is.SameAs(packages),
                "重复读取配置不应反复分配只读包装器。");
            Assert.That(settings.CdnUrls, Is.SameAs(cdnUrls),
                "重复读取配置不应反复分配只读包装器。");
        }

        [Test]
        public void CollectionViews_RefreshWhenSerializedListInstancesAreReplaced()
        {
            var settings = CreateSettings();
            IReadOnlyList<AssetPackageConfig> oldPackages = settings.Packages;
            IReadOnlyList<string> oldCdnUrls = settings.CdnUrls;

            SetField(settings, "_packages", new List<AssetPackageConfig>
            {
                new("Replacement", autoInitialize: false, enableOnDemandDownload: false),
            });
            SetField(settings, "_cdnUrls", new List<string> { "https://replacement.example/" });

            Assert.That(settings.Packages, Is.Not.SameAs(oldPackages));
            Assert.That(settings.Packages[0].Name, Is.EqualTo("Replacement"));
            Assert.That(settings.CdnUrls, Is.Not.SameAs(oldCdnUrls));
            Assert.That(settings.CdnUrls[0], Is.EqualTo("https://replacement.example/"));
            AssertReadOnly(settings.Packages);
            AssertReadOnly(settings.CdnUrls);
        }

        private static AssetRuntimeSettings CreateSettings() => new(
            new[] { new AssetPackageConfig("Base") },
            "Base",
            AssetPlayMode.EditorSimulate,
            AssetPlayMode.Offline,
            new[] { "https://cdn.example/" },
            downloadingMaxNumber: 4,
            failedTryAgain: 1,
            fileOffset: 0);

        private static void AssertReadOnly<T>(IReadOnlyList<T> view)
        {
            Assert.That(view, Is.Not.InstanceOf<List<T>>(),
                "只读 Interface 不能直接泄漏内部 List 实例。");
            if (view is not ICollection<T> collection) return;

            Assert.That(collection.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => collection.Clear());
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }
    }
}
