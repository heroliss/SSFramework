namespace Game.Framework.Storage
{
    /// <summary>
    /// 存储序列化抽象：对象 ↔ 字节。<see cref="StorageUtility"/> 的两个正交扩展点之一
    /// （另一个是 <see cref="IStorageProvider"/>：字节 ↔ 介质），换格式只换本接口实现、不动 provider。
    ///
    /// <para>默认实现为 <see cref="JsonUtilityStorageSerializer"/>（UTF-8 JSON，零第三方依赖）；
    /// 需要更强格式（MemoryPack / Newtonsoft 等）或加密（包一层对称加解密）时自写实现，
    /// 经 <see cref="StorageUtility"/> 构造函数注入。ADR-0021。</para>
    /// </summary>
    /// <remarks>
    /// 实现契约：
    /// <list type="bullet">
    ///   <item><see cref="Serialize{T}"/> 失败（类型不支持等）直接抛异常——写路径的失败必须让调用方知道；成功不得返回 null。</item>
    ///   <item><see cref="Deserialize{T}"/> 对损坏 / 不兼容字节<b>抛异常</b>（由 <see cref="StorageUtility"/> 捕获并回退备份），
    ///         成功不得返回 null 或残缺根对象。</item>
    ///   <item>两个方法都在<b>主线程</b>被调用（见 ADR-0021 §6），实现无需考虑线程安全。</item>
    /// </list>
    /// </remarks>
    public interface IStorageSerializer
    {
        /// <summary>
        /// 把非 null 数据对象序列化为字节。成功必须返回非 null 数组（无内容用空数组），失败直接抛异常；
        /// 返回值由调用方接管，序列化器不得在返回后继续修改。
        /// </summary>
        byte[] Serialize<T>(T data) where T : class;

        /// <summary>
        /// 从非 null 字节快照还原非 null 数据对象。字节损坏、格式不兼容或无法构造根对象时直接抛异常；
        /// 输入只在本次调用期间借用，序列化器不得缓存或修改。
        /// </summary>
        T Deserialize<T>(byte[] bytes) where T : class;
    }
}
