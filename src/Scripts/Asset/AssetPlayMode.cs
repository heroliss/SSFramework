namespace Game.Framework
{
    /// <summary>
    /// 资源系统运行模式。
    ///
    /// 由具体的 <see cref="IAssetProvider"/> 实现按自身能力映射；并不假设某个 provider 必须支持所有模式。
    /// 框架自有枚举，业务和 Settings 都只依赖本枚举，从而隔离底层资源库。
    /// </summary>
    public enum AssetPlayMode
    {
        /// <summary>编辑器内模拟模式：不打包，直接走 AssetDatabase 或等价机制。</summary>
        EditorSimulate,

        /// <summary>离线模式：只用内置（buildin）文件系统，不访问远端 CDN。</summary>
        Offline,

        /// <summary>主机模式：先查内置，缺失/过期时从 CDN 候选列表拉取并缓存。</summary>
        Host,

        /// <summary>WebGL/Web 模式：通过 HTTP 远端文件系统访问资源，不写入本地缓存。</summary>
        Web,
    }
}
