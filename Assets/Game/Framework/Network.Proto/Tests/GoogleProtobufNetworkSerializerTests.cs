using System;
using System.Linq;
using Game.Framework.Network;
using Game.Framework.Test.ProtoGen;
using NUnit.Framework;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证框架增强模块（Game.Framework.Network.Proto）的 <see cref="GoogleProtobufNetworkSerializer"/>：
    /// RegisterFile 整文件注册（含嵌套类型、跳过 map entry）/ 消息往返 / envelope 与内核手写
    /// <see cref="ProtobufNetworkSerializer"/> 逐字节一致且可互解 / 与 <see cref="ProtoWriter"/>/<see cref="ProtoReader"/>
    /// 的 wire 互通 / 失败语义（未注册、重复注册、null）。消息类型来自测试专用契约
    /// <c>Network.Proto/Tests/Proto~/framework_net_test.proto</c>（工作台 SSFramework/代码生成/Protobuf 生成）。纯同步无 Unity 依赖，[Test] 即可。
    /// </summary>
    public class GoogleProtobufNetworkSerializerTests
    {
        private static GoogleProtobufNetworkSerializer CreateSerializer() =>
            new GoogleProtobufNetworkSerializer().RegisterFile(FrameworkNetTestReflection.Descriptor);

        [Test]
        public void RegisterFile_ScalarMessage_RoundTrip()
        {
            var serializer = CreateSerializer();
            var msg = new TestScalarMessage { Name = "哨站-A1", Count = 390, Flag = true, Ratio = -0.25, Delta = -7 };

            var back = serializer.Deserialize<TestScalarMessage>(serializer.Serialize(msg));

            Assert.AreEqual("哨站-A1", back.Name);
            Assert.AreEqual(390, back.Count);
            Assert.IsTrue(back.Flag);
            Assert.AreEqual(-0.25, back.Ratio); // double / 负数 sint32：内核 ProtoWire 刻意不支持、真库该覆盖
            Assert.AreEqual(-7, back.Delta);
        }

        [Test]
        public void RegisterFile_NestedRepeatedMap_RoundTrip_And_NestedTypeRegistered()
        {
            var serializer = CreateSerializer();
            var msg = new TestNestedMessage { Inner = new TestNestedMessage.Types.Inner { Tag = "内层" } };
            msg.Items.Add(new TestScalarMessage { Name = "a", Count = 1 });
            msg.Items.Add(new TestScalarMessage { Name = "b", Count = 2 });
            msg.Scores["alpha"] = 10;
            msg.Scores["beta"] = 20;

            var back = serializer.Deserialize<TestNestedMessage>(serializer.Serialize(msg));
            Assert.AreEqual("内层", back.Inner.Tag);
            Assert.AreEqual(2, back.Items.Count);
            Assert.AreEqual("b", back.Items[1].Name);
            Assert.AreEqual(20, back.Scores["beta"]);

            // 嵌套类型被 RegisterFile 递归注册（可独立编解码）；map entry 合成类型被跳过（不炸即证）
            var inner = new TestNestedMessage.Types.Inner { Tag = "独立" };
            Assert.AreEqual("独立", serializer.Deserialize<TestNestedMessage.Types.Inner>(serializer.Serialize(inner)).Tag);
        }

        [Test]
        public void RegisterFile_RecursesImportDependencies()
        {
            // 只给顶层 file（framework_net_test import framework_net_common）——依赖文件的 CommonMeta 应被递归带上
            var serializer = new GoogleProtobufNetworkSerializer().RegisterFile(FrameworkNetTestReflection.Descriptor);

            var meta = new CommonMeta { Origin = "依赖", Timestamp = 123456789012L };
            var back = serializer.Deserialize<CommonMeta>(serializer.Serialize(meta));
            Assert.AreEqual("依赖", back.Origin);
            Assert.AreEqual(123456789012L, back.Timestamp);
        }

        [Test]
        public void RegisterFile_Idempotent_OnSharedDependency()
        {
            // 顶层 file 递归已注册 CommonMeta，再单独整包注册它所在的依赖 file——幂等跳过、不抛（diamond import 常态）
            var serializer = new GoogleProtobufNetworkSerializer()
                .RegisterFile(FrameworkNetTestReflection.Descriptor)
                .RegisterFile(FrameworkNetCommonReflection.Descriptor);

            var back = serializer.Deserialize<CommonMeta>(serializer.Serialize(new CommonMeta { Origin = "x" }));
            Assert.AreEqual("x", back.Origin);
        }

        [Test]
        public void Envelope_ByteCompatible_WithKernelSerializer()
        {
            var google = CreateSerializer();
            var kernel = new ProtobufNetworkSerializer();
            byte[] payload = google.Serialize(new TestScalarMessage { Name = "载荷", Count = 7 });

            // 三种形态逐字节一致：常规 / 空载荷 / 空 type（proto3 语义：空字段整体省略）
            CollectionAssert.AreEqual(kernel.EncodeEnvelope("evt", payload), google.EncodeEnvelope("evt", payload));
            CollectionAssert.AreEqual(kernel.EncodeEnvelope("evt", Array.Empty<byte>()), google.EncodeEnvelope("evt", Array.Empty<byte>()));
            CollectionAssert.AreEqual(kernel.EncodeEnvelope("", payload), google.EncodeEnvelope("", payload));
        }

        [Test]
        public void Envelope_CrossDecode_BothDirections()
        {
            var google = CreateSerializer();
            var kernel = new ProtobufNetworkSerializer();
            byte[] payload = { 0x0A, 0x02, 0xC3, 0x28 }; // 任意二进制载荷（含非 UTF-8 字节）

            kernel.DecodeEnvelope(google.EncodeEnvelope("push", payload), out string t1, out byte[] p1);
            Assert.AreEqual("push", t1);
            CollectionAssert.AreEqual(payload, p1);

            google.DecodeEnvelope(kernel.EncodeEnvelope("push", payload), out string t2, out byte[] p2);
            Assert.AreEqual("push", t2);
            CollectionAssert.AreEqual(payload, p2);
        }

        [Test]
        public void WireInterop_WithProtoWireHandCodec_BothDirections()
        {
            // Google 编 → ProtoReader 手写读（只用 ProtoWire 支持的 varint / length-delimited 子集）
            var serializer = CreateSerializer();
            byte[] googleBytes = serializer.Serialize(new TestScalarMessage { Name = "互通", Count = 42, Flag = true });
            var r = new ProtoReader(googleBytes);
            string name = null; int count = 0; bool flag = false;
            while (r.TryReadTag(out int field, out int wireType))
            {
                switch (field)
                {
                    case 1: name = r.ReadString(); break;
                    case 2: count = r.ReadInt32(); break;
                    case 3: flag = r.ReadBool(); break;
                    default: r.SkipField(wireType); break;
                }
            }
            Assert.AreEqual("互通", name);
            Assert.AreEqual(42, count);
            Assert.IsTrue(flag);

            // ProtoWriter 手写 → Google Parser 解
            var w = new ProtoWriter();
            w.WriteString(1, "互通");
            w.WriteInt32(2, 42);
            w.WriteBool(3, true);
            var parsed = TestScalarMessage.Parser.ParseFrom(w.ToArray());
            Assert.AreEqual("互通", parsed.Name);
            Assert.AreEqual(42, parsed.Count);
            Assert.IsTrue(parsed.Flag);
        }

        [Test]
        public void FailureSemantics_Unregistered_Duplicate_Null()
        {
            var serializer = CreateSerializer();

            // 未注册类型：抛 InvalidOperationException（代码写错了，指路 Register/RegisterFile）
            var empty = new GoogleProtobufNetworkSerializer();
            Assert.Throws<InvalidOperationException>(() => empty.Deserialize<TestScalarMessage>(new byte[] { 0x08, 0x01 }));

            // 重复注册同类型：抛（RegisterFile 后再单独 Register 同消息）
            Assert.Throws<InvalidOperationException>(() => serializer.Register(TestScalarMessage.Parser));

            // null 消息：抛 ArgumentNullException（而非误导性的“不是 IMessage”）
            Assert.Throws<ArgumentNullException>(() => serializer.Serialize<TestScalarMessage>(null));
        }
    }
}
