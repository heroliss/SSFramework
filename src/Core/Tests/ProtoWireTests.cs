using System;
using System.Collections.Generic;
using System.IO;
using Game.Framework.Network;
using NUnit.Framework;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证 Protobuf wire 编解码（<see cref="ProtoWriter"/> / <see cref="ProtoReader"/>）与注册式序列化器
    /// （<see cref="ProtobufNetworkSerializer"/>）：字段往返 / proto3 默认值省略 / 未知字段跳过（协议演进）/
    /// 损坏字节抛 / envelope 往返。纯同步无 Unity 依赖，[Test] 即可。
    /// </summary>
    public class ProtoWireTests
    {
        private sealed class Score
        {
            public string Player;
            public int Value;
            public bool Verified;
        }

        private static void WriteScore(ProtoWriter w, Score s)
        {
            w.WriteString(1, s.Player);
            w.WriteInt32(2, s.Value);
            w.WriteBool(3, s.Verified);
        }

        private static Score ReadScore(ProtoReader r)
        {
            var s = new Score();
            while (r.TryReadTag(out int field, out int wireType))
            {
                switch (field)
                {
                    case 1: s.Player = r.ReadString(); break;
                    case 2: s.Value = r.ReadInt32(); break;
                    case 3: s.Verified = r.ReadBool(); break;
                    default: r.SkipField(wireType); break;
                }
            }
            return s;
        }

        [Test]
        public void Fields_RoundTrip()
        {
            var w = new ProtoWriter();
            WriteScore(w, new Score { Player = "哨站-A1", Value = 390, Verified = true });
            var s = ReadScore(new ProtoReader(w.ToArray()));

            Assert.AreEqual("哨站-A1", s.Player);
            Assert.AreEqual(390, s.Value);
            Assert.IsTrue(s.Verified);
        }

        [Test]
        public void DefaultValues_OmittedOnWire_RestoredOnRead()
        {
            var w = new ProtoWriter();
            WriteScore(w, new Score { Player = "", Value = 0, Verified = false });
            byte[] bytes = w.ToArray();

            Assert.AreEqual(0, bytes.Length, "proto3 语义：全默认值消息应是零字节");
            var s = ReadScore(new ProtoReader(bytes));
            Assert.AreEqual(0, s.Value);
            Assert.IsFalse(s.Verified);
            Assert.IsNull(s.Player); // 读侧默认值 = 字段初始值（本测试类型初始 null）
        }

        [Test]
        public void UnknownFields_Skipped_ProtocolEvolution()
        {
            // "新版本"多写两个字段（varint + length-delimited），旧读侧应跳过并读出认识的部分
            var w = new ProtoWriter();
            w.WriteInt32(9, 12345);
            w.WriteString(10, "future-field");
            WriteScore(w, new Score { Player = "p", Value = 7 });

            var s = ReadScore(new ProtoReader(w.ToArray()));
            Assert.AreEqual("p", s.Player);
            Assert.AreEqual(7, s.Value);
        }

        [Test]
        public void RepeatedMessages_CountPreserved_IncludingEmptyElement()
        {
            var w = new ProtoWriter();
            foreach (var s in new[] { new Score { Player = "a", Value = 1 }, new Score(), new Score { Player = "c" } })
            {
                var item = new ProtoWriter();
                WriteScore(item, s);
                w.WriteMessage(1, item.ToArray()); // 空消息元素也要占位——个数由 tag 次数决定
            }

            var list = new List<Score>();
            var r = new ProtoReader(w.ToArray());
            while (r.TryReadTag(out int field, out int wireType))
            {
                if (field == 1) list.Add(ReadScore(r.ReadMessage()));
                else r.SkipField(wireType);
            }

            Assert.AreEqual(3, list.Count);
            Assert.AreEqual("a", list[0].Player);
            Assert.IsNull(list[1].Player);
            Assert.AreEqual("c", list[2].Player);
        }

        [Test]
        public void NegativeInt32_Throws() =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProtoWriter().WriteInt32(1, -1));

        [Test]
        public void TruncatedBytes_ThrowInvalidData()
        {
            var w = new ProtoWriter();
            w.WriteString(1, "hello world");
            byte[] bytes = w.ToArray();
            var truncated = new byte[bytes.Length - 4];
            Buffer.BlockCopy(bytes, 0, truncated, 0, truncated.Length);

            Assert.Throws<InvalidDataException>(() => ReadScore(new ProtoReader(truncated)));
        }

        // ── ProtobufNetworkSerializer ────────────────────────────────────────

        [Test]
        public void Reader_RejectsInvalidBufferSlices()
        {
            Assert.Throws<ArgumentNullException>(() => new ProtoReader(null));
            Assert.Throws<ArgumentNullException>(() => new ProtoReader(null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProtoReader(new byte[1], -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProtoReader(new byte[1], 2, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProtoReader(new byte[1], 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProtoReader(new byte[1], 1, int.MaxValue));
            Assert.IsFalse(new ProtoReader(new byte[1], 1, 0).TryReadTag(out _, out _));
        }

        [Test]
        public void LengthOverflow_ThrowsBeforeAllocatingOrMovingCursor()
        {
            // int.MaxValue + 当前游标会溢出；2^32 则会在 uint 截断后伪装成零长度。
            foreach (var bytes in new[] {
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
                new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 } })
            {
                Assert.Throws<InvalidDataException>(() => new ProtoReader(bytes).ReadMessage());
                Assert.Throws<InvalidDataException>(() => new ProtoReader(bytes).ReadBytes());
                Assert.Throws<InvalidDataException>(() => new ProtoReader(bytes).SkipField(2));
            }
        }

        [Test]
        public void Varint_RejectsOverflowButCanSkipFullUInt64()
        {
            var bytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
            var reader = new ProtoReader(bytes);
            reader.SkipField(0);
            Assert.IsFalse(reader.TryReadTag(out _, out _));
            bytes[9] = 0x02;
            Assert.Throws<InvalidDataException>(() => new ProtoReader(bytes).SkipField(0));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(0x20000000)]
        public void Writer_RejectsInvalidFieldNumbers_EvenForOmittedValues(int field)
        {
            var writer = new ProtoWriter();
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteInt32(field, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBool(field, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteString(field, ""));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBytes(field, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteMessage(field, null));
            Assert.IsEmpty(writer.ToArray());
        }

        [Test]
        public void Tags_RejectOverflowAndInvalidWireTypes_AndPreserveMaximumField()
        {
            Assert.Throws<InvalidDataException>(() =>
                new ProtoReader(new byte[] { 0x88, 0x80, 0x80, 0x80, 0x10 }).TryReadTag(out _, out _));
            Assert.Throws<InvalidDataException>(() => new ProtoReader(new byte[] { 0x0E }).TryReadTag(out _, out _));
            var writer = new ProtoWriter();
            writer.WriteBool(0x1FFFFFFF, true);
            var reader = new ProtoReader(writer.ToArray());
            Assert.IsTrue(reader.TryReadTag(out int field, out int wire));
            Assert.AreEqual(0x1FFFFFFF, field);
            Assert.AreEqual(0, wire);
            Assert.IsTrue(reader.ReadBool());
        }

        private static ProtobufNetworkSerializer CreateSerializer() =>
            new ProtobufNetworkSerializer().Register<Score>(WriteScore, ReadScore);

        [Test]
        public void Serializer_RoundTrip_AndContentType()
        {
            var serializer = CreateSerializer();
            Assert.AreEqual("application/x-protobuf", serializer.ContentType);
            Assert.IsTrue(serializer.UseBinaryFrames);

            var s = serializer.Deserialize<Score>(serializer.Serialize(new Score { Player = "x", Value = 42 }));
            Assert.AreEqual("x", s.Player);
            Assert.AreEqual(42, s.Value);
        }

        [Test]
        public void Serializer_UnregisteredType_Throws()
        {
            var serializer = CreateSerializer();
            Assert.Throws<InvalidOperationException>(() => serializer.Serialize(new object()));
            Assert.Throws<InvalidOperationException>(() => serializer.Deserialize<string>(Array.Empty<byte>()));
        }

        [Test]
        public void Serializer_DuplicateRegister_Throws() =>
            Assert.Throws<InvalidOperationException>(() => CreateSerializer().Register<Score>(WriteScore, ReadScore));

        [Test]
        public void Envelope_RoundTrip()
        {
            var serializer = CreateSerializer();
            byte[] payload = serializer.Serialize(new Score { Player = "y", Value = 9 });
            byte[] frame = serializer.EncodeEnvelope("score", payload);

            serializer.DecodeEnvelope(frame, out string type, out byte[] decoded);
            Assert.AreEqual("score", type);
            Assert.AreEqual(payload, decoded);
        }

        [TestCase(0x08)]
        [TestCase(0x10)]
        public void Envelope_KnownFieldWithWrongWireType_Throws(int tag)
        {
            // 错误 wire=0 不能把随后的 varint 偷换成长度，拼出看似合法的字符串/载荷。
            var frame = new byte[] { (byte)tag, 1, 65 };
            Assert.Throws<InvalidDataException>(() => CreateSerializer().DecodeEnvelope(frame, out _, out _));
        }

        [Test]
        public void Reader_SliceBoundary_IsIndependentOfBackingBuffer()
        {
            var reader = new ProtoReader(new byte[] { 0, 2, 65, 66, 0 }, 1, 2);
            Assert.Throws<InvalidDataException>(() => reader.ReadString());
            var negative = new ProtoReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 1 });
            Assert.AreEqual(-1, negative.ReadInt32(), "读取仍兼容对端标准 int32 的十字节负值编码。");
        }

        [Test]
        public void Envelope_EmptyPayload_TypeStillDecodes()
        {
            var serializer = CreateSerializer();
            byte[] frame = serializer.EncodeEnvelope("ping", Array.Empty<byte>());

            serializer.DecodeEnvelope(frame, out string type, out byte[] payload);
            Assert.AreEqual("ping", type);
            Assert.IsTrue(payload == null || payload.Length == 0);
        }
    }
}
