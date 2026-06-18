using YooAsset; // IBundleEncryptor / IManifestEncryptor / IManifestDecryptor

namespace Game.Framework.Build
{
    /// <summary>
    /// 构建期自定义加密接入点（YooAsset 后端）——框架内置「偏移加密」之外的扩展位。
    ///
    /// <para>项目要用偏移以外的加密（XOR / AES 等）时，在 Editor 程序集的 <c>[InitializeOnLoadMethod]</c> 里设置这些实例
    /// （批处理 / CI 构建也会跑 InitializeOnLoad，故 CI 同样生效）；<see cref="FrameworkAssetBuilder"/> 构建时<b>优先</b>用它们，
    /// 否则回退到 profile 的偏移加密（<see cref="FrameworkAssetBuildProfile.FileOffset"/>）。</para>
    ///
    /// <para>必须与运行时 <c>Game.Framework.GameAssetDecryption</c> 的对应解密器<b>成对、算法一致</b>，否则整批加载失败。
    /// 完整用法 / 选型见 <c>docs/asset-encryption.md</c>。</para>
    /// </summary>
    public static class GameAssetEncryption
    {
        /// <summary>自定义资源包加密器。非 null 时优先于 profile 偏移加密（二者都配会警告并以此为准）。</summary>
        public static IBundleEncryptor CustomBundleEncryptor { get; set; }

        /// <summary>自定义清单加密器（要加密清单时设；不设＝清单明文，与偏移加密一致）。</summary>
        public static IManifestEncryptor CustomManifestEncryptor { get; set; }

        /// <summary>
        /// 自定义清单解密器。⚠ 加密清单时【必须】同时设——构建过程会回读上次构建的清单做增量 / 版本对比，
        /// 没有解密器读不了已加密的旧清单。运行时另需 <c>GameAssetDecryption.ManifestDecryptorFactory</c>。
        /// </summary>
        public static IManifestDecryptor CustomManifestDecryptor { get; set; }
    }
}
