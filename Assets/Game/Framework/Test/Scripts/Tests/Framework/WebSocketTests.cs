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

        /// <summary>可编程 WS 传输桩：测试注入「收到的消息 / 远端关闭」、捕获发出的帧、可控 Connect 失败与发送阻塞。</summary>
        private sealed class FakeWebSocketProvider : IWebSocketProvider
        {
            public bool FailConnect;
            public readonly List<byte[]> Sent = new();
            public UniTaskCompletionSource SendGate; // 非 null 时 SendAsync 先等它——用于测发送 FIFO 保序

            private readonly Queue<UniTaskCompletionSource<byte[]>> _pendingReceives = new();
            private readonly Queue<byte[]> _buffered = new(); // null 元素 = 远端关闭

            public UniTask ConnectAsync(Uri uri, CancellationToken ct)
            {
                if (FailConnect) throw new Exception("fake connect failure");
                return UniTask.CompletedTask;
            }

            public async UniTask SendAsync(byte[] payload, CancellationToken ct)
            {
                if (SendGate != null) await SendGate.Task;
                Sent.Add(payload);
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
                if (_pendingReceives.Count > 0) _pendingReceives.Dequeue().TrySetResult(msg);
                else _buffered.Enqueue(msg);
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
