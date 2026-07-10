using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Event;
using Game.Framework.Network;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证 WebSocket 通道（ADR-0028）编排逻辑：状态机 / Connect 失败回滚 / 推送→事件（主线程送达）/
    /// 未注册 type 丢弃 / 坏消息不毒化循环 / 发送 FIFO / 未连接抛 / 断开事件（主动 vs 意外）/ Dispose。
    /// 全走 FakeWebSocketProvider（确定性、无真实网络）；真实 ClientWebSocket ↔ 服务器路径由 demo 章 Play 走查覆盖
    /// （spike 已证真实路径可通，RFC6455 服务器在 demo 侧，测试不重复易碎的二进制帧代码）。
    /// </summary>
    public class WebSocketTests
    {
        [Serializable]
        private struct ChatPush : IEvent
        {
            public string Text;
            public int UserId;
        }

        // Send 的 payload 需为 class（IWebSocketUtility.Send<T> 约束）；接收侧事件是 struct。
        [Serializable]
        private class ChatOutbound
        {
            public string Text;
        }

        // envelope 构造用（与 WebSocketUtility 内部 Envelope 同形），JsonUtility 自动转义 payload 字符串。
        [Serializable]
        private struct Env
        {
            public string type;
            public string payload;
        }

        /// <summary>可编程 WS 传输桩：测试注入「收到的消息 / 远端关闭」、捕获发出的帧、可控 Connect / Send 失败与阻塞。</summary>
        private sealed class FakeWebSocketProvider : IWebSocketProvider
        {
            public bool FailConnect;
            public bool FailSend; // 模拟「调用时已连接、写 socket 才失败」（对端刚断）
            public readonly List<byte[]> Sent = new();
            public readonly List<bool> SentBinary = new(); // 与 Sent 一一对应：每帧的 binary 标记
            public UniTaskCompletionSource SendGate;    // 非 null 时 SendAsync 先等它——用于测发送 FIFO 保序
            public UniTaskCompletionSource ConnectGate; // 非 null 时 ConnectAsync 先等它——用于测 Connecting 中途取消

            private readonly Queue<UniTaskCompletionSource<byte[]>> _pendingReceives = new();
            private readonly Queue<byte[]> _buffered = new(); // null 元素 = 远端关闭

            public async UniTask ConnectAsync(Uri uri, CancellationToken ct)
            {
                if (FailConnect) throw new Exception("fake connect failure");
                if (ConnectGate != null) await ConnectGate.Task.AttachExternalCancellation(ct);
            }

            public async UniTask SendAsync(byte[] payload, bool binary, CancellationToken ct)
            {
                if (SendGate != null) await SendGate.Task;
                if (FailSend) throw new Exception("fake send failure");
                Sent.Add(payload);
                SentBinary.Add(binary);
            }

            public UniTask<byte[]> ReceiveAsync(CancellationToken ct)
            {
                if (_buffered.Count > 0) return UniTask.FromResult(_buffered.Dequeue());
                var tcs = new UniTaskCompletionSource<byte[]>();
                _pendingReceives.Enqueue(tcs);
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            }

            public UniTask CloseAsync(CancellationToken ct) => UniTask.CompletedTask;
            public void Dispose() { }

            public void InjectMessage(byte[] msg) => Deliver(msg);
            public void InjectRemoteClose() => Deliver(null);

            private void Deliver(byte[] msg)
            {
                // 断开后重连时，旧接收循环被取消的挂起项还在队列里（TrySetResult 返回 false）——跳过找到活的那个
                while (_pendingReceives.Count > 0)
                    if (_pendingReceives.Dequeue().TrySetResult(msg))
                        return;
                _buffered.Enqueue(msg);
            }
        }

        private FakeWebSocketProvider _fake;
        private WebSocketUtility _ws;
        private GameContext _ctx;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeWebSocketProvider();
            _ws = new WebSocketUtility(_fake);
            var builder = new ContainerBuilder();
            builder.RegisterOwned(_ws, typeof(IWebSocketUtility)); // 注册即注入 → AttachTo 回填 _context（SendEvent 所需）
            _ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
        }

        [TearDown]
        public void TearDown() => _ctx.Dispose(); // 级联 Dispose _ws

        private static byte[] Envelope(string type, string payloadJson)
            => Encoding.UTF8.GetBytes(JsonUtility.ToJson(new Env { type = type, payload = payloadJson }));

        [UnityTest]
        public IEnumerator Connect_TransitionsToConnected() => UniTask.ToCoroutine(async () =>
        {
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            await _ws.Connect("ws://fake/");
            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue);
        });

        [UnityTest]
        public IEnumerator Connect_Failure_RollsBackState_ThrowsConnectionError() => UniTask.ToCoroutine(async () =>
        {
            _fake.FailConnect = true;
            try
            {
                await _ws.Connect("ws://fake/");
                Assert.Fail("连接失败应抛 NetworkException");
            }
            catch (NetworkException e)
            {
                Assert.AreEqual(NetworkErrorKind.ConnectionError, e.Kind);
            }
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue); // 状态回滚
        });

        [UnityTest]
        public IEnumerator Connect_WhileConnected_Throws() => UniTask.ToCoroutine(async () =>
        {
            await _ws.Connect("ws://fake/");
            try
            {
                await _ws.Connect("ws://fake/");
                Assert.Fail("重复 Connect 应抛 InvalidOperationException");
            }
            catch (InvalidOperationException) { /* 预期 */ }
        });

        [UnityTest]
        public IEnumerator Push_MappedType_DeliveredAsEvent() => UniTask.ToCoroutine(async () =>
        {
            _ws.RegisterPush<ChatPush>("chat");
            ChatPush? received = null;
            using var sub = _ctx.RegisterEvent<ChatPush>(e => received = e);

            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1); // 让接收循环挂到第一次 ReceiveAsync
            _fake.InjectMessage(Envelope("chat", JsonUtility.ToJson(new ChatPush { Text = "hi", UserId = 7 })));
            await UniTask.DelayFrame(2); // 让循环切主线程 + 扇出

            Assert.IsTrue(received.HasValue, "映射的推送应转成事件送达");
            Assert.AreEqual("hi", received.Value.Text);
            Assert.AreEqual(7, received.Value.UserId);
        });

        [UnityTest]
        public IEnumerator Push_UnregisteredType_WarnsAndDrops() => UniTask.ToCoroutine(async () =>
        {
            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("未注册的推送 type"));
            _fake.InjectMessage(Envelope("unknown", "{}"));
            await UniTask.DelayFrame(2);
            // 无异常、连接仍在（未毒化）
            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue);
        });

        [UnityTest]
        public IEnumerator Push_MalformedMessage_DroppedButLoopContinues() => UniTask.ToCoroutine(async () =>
        {
            _ws.RegisterPush<ChatPush>("chat");
            ChatPush? received = null;
            using var sub = _ctx.RegisterEvent<ChatPush>(e => received = e);
            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("无法解析的 envelope"));
            _fake.InjectMessage(Encoding.UTF8.GetBytes("not-json-at-all###")); // 坏 envelope
            await UniTask.DelayFrame(2);

            // 坏消息后循环仍活：紧接一条正常消息应正常送达
            _fake.InjectMessage(Envelope("chat", JsonUtility.ToJson(new ChatPush { Text = "ok", UserId = 1 })));
            await UniTask.DelayFrame(2);
            Assert.IsTrue(received.HasValue, "坏消息不应毒化接收循环");
            Assert.AreEqual("ok", received.Value.Text);
        });

        [UnityTest]
        public IEnumerator Send_PreservesOrder_FIFO() => UniTask.ToCoroutine(async () =>
        {
            await _ws.Connect("ws://fake/");

            // 第一条发送阻塞在 gate 上，此时发第二条：FIFO 尾链应让第二条排在第一条之后。
            var gate = new UniTaskCompletionSource();
            _fake.SendGate = gate;
            var t1 = _ws.Send("a", new ChatOutbound { Text = "first" });
            _fake.SendGate = null;
            var t2 = _ws.Send("b", new ChatOutbound { Text = "second" });

            gate.TrySetResult(); // 放行第一条
            await t1;
            await t2;

            Assert.AreEqual(2, _fake.Sent.Count);
            StringAssert.Contains("\"type\":\"a\"", Encoding.UTF8.GetString(_fake.Sent[0]));
            StringAssert.Contains("\"type\":\"b\"", Encoding.UTF8.GetString(_fake.Sent[1]));
        });

        [UnityTest]
        public IEnumerator Send_NoPayload_EncodesEmptyPayload() => UniTask.ToCoroutine(async () =>
        {
            await _ws.Connect("ws://fake/");
            await _ws.Send("ping");
            Assert.AreEqual(1, _fake.Sent.Count);
            string json = Encoding.UTF8.GetString(_fake.Sent[0]);
            StringAssert.Contains("\"type\":\"ping\"", json);
        });

        [UnityTest]
        public IEnumerator Send_MidFlightFailure_WrapsAsConnectionError() => UniTask.ToCoroutine(async () =>
        {
            // EnsureConnected 只挡「调用时未连接」；写 socket 中途失败（对端刚断）也必须折叠为
            // NetworkException(ConnectionError)，不能让 WebSocketException 之类传输层原始异常泄给业务。
            await _ws.Connect("ws://fake/");
            _fake.FailSend = true;
            try
            {
                await _ws.Send("a", new ChatOutbound { Text = "x" });
                Assert.Fail("发送中途失败应折叠为 NetworkException(ConnectionError)");
            }
            catch (NetworkException e)
            {
                Assert.AreEqual(NetworkErrorKind.ConnectionError, e.Kind);
            }
        });

        [UnityTest]
        public IEnumerator Disconnect_WhileConnecting_CancelsConnect_NoClosedEvent() => UniTask.ToCoroutine(async () =>
        {
            _fake.ConnectGate = new UniTaskCompletionSource(); // 让 Connect 挂在半路
            WebSocketClosedEvent? closed = null;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e => closed = e);

            UniTask connecting = _ws.Connect("ws://fake/");
            Assert.AreEqual(NetworkConnectionState.Connecting, _ws.State.CurrentValue);

            await _ws.Disconnect(); // Connecting 期间：取消在途 Connect，而不是发出与实际不符的关闭事件
            try
            {
                await connecting;
                Assert.Fail("被 Disconnect 取消的 Connect 应抛 OCE");
            }
            catch (OperationCanceledException) { /* 预期 */ }

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.IsFalse(closed.HasValue, "从未连接成功，不应发 ClosedEvent");
        });

        [UnityTest]
        public IEnumerator Reconnect_AfterDisconnect_PushStillDelivered() => UniTask.ToCoroutine(async () =>
        {
            _ws.RegisterPush<ChatPush>("chat");
            ChatPush? received = null;
            using var sub = _ctx.RegisterEvent<ChatPush>(e => received = e);

            await _ws.Connect("ws://fake/");
            await _ws.Disconnect();
            await _ws.Connect("ws://fake/"); // 断开后同一实例可重连，推送注册表保留
            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue);

            await UniTask.DelayFrame(1); // 让新接收循环挂到 ReceiveAsync
            _fake.InjectMessage(Envelope("chat", JsonUtility.ToJson(new ChatPush { Text = "again", UserId = 2 })));
            await UniTask.DelayFrame(2);

            Assert.IsTrue(received.HasValue, "重连后的推送应照常送达");
            Assert.AreEqual("again", received.Value.Text);
        });

        [UnityTest]
        public IEnumerator Send_WhenNotConnected_ThrowsConnectionError() => UniTask.ToCoroutine(async () =>
        {
            try
            {
                await _ws.Send("a", new ChatOutbound { Text = "x" });
                Assert.Fail("未连接发送应抛 NetworkException");
            }
            catch (NetworkException e)
            {
                Assert.AreEqual(NetworkErrorKind.ConnectionError, e.Kind);
            }
        });

        [UnityTest]
        public IEnumerator Disconnect_FiresClosedEvent_ByUserTrue() => UniTask.ToCoroutine(async () =>
        {
            WebSocketClosedEvent? closed = null;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e => closed = e);
            await _ws.Connect("ws://fake/");
            await _ws.Disconnect();

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.IsTrue(closed.HasValue);
            Assert.IsTrue(closed.Value.ByUser, "主动断开 ByUser 应为 true");
        });

        [UnityTest]
        public IEnumerator RemoteClose_FiresClosedEvent_ByUserFalse() => UniTask.ToCoroutine(async () =>
        {
            WebSocketClosedEvent? closed = null;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e => closed = e);
            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);

            _fake.InjectRemoteClose(); // ReceiveAsync 返回 null = 对端关闭
            await UniTask.DelayFrame(2);

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.IsTrue(closed.HasValue);
            Assert.IsFalse(closed.Value.ByUser, "意外断开 ByUser 应为 false");
        });

        [Test]
        public void RegisterPush_DuplicateType_Throws()
        {
            _ws.RegisterPush<ChatPush>("chat");
            Assert.Throws<InvalidOperationException>(() => _ws.RegisterPush<ChatPush>("chat"));
        }

        // ── IWebSocketEnvelopeSerializer 路径（二进制格式接管 envelope）─────────

        /// <summary>
        /// 测试用二进制 envelope 序列化器：payload = JSON 字节前加 <c>0xFF 0x00</c> 魔数（0xFF 是非法 UTF-8 起始字节，
        /// 一旦 utility 内部对 payload 做过字符串 round-trip 就会被替换损坏、Deserialize 的魔数校验立刻揭穿）；
        /// envelope = <c>[1字节 type 长度][type UTF8][payload]</c>。
        /// </summary>
        private sealed class BinaryEnvelopeSerializer : IWebSocketEnvelopeSerializer
        {
            public string ContentType => "application/x-test-binary";
            public bool UseBinaryFrames => true;

            public byte[] Serialize<T>(T data)
            {
                byte[] json = Encoding.UTF8.GetBytes(JsonUtility.ToJson(data));
                var bytes = new byte[json.Length + 2];
                bytes[0] = 0xFF;
                bytes[1] = 0x00;
                Buffer.BlockCopy(json, 0, bytes, 2, json.Length);
                return bytes;
            }

            public T Deserialize<T>(byte[] bytes)
            {
                if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0x00)
                    throw new InvalidOperationException("payload 魔数缺失——字节被破坏（疑似经过了字符串 round-trip）。");
                return JsonUtility.FromJson<T>(Encoding.UTF8.GetString(bytes, 2, bytes.Length - 2));
            }

            public byte[] EncodeEnvelope(string type, byte[] payload)
            {
                byte[] typeBytes = Encoding.UTF8.GetBytes(type);
                var frame = new byte[1 + typeBytes.Length + payload.Length];
                frame[0] = (byte)typeBytes.Length;
                Buffer.BlockCopy(typeBytes, 0, frame, 1, typeBytes.Length);
                Buffer.BlockCopy(payload, 0, frame, 1 + typeBytes.Length, payload.Length);
                return frame;
            }

            public void DecodeEnvelope(byte[] frame, out string type, out byte[] payload)
            {
                int typeLen = frame[0]; // 空帧 / 越界直接抛（IndexOutOfRange）——契约就是解不了抛、utility 兜住
                type = Encoding.UTF8.GetString(frame, 1, typeLen);
                payload = new byte[frame.Length - 1 - typeLen];
                Buffer.BlockCopy(frame, 1 + typeLen, payload, 0, payload.Length);
            }
        }

        // 与 SetUp 的默认 JSON 实例并行：envelope 序列化器路径用独立的一套（构造后由调用方负责 ctx.Dispose）。
        private static (FakeWebSocketProvider fake, WebSocketUtility ws, GameContext ctx) CreateBinaryWs()
        {
            var fake = new FakeWebSocketProvider();
            var ws = new WebSocketUtility(fake, new BinaryEnvelopeSerializer());
            var builder = new ContainerBuilder();
            builder.RegisterOwned(ws, typeof(IWebSocketUtility));
            return (fake, ws, new GameContext(builder.Build(), inheritFromGlobal: false));
        }

        [UnityTest]
        public IEnumerator EnvelopeSerializer_Send_BinaryFrame_PayloadBytesIntact() => UniTask.ToCoroutine(async () =>
        {
            var (fake, ws, ctx) = CreateBinaryWs();
            try
            {
                await ws.Connect("ws://fake/");
                await ws.Send("chat", new ChatOutbound { Text = "二进制" });

                Assert.AreEqual(1, fake.Sent.Count);
                Assert.IsTrue(fake.SentBinary[0], "envelope 序列化器 UseBinaryFrames=true 时应发二进制帧");

                // 帧能按自定 envelope 解回、payload 魔数完好 = 全程 byte[]、无字符串 round-trip
                var serializer = new BinaryEnvelopeSerializer();
                serializer.DecodeEnvelope(fake.Sent[0], out string type, out byte[] payload);
                Assert.AreEqual("chat", type);
                Assert.AreEqual("二进制", serializer.Deserialize<ChatOutbound>(payload).Text);
            }
            finally { ctx.Dispose(); }
        });

        [UnityTest]
        public IEnumerator EnvelopeSerializer_Send_NoPayload_EncodesEmptyPayload() => UniTask.ToCoroutine(async () =>
        {
            var (fake, ws, ctx) = CreateBinaryWs();
            try
            {
                await ws.Connect("ws://fake/");
                await ws.Send("ping");

                new BinaryEnvelopeSerializer().DecodeEnvelope(fake.Sent[0], out string type, out byte[] payload);
                Assert.AreEqual("ping", type);
                Assert.AreEqual(0, payload.Length);
            }
            finally { ctx.Dispose(); }
        });

        [UnityTest]
        public IEnumerator EnvelopeSerializer_Push_DecodedAndDelivered() => UniTask.ToCoroutine(async () =>
        {
            var (fake, ws, ctx) = CreateBinaryWs();
            try
            {
                ws.RegisterPush<ChatPush>("chat");
                ChatPush? received = null;
                using var sub = ctx.RegisterEvent<ChatPush>(e => received = e);

                await ws.Connect("ws://fake/");
                await UniTask.DelayFrame(1);

                var serializer = new BinaryEnvelopeSerializer();
                fake.InjectMessage(serializer.EncodeEnvelope("chat",
                    serializer.Serialize(new ChatPush { Text = "hi", UserId = 9 })));
                await UniTask.DelayFrame(2);

                Assert.IsTrue(received.HasValue, "envelope 序列化器路径的推送应转成事件送达");
                Assert.AreEqual("hi", received.Value.Text);
                Assert.AreEqual(9, received.Value.UserId);
            }
            finally { ctx.Dispose(); }
        });

        [UnityTest]
        public IEnumerator EnvelopeSerializer_MalformedFrame_DroppedButLoopContinues() => UniTask.ToCoroutine(async () =>
        {
            var (fake, ws, ctx) = CreateBinaryWs();
            try
            {
                ws.RegisterPush<ChatPush>("chat");
                ChatPush? received = null;
                using var sub = ctx.RegisterEvent<ChatPush>(e => received = e);
                await ws.Connect("ws://fake/");
                await UniTask.DelayFrame(1);

                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("无法解析的 envelope"));
                fake.InjectMessage(new byte[] { 250 }); // type 长度声称 250、帧只有 1 字节 → DecodeEnvelope 抛
                await UniTask.DelayFrame(2);

                var serializer = new BinaryEnvelopeSerializer();
                fake.InjectMessage(serializer.EncodeEnvelope("chat",
                    serializer.Serialize(new ChatPush { Text = "ok", UserId = 1 })));
                await UniTask.DelayFrame(2);
                Assert.IsTrue(received.HasValue, "坏帧不应毒化接收循环");
                Assert.AreEqual("ok", received.Value.Text);
            }
            finally { ctx.Dispose(); }
        });

        [UnityTest]
        public IEnumerator Dispose_ThenConnect_ThrowsObjectDisposed() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeWebSocketProvider();
            var ws = new WebSocketUtility(fake);
            var builder = new ContainerBuilder();
            builder.RegisterOwned(ws, typeof(IWebSocketUtility));
            var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
            ctx.Dispose(); // 级联 Dispose ws

            try
            {
                await ws.Connect("ws://fake/");
                Assert.Fail("Dispose 后 Connect 应抛 ObjectDisposedException");
            }
            catch (ObjectDisposedException) { /* 预期 */ }
        });
    }
}
