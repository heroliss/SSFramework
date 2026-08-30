using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YooAsset;

namespace Game.Framework.Asset.Yoo.Tests
{
    /// <summary>锁定 Web 文件系统与其它运行模式共享同一内置偏移格式，但不注入 Sandbox 专属 fallback 参数。</summary>
    public sealed class YooAssetWebDecryptionTests
    {
        [SetUp]
        public void SetUp() => GameAssetDecryption.BundleDecryptorFactory = null;

        [TearDown]
        public void TearDown() => GameAssetDecryption.BundleDecryptorFactory = null;

        [Test]
        public void WebOptions_InjectMemoryDecryptorIntoServerAndNetworkFileSystems()
        {
            using var provider = new YooAssetProvider();
            var options = (WebPlayModeOptions)provider.CreateInitOptions(
                "WebPackage",
                AssetPlayMode.Web,
                new AssetProviderConfig
                {
                    CdnUrls = new[] { "https://cdn.example.invalid" },
                    FileOffset = 64,
                },
                copyBuiltinManifest: false);

            AssertWebParameters(options.WebServerFileSystemParameters);
            AssertWebParameters(options.WebNetworkFileSystemParameters);
        }

        [Test]
        public void RuntimeOffsetDecryptor_RejectsValueBeyondSharedLimit()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new GameBundleOffsetDecryptor(AssetProviderConfig.MaxBuiltInFileOffset + 1UL));
        }

        [Test]
        public void WebOptions_RejectCustomDecryptorWithoutMemoryContract()
        {
            GameAssetDecryption.BundleDecryptorFactory = () => new OffsetOnlyDecryptor();
            using var provider = new YooAssetProvider();

            var failure = Assert.Throws<InvalidOperationException>(() => provider.CreateInitOptions(
                "WebPackage",
                AssetPlayMode.Web,
                new AssetProviderConfig { CdnUrls = new[] { "https://cdn.example.invalid" } },
                copyBuiltinManifest: false));

            Assert.That(failure?.Message, Does.Contain(nameof(IBundleMemoryDecryptor)));
            Assert.That(failure?.Message, Does.Contain(nameof(OffsetOnlyDecryptor)));
        }

        private static void AssertWebParameters(FileSystemParameters parameters)
        {
            var field = typeof(FileSystemParameters).GetField(
                "_createParameters", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "YooAsset 参数快照字段发生变化时，应重新核验 Web 解密注入 API。");
            var values = (Dictionary<string, object>)field!.GetValue(parameters);

            Assert.That(values[nameof(EFileSystemParameter.AssetBundleDecryptor)],
                Is.InstanceOf<IBundleMemoryDecryptor>());
            Assert.That(values[nameof(EFileSystemParameter.RawBundleDecryptor)],
                Is.InstanceOf<IBundleMemoryDecryptor>());
            Assert.That(values.ContainsKey(nameof(EFileSystemParameter.AssetBundleFallbackDecryptor)), Is.False,
                "Web 文件系统不接受 Sandbox/Builtin 专属的 fallback 参数。");
        }

        private sealed class OffsetOnlyDecryptor : IBundleOffsetDecryptor
        {
            public long GetFileOffset(BundleDecryptArgs args) => 0;
        }
    }
}
