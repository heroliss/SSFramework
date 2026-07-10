using System;
using System.Collections.Generic;

namespace Game.Framework.Network
{
    /// <summary>
    /// Protobuf wire 格式的 <see cref="INetworkSerializer"/>：业务按消息类型注册显式编解码函数
    /// （<see cref="Register{T}"/>，用 <see cref="ProtoWriter"/> / <see cref="ProtoReader"/> 写读字段），
    /// HTTP 体走 <c>application/x-protobuf</c>；同时实现 <see cref="IWebSocketEnvelopeSerializer"/>——
    /// WS envelope 用标准 proto 消息 <c>{string type = 1; bytes payload = 2;}</c> + 二进制帧。
    /// </summary>
    /// <remarks>
    /// <b>定位</b>：轻量真 wire 格式实现——字节与标准 protobuf 互通（对端 .proto 字段号对上即可对讲），
    /// 但无 protoc 代码生成、无反射：每个消息类型手写几行编解码。适合消息数量有限的自建后端 / dev server。
    /// 消息多到手写吃力、或需要 map / oneof / 有符号 / 浮点 / .proto 契约共享时，换 Google.Protobuf 等真库
    /// 实现 <see cref="INetworkSerializer"/> 替换本类（构造注入，业务调用代码零改动）。<br/>
    /// <b>注册时机</b>：<see cref="Register{T}"/> 只在构造 / 服务注册期调用（未加锁）；之后字典只读 + 编解码纯函数，
    /// 任意线程可安全并发 Serialize / Deserialize（回环 dev server 的后台线程与主线程共用同一实例是安全的——
    /// <see cref="INetworkSerializer"/> 的"主线程被调"契约约束的是框架调用侧，本实现自身无线程亲和）。<br/>
    /// <b>失败语义</b>：未注册类型抛 <see cref="InvalidOperationException"/>（代码写错了）；
    /// 字节损坏由编解码函数抛（框架网络层折叠为 DeserializeError，实现契约：不吞）。
    /// </remarks>
    public sealed class ProtobufNetworkSerializer : IWebSocketEnvelopeSerializer
    {
        // envelope 的 proto 字段号（等价 .proto：message Envelope { string type = 1; bytes payload = 2; }）
        private const int EnvelopeTypeField = 1;
        private const int EnvelopePayloadField = 2;

        private interface ICodec { }

        private sealed class Codec<T> : ICodec
        {
            public Action<ProtoWriter, T> Write;
            public Func<ProtoReader, T> Read;
        }

        private readonly Dictionary<Type, ICodec> _codecs = new();

        public string ContentType => "application/x-protobuf";

        public bool UseBinaryFrames => true;

        /// <summary>
        /// 注册一个消息类型的编解码（写侧逐字段 <c>w.WriteXxx(字段号, 值)</c>、读侧
        /// <c>while (r.TryReadTag(...)) switch</c>，未知字段 <c>SkipField</c>）。返回自身可链式注册。
        /// 同类型重复注册抛（代码写错了）。
        /// </summary>
        public ProtobufNetworkSerializer Register<T>(Action<ProtoWriter, T> write, Func<ProtoReader, T> read)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (read == null) throw new ArgumentNullException(nameof(read));
            if (_codecs.ContainsKey(typeof(T)))
                throw new InvalidOperationException($"[ProtobufNetworkSerializer] 消息类型 {typeof(T).Name} 已注册过编解码。");
            _codecs[typeof(T)] = new Codec<T> { Write = write, Read = read };
            return this;
        }

        public byte[] Serialize<T>(T data)
        {
            var writer = new ProtoWriter();
            GetCodec<T>().Write(writer, data);
            return writer.ToArray();
        }

        public T Deserialize<T>(byte[] bytes) => GetCodec<T>().Read(new ProtoReader(bytes));

        public byte[] EncodeEnvelope(string type, byte[] payload)
        {
            var writer = new ProtoWriter();
            writer.WriteString(EnvelopeTypeField, type);
            writer.WriteBytes(EnvelopePayloadField, payload);
            return writer.ToArray();
        }

        public void DecodeEnvelope(byte[] frame, out string type, out byte[] payload)
        {
            type = null;
            payload = null;
            var reader = new ProtoReader(frame);
            while (reader.TryReadTag(out int field, out int wireType))
            {
                switch (field)
                {
                    case EnvelopeTypeField: type = reader.ReadString(); break;
                    case EnvelopePayloadField: payload = reader.ReadBytes(); break;
                    default: reader.SkipField(wireType); break;
                }
            }
        }

        private Codec<T> GetCodec<T>()
        {
            if (!_codecs.TryGetValue(typeof(T), out var codec))
                throw new InvalidOperationException(
                    $"[ProtobufNetworkSerializer] 消息类型 {typeof(T).Name} 未注册编解码——服务注册处先 Register<{typeof(T).Name}>(write, read)。");
            return (Codec<T>)codec;
        }
    }
}
