using System;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Game.Framework.Network
{
    /// <summary>
    /// 官方 Google.Protobuf 版 <see cref="IWebSocketEnvelopeSerializer"/>：消息编解码走 protoc 生成的
    /// <see cref="IMessage"/> 类型（<c>ToByteArray</c> / <see cref="MessageParser"/>），HTTP 体走
    /// <c>application/x-protobuf</c>；WS envelope 用官方 <see cref="CodedOutputStream"/> 手写
    /// <c>{string type=1; bytes payload=2;}</c> + 二进制帧。构造注入替换默认 JSON 或内核手写
    /// <see cref="ProtobufNetworkSerializer"/>，业务调用代码零改动（ADR-0028 §5/§6）。
    /// </summary>
    /// <remarks>
    /// <b>定位</b>：本类住框架增强模块 <c>Game.Framework.Network.Proto</c>——Google.Protobuf 依赖收口在此，
    /// 内核保持第三方零依赖（ports &amp; adapters，同 Asset.Yoo 先例）。配套的 .proto → C# 生成管线在
    /// 同模块 Editor（工作台 <c>SSFramework/代码生成/Protobuf</c>，多套 ProtoConfigProfile 按目录配置）。<br/>
    /// <b>wire 兼容</b>：envelope 字段号与内核手写 <see cref="ProtobufNetworkSerializer"/> 一致（逐字节等价）、
    /// 消息体都是标准 protobuf wire 字节——两实现可对讲、可灰度互换，对端只认 .proto 字段号。<br/>
    /// <b>反序列化注册</b>：<see cref="Register{T}"/>（单消息）/ <see cref="RegisterFile"/>（整个 .proto 文件）
    /// 登记生成代码里的 <see cref="MessageParser"/> 静态实例——不做运行时程序集反射扫描，IL2CPP / HybridCLR AOT
    /// 下行为可预期。注册只在构造 / 服务注册期做；之后字典只读 + 编解码纯函数，任意线程可并发 Serialize / Deserialize。<br/>
    /// <b>IL2CPP 防裁剪</b>：模块自带 link.xml preserve 整个 Google.Protobuf；生成的消息类型住业务程序集，
    /// 是否被裁剪随业务程序集自身配置（热更程序集不经 IL2CPP 裁剪，天然安全）。
    /// </remarks>
    public sealed class GoogleProtobufNetworkSerializer : IWebSocketEnvelopeSerializer
    {
        // envelope 的 proto 字段号（等价 .proto：message Envelope { string type = 1; bytes payload = 2; }）。
        // 与内核 ProtobufNetworkSerializer 是同一契约——改这里 = 破坏两实现互通与对端兼容。
        private const int EnvelopeTypeField = 1;
        private const int EnvelopePayloadField = 2;

        // 类型 → 解析器（基类 MessageParser，ParseFrom 返回 IMessage 再转 T）。
        private readonly Dictionary<Type, MessageParser> _parsers = new();

        public string ContentType => "application/x-protobuf";

        public bool UseBinaryFrames => true;

        /// <summary>注册单个消息类型的解析器（传生成类的静态 <c>T.Parser</c>）。返回自身可链式注册；
        /// 显式点名重复注册抛（代码写错了）。消息集中在少数 .proto 文件时，用 <see cref="RegisterFile"/> 整文件注册更省事。</summary>
        public GoogleProtobufNetworkSerializer Register<T>(MessageParser<T> parser) where T : IMessage<T>
        {
            if (parser == null) throw new ArgumentNullException(nameof(parser));
            AddParser(typeof(T), parser, throwOnDuplicate: true);
            return this;
        }

        /// <summary>
        /// 注册一个 .proto 文件的<b>全部</b>消息类型，传生成代码里的 <c>XxxReflection.Descriptor</c>。
        /// 递归覆盖：① 文件内消息含<b>嵌套</b>类型；② 该文件 <c>import</c> 的<b>依赖文件</b>（传递闭包）——
        /// 跨文件拆分 + import 是 protobuf 常规用法，注册入口只给顶层 file、依赖自动带上，无需逐个 file 点名。
        /// map 字段的内部 entry 类型没有 CLR 类型，自动跳过。
        /// <b>幂等</b>：对已注册类型跳过（多个 file 经 diamond import 共享同一依赖、或 well-known types 被多处引用是常态，
        /// 不视为错误）——与单消息 <see cref="Register{T}"/> 的「显式重复即抛」区分。
        /// </summary>
        public GoogleProtobufNetworkSerializer RegisterFile(FileDescriptor file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            RegisterFileTree(file, new HashSet<FileDescriptor>());
            return this;
        }

        // 传递闭包遍历：先注册依赖文件、再注册本文件消息；visited 去重防 diamond import 重复处理（proto import 无环）。
        private void RegisterFileTree(FileDescriptor file, HashSet<FileDescriptor> visited)
        {
            if (!visited.Add(file)) return;
            foreach (var dependency in file.Dependencies)
                RegisterFileTree(dependency, visited);
            foreach (var message in file.MessageTypes)
                RegisterMessageTree(message);
        }

        private void RegisterMessageTree(MessageDescriptor message)
        {
            if (message.ClrType != null && message.Parser != null) // map entry 等编译器合成类型无 CLR 类型，跳过
                AddParser(message.ClrType, message.Parser, throwOnDuplicate: false); // 整文件注册对已登记类型幂等跳过
            foreach (var nested in message.NestedTypes)
                RegisterMessageTree(nested);
        }

        private void AddParser(Type type, MessageParser parser, bool throwOnDuplicate)
        {
            if (_parsers.ContainsKey(type))
            {
                if (throwOnDuplicate)
                    throw new InvalidOperationException($"[GoogleProtobufNetworkSerializer] {type.Name} 已注册解析器。");
                return; // 幂等：RegisterFile 对已注册类型（共享依赖 / well-known types）跳过
            }
            _parsers[type] = parser;
        }

        public byte[] Serialize<T>(T data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data is not IMessage message)
                throw new InvalidOperationException(
                    $"[GoogleProtobufNetworkSerializer] {typeof(T).Name} 不是 Google.Protobuf IMessage——只能序列化 protoc 生成的消息。");
            return message.ToByteArray();
        }

        public T Deserialize<T>(byte[] bytes)
        {
            if (!_parsers.TryGetValue(typeof(T), out var parser))
                throw new InvalidOperationException(
                    $"[GoogleProtobufNetworkSerializer] {typeof(T).Name} 未注册解析器——服务注册处先 " +
                    $"Register({typeof(T).Name}.Parser) 或 RegisterFile(生成的 XxxReflection.Descriptor)。");
            return (T)parser.ParseFrom(bytes);
        }

        // envelope 用官方 CodedOutputStream 手写（不依赖某个生成的 Envelope 类型，保持本类对任意 IMessage 通用）。
        // proto3 语义：type 空 / payload 空则整字段省略——与内核 ProtobufNetworkSerializer.EncodeEnvelope 逐字节一致。
        // 尺寸先算后写、单次精确分配；payload 用 UnsafeWrap 包装免二次拷贝（包装后的字节不再被改动，满足其安全前提）。
        public byte[] EncodeEnvelope(string type, byte[] payload)
        {
            bool hasType = !string.IsNullOrEmpty(type);
            bool hasPayload = payload is { Length: > 0 };
            ByteString payloadWrapped = hasPayload ? UnsafeByteOperations.UnsafeWrap(payload) : null;

            int size = 0;
            if (hasType)
                size += CodedOutputStream.ComputeTagSize(EnvelopeTypeField) + CodedOutputStream.ComputeStringSize(type);
            if (hasPayload)
                size += CodedOutputStream.ComputeTagSize(EnvelopePayloadField) + CodedOutputStream.ComputeBytesSize(payloadWrapped);

            var frame = new byte[size];
            var output = new CodedOutputStream(frame);
            if (hasType)
            {
                output.WriteTag(EnvelopeTypeField, WireFormat.WireType.LengthDelimited);
                output.WriteString(type);
            }
            if (hasPayload)
            {
                output.WriteTag(EnvelopePayloadField, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(payloadWrapped);
            }
            output.Flush();
            return frame;
        }

        public void DecodeEnvelope(byte[] frame, out string type, out byte[] payload)
        {
            type = null;
            payload = null;
            var input = new CodedInputStream(frame);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case EnvelopeTypeField: type = input.ReadString(); break;
                    case EnvelopePayloadField: payload = input.ReadBytes().ToByteArray(); break;
                    default: input.SkipLastField(); break;
                }
            }
        }
    }
}
