using System;
using YooAsset; // IBundleDecryptor / IManifestDecryptor

namespace Game.Framework
{
    /// <summary>
    /// 运行时自定义解密接入点（YooAsset 后端）——框架内置「偏移解密」之外的扩展位。
    ///
    /// <para>项目要用偏移以外的加密（XOR / AES 等）时，在资源初始化【之前】（启动引导里，先于 <c>AssetInitSystem</c> 跑）
    /// 设置这里的工厂；<c>YooAssetProvider</c> 注入文件系统时<b>优先</b>用它，否则回退到偏移解密（按 <c>FileOffset</c>）。
    /// 必须与构建侧 <c>Game.Framework.Build.GameAssetEncryption</c> 的对应加密器<b>成对、算法一致</b>，否则整批加载失败。</para>
    ///
    /// <para>静态钩子刻意置于 YooAsset 适配程序集：自定义解密器本就依赖 YooAsset 的 <see cref="IBundleDecryptor"/>，
    /// 换后端时随之替换（ADR-0013「YooAsset 收口在 provider」）。这是「扩展资源后端」的合法边界，与业务代码不碰 YooAsset 不冲突。
    /// 完整用法 / 选型 / AES-CTR 注意点见 <c>docs/asset-encryption.md</c>。</para>
    /// </summary>
    public static class GameAssetDecryption
    {
        /// <summary>
        /// 自定义资源包解密器工厂——实现 <see cref="IBundleOffsetDecryptor"/> / <see cref="IBundleMemoryDecryptor"/> /
        /// <see cref="IBundleStreamDecryptor"/> 之一。非 null 时优先于偏移解密。每个文件系统注入时各调一次（按需返回实例）。
        /// </summary>
        public static Func<IBundleDecryptor> BundleDecryptorFactory { get; set; }

        /// <summary>自定义清单解密器工厂——仅当构建侧加密了清单时设；不加密清单（含偏移加密）则留空。</summary>
        public static Func<IManifestDecryptor> ManifestDecryptorFactory { get; set; }
    }
}
