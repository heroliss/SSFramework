namespace Game.Framework.Network
{
    /// <summary>
    /// WebSocket envelope 编码接缝（可选能力）：<see cref="INetworkSerializer"/> 的实现同时实现本接口时，
    /// <see cref="WebSocketUtility"/> 把 envelope（type + payload 字节）的编解码与帧类型交给序列化器接管——
    /// payload 全程保持 <c>byte[]</c>，不经字符串转换。二进制格式（Protobuf / MemoryPack）必须实现本接口：
    /// 默认的「payload 二次编码为 JSON 文本」兼容路径对二进制字节是破坏性的（UTF-8 round-trip 不保真）。
    /// </summary>
    /// <remarks>
    /// 实现契约：
    /// <list type="bullet">
    ///   <item><see cref="EncodeEnvelope"/> 的 <c>payload</c> 非 null（无载荷 = 空数组）；失败直接抛（代码写错了）。</item>
    ///   <item><see cref="DecodeEnvelope"/> 对损坏 / 契约不符的帧抛——utility 捕获后 warning + 丢弃当条，不毒化接收循环；
    ///         无载荷时 <c>payload</c> 输出空数组或 null 均可（utility 都按「无载荷」处理）。</item>
    ///   <item><see cref="UseBinaryFrames"/> 决定发送帧类型（RFC6455 文本帧要求合法 UTF-8，二进制格式必须 true）；
    ///         接收侧不区分帧类型（传输层按完整消息聚合上交）。</item>
    ///   <item>envelope 的具体线上形态由实现自定（如 Protobuf 的 <c>Envelope{string type=1; bytes payload=2;}</c>），
    ///         只须与对端约定一致；框架不再规定嵌套编码方式。</item>
    /// </list>
    /// 不实现本接口的序列化器走 utility 内置兼容路径：JSON envelope <c>{"type":..,"payload":"载荷文本"}</c> + 文本帧
    /// （与默认 <see cref="JsonUtilityNetworkSerializer"/> 的既有线上格式逐字节一致）。ADR-0028 §4。
    /// </remarks>
    public interface IWebSocketEnvelopeSerializer : INetworkSerializer
    {
        /// <summary>发送帧类型：true = 二进制帧（0x2），false = 文本帧（0x1，内容必须是合法 UTF-8）。</summary>
        bool UseBinaryFrames { get; }

        /// <summary>把一条消息（type + 已序列化的 payload 字节）编码为一帧的完整字节。</summary>
        byte[] EncodeEnvelope(string type, byte[] payload);

        /// <summary>从一帧完整字节解出 type 与 payload 字节。解析失败抛（utility 兜住丢弃当条）。</summary>
        void DecodeEnvelope(byte[] frame, out string type, out byte[] payload);
    }
}
