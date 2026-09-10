using System;
using System.IO;
using System.Text;

namespace Game.Framework.Network
{
    /// <summary>
    /// 最小 Protobuf wire 格式写入器（varint + length-delimited 两种编码，覆盖常规消息形态）。
    /// 与 <see cref="ProtoReader"/> 配对，供 <see cref="ProtobufNetworkSerializer"/> 的 per-message
    /// 编解码函数使用。字节与标准 protobuf 完全互通——字段号与对端 .proto 对上即可直接对讲，
    /// 将来换 Google.Protobuf / protobuf-net 等真库线上格式不变。
    /// </summary>
    /// <remarks>
    /// proto3 语义：标量字段等于默认值（0 / false / 空串）时不写——读侧用字段初始值兜住。
    /// 写入只支持非负 int32（分数 / 计数 / id 类字段的常态；标准 int32 负数用十字节补码 varint，
    /// sint32 才用 zigzag；本写入器刻意不扩展这两条路径——
    /// 真需要有符号 / 64 位 / 浮点时就该换真 protobuf 库了，见 <see cref="ProtobufNetworkSerializer"/> 的定位说明）。
    /// 纯 C#、无状态依赖，可在任意线程使用。
    /// </remarks>
    public sealed class ProtoWriter
    {
        private readonly MemoryStream _ms = new();

        public byte[] ToArray() => _ms.ToArray();

        /// <summary>写非负 int32 字段（varint）。值为 0 按 proto3 语义省略；负数不在此写入器支持范围内。</summary>
        public void WriteInt32(int fieldNumber, int value)
        {
            ValidateFieldNumber(fieldNumber);
            if (value == 0) return;
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "ProtoWriter 只支持非负 int32。");
            WriteTag(fieldNumber, wireType: 0);
            WriteVarint((uint)value);
        }

        /// <summary>写 bool 字段（varint 0/1）。false 按 proto3 语义省略。</summary>
        public void WriteBool(int fieldNumber, bool value)
        {
            ValidateFieldNumber(fieldNumber);
            if (!value) return;
            WriteTag(fieldNumber, wireType: 0);
            WriteVarint(1);
        }

        /// <summary>写 string 字段（UTF-8 length-delimited）。null / 空串按 proto3 语义省略。</summary>
        public void WriteString(int fieldNumber, string value)
        {
            ValidateFieldNumber(fieldNumber);
            if (string.IsNullOrEmpty(value)) return;
            WriteLengthDelimited(fieldNumber, Encoding.UTF8.GetBytes(value));
        }

        /// <summary>写 bytes 字段。null / 空按 proto3 语义省略。</summary>
        public void WriteBytes(int fieldNumber, byte[] value)
        {
            ValidateFieldNumber(fieldNumber);
            if (value == null || value.Length == 0) return;
            WriteLengthDelimited(fieldNumber, value);
        }

        /// <summary>
        /// 写嵌套消息 / repeated 消息的一个元素（length-delimited）。
        /// 与 <see cref="WriteBytes"/> 的差别：空消息（全字段默认值 → 零长度）也要写 tag——
        /// repeated 的元素个数由 tag 出现次数决定，省略空元素会丢条目。
        /// </summary>
        public void WriteMessage(int fieldNumber, byte[] encodedMessage)
        {
            ValidateFieldNumber(fieldNumber);
            WriteLengthDelimited(fieldNumber, encodedMessage ?? Array.Empty<byte>());
        }

        private void WriteLengthDelimited(int fieldNumber, byte[] bytes)
        {
            WriteTag(fieldNumber, wireType: 2);
            WriteVarint((uint)bytes.Length);
            _ms.Write(bytes, 0, bytes.Length);
        }

        private void WriteTag(int fieldNumber, int wireType) => WriteVarint((uint)((fieldNumber << 3) | wireType));

        private static void ValidateFieldNumber(int fieldNumber)
        {
            if (fieldNumber <= 0 || fieldNumber > 0x1FFFFFFF)
                throw new ArgumentOutOfRangeException(nameof(fieldNumber), "Protobuf 字段号必须在 1 到 536870911 之间。");
        }

        private void WriteVarint(uint value)
        {
            while (value >= 0x80)
            {
                _ms.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            _ms.WriteByte((byte)value);
        }
    }

    /// <summary>
    /// 最小 Protobuf wire 格式读取器（配对 <see cref="ProtoWriter"/>）。
    /// 标准姿势：<c>while (TryReadTag(...)) switch (fieldNumber) { ...; default: SkipField(wireType); }</c>——
    /// 未知字段按 wire 类型跳过（协议演进宽容性，与真 protobuf 一致）；字节损坏 / 越界抛
    /// <see cref="InvalidDataException"/>，由网络层折叠为 DeserializeError（<see cref="INetworkSerializer"/> 实现契约：不吞）。
    /// </summary>
    public sealed class ProtoReader
    {
        private readonly byte[] _buf;
        private readonly int _end;
        private int _pos;

        /// <summary>读取完整的 Protobuf 字节数组。</summary>
        /// <param name="buffer">消息字节；调用方在读取期间不得修改。</param>
        public ProtoReader(byte[] buffer) : this(buffer, 0,
            buffer?.Length ?? throw new ArgumentNullException(nameof(buffer))) { }

        /// <summary>读取字节数组中的指定消息片段。</summary>
        /// <param name="buffer">消息字节；调用方在读取期间不得修改。</param>
        /// <param name="offset">片段起始偏移。</param>
        /// <param name="length">片段长度。</param>
        public ProtoReader(byte[] buffer, int offset, int length)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length < 0 || length > buffer.Length - offset) throw new ArgumentOutOfRangeException(nameof(length));
            _buf = buffer;
            _pos = offset;
            _end = offset + length;
        }

        /// <summary>读下一个字段 tag；到达消息末尾返回 false。</summary>
        public bool TryReadTag(out int fieldNumber, out int wireType)
        {
            if (_pos >= _end)
            {
                fieldNumber = 0;
                wireType = 0;
                return false;
            }
            ulong tag = ReadVarint();
            if (tag > uint.MaxValue) throw new InvalidDataException("Protobuf tag 超出 32 位范围——字节流损坏。");
            fieldNumber = (int)(tag >> 3);
            wireType = (int)(tag & 0x7);
            if (fieldNumber == 0) throw new InvalidDataException("Protobuf 字段号 0 非法——字节流损坏。");
            if (wireType > 5) throw new InvalidDataException($"未知 protobuf wire 类型 {wireType}——字节流损坏。");
            return true;
        }

        public int ReadInt32() => unchecked((int)ReadVarint());

        public bool ReadBool() => ReadVarint() != 0;

        public string ReadString()
        {
            int len = ReadLength();
            string s = Encoding.UTF8.GetString(_buf, _pos, len);
            _pos += len;
            return s;
        }

        public byte[] ReadBytes()
        {
            int len = ReadLength();
            var bytes = new byte[len];
            Buffer.BlockCopy(_buf, _pos, bytes, 0, len);
            _pos += len;
            return bytes;
        }

        /// <summary>读嵌套消息：返回覆盖其字节区间的子 reader（零拷贝），游标越过该消息。</summary>
        public ProtoReader ReadMessage()
        {
            int len = ReadLength();
            var sub = new ProtoReader(_buf, _pos, len);
            _pos += len;
            return sub;
        }

        /// <summary>跳过一个未知字段（按 wire 类型确定长度）。</summary>
        public void SkipField(int wireType)
        {
            switch (wireType)
            {
                case 0: ReadVarint(); break; // varint
                case 1: Advance(8); break;   // fixed64
                case 2: Advance(ReadLength()); break; // length-delimited
                case 5: Advance(4); break;   // fixed32
                default: throw new InvalidDataException($"未知 protobuf wire 类型 {wireType}——字节流损坏。");
            }
        }

        private int ReadLength()
        {
            // 先以完整 64 位值验证剩余区间，再缩窄；先转 int 或相加都会让损坏长度溢出后绕过边界。
            ulong len = ReadVarint();
            if (len > (ulong)(_end - _pos)) throw new InvalidDataException("Protobuf 长度字段越界——字节流损坏。");
            return (int)len;
        }

        private void Advance(int count)
        {
            if (count < 0 || count > _end - _pos) throw new InvalidDataException("Protobuf 读取越界——字节流损坏。");
            _pos += count;
        }

        private ulong ReadVarint()
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                if (_pos >= _end) throw new InvalidDataException("Protobuf varint 未终止——字节流损坏。");
                byte b = _buf[_pos++];
                // 第十字节只剩第 64 位可用；更高 payload 或继续位都是溢出，不能静默截断。
                if (shift == 63 && b > 1) throw new InvalidDataException("Protobuf varint 超出 64 位范围——字节流损坏。");
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift >= 64) throw new InvalidDataException("Protobuf varint 超长——字节流损坏。");
            }
        }
    }
}
