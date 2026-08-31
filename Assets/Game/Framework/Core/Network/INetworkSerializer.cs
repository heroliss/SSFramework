namespace Game.Framework.Network
{
    /// <summary>
    /// 网络序列化接缝：对象 ↔ 字节，HTTP 请求/响应体与 WebSocket 载荷共用。
    /// 默认 <see cref="JsonUtilityNetworkSerializer"/>（UTF-8 JSON，零第三方依赖）；
    /// 后端技术栈确定后换 MemoryPack / Protobuf 实现，经 utility 构造注入，业务代码零改动。ADR-0028 §5。
    /// </summary>
    /// <remarks>
    /// 实现契约：
    /// <list type="bullet">
    ///   <item><see cref="Serialize{T}"/> 失败直接抛（参数/类型问题，代码写错了），成功不得返回 null。</item>
    ///   <item><see cref="Deserialize{T}"/> 对损坏/契约不符的字节抛，非空输入成功时不得返回 null——HTTP utility
    ///         折叠为 <see cref="NetworkException"/>（DeserializeError）；WebSocket 推送记录 warning 并丢弃当条。</item>
    ///   <item>有 HTTP 请求体时 <see cref="ContentType"/> 必须是非空、无 CR/LF 的 header value。</item>
    ///   <item>两方法均在主线程被调（框架统一契约，对齐 ADR-0021 §6 先例）。</item>
    /// </list>
    /// 与 <c>IStorageSerializer</c> 的两点差异是刻意的：<b>无 class 约束</b>（WebSocket 推送事件是
    /// <c>[Serializable]</c> struct）；多出 <see cref="ContentType"/>（HTTP 请求头随格式走，格式与声明不该分两处配）。
    /// </remarks>
    public interface INetworkSerializer
    {
        /// <summary>本格式对应的 HTTP Content-Type（默认 JSON 实现为 <c>"application/json"</c>；
        /// Protobuf 实现给 <c>"application/x-protobuf"</c>）。有请求体时自动作为请求头发送。</summary>
        string ContentType { get; }

        /// <summary>
        /// 把一份非 null 载荷编码为字节。成功必须返回非 null 数组（无内容用空数组），失败直接抛异常；
        /// 返回值由调用方接管，序列化器不得在返回后继续修改。
        /// </summary>
        byte[] Serialize<T>(T data);

        /// <summary>
        /// 把非 null 字节快照还原为目标值。非空输入成功时不得返回 null；损坏数据、契约不匹配或无法构造根对象时直接抛异常。
        /// 输入只在本次调用期间借用，序列化器不得缓存或修改。
        /// </summary>
        T Deserialize<T>(byte[] bytes);
    }
}
