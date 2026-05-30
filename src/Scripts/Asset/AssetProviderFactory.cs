namespace Game.Framework
{
    /// <summary>
    /// 默认资源 provider 创建点。
    ///
    /// AssetUtility 只依赖 <see cref="IAssetProvider"/>；未来切换实现时，把这里替换为新的 provider 即可，
    /// Settings / InitSystem / 业务加载 API 都不需要跟随改动。
    /// </summary>
    internal static class AssetProviderFactory
    {
        public static IAssetProvider CreateDefault() => new YooAssetProvider();
    }
}
