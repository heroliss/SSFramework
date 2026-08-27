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
using R3;
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

        /// <summary>
        /// 可编程 WS 传输桩：测试注入「收到的消息 / 远端关闭 / 接收异常」、捕获发出的帧，
        /// 并可分别阻塞 Connect / Send / Close / 某一次 Receive，用来复现跨连接代际的迟到完成。
        /// </summary>
        private sealed class FakeWebSocketProvider : IWebSocketProvider
        {
            public bool FailConnect;
            public bool FailSend; // 模拟「调用时已连接、写 socket 才失败」（对端刚断）
            public int FailSendCount;
            public bool FailSendWithNetworkException;
            public bool CancelConnectWithoutToken; // provider 自发 OCE：用于区分传输失败与 owner/caller 取消
            public bool CancelCloseWithoutToken;
            public bool ThrowDisposedWhenConnectTokenCanceled;
            public bool CompleteConnectOnThreadPool;
            public bool CompleteCloseOnThreadPool;
            public bool IgnoreCancellationForConnectGate;
            public bool ThrowOnConnectCancellation;
            public bool ThrowOnSendCancellation;
            public bool ThrowOnReceiveCancellation;
            public bool ThrowOnCloseCancellation;
            public int DisposeCount;
            public int AbortCount;
            public readonly List<byte[]> Sent = new();
            public readonly List<bool> SentBinary = new(); // 与 Sent 一一对应：每帧的 binary 标记
            public UniTaskCompletionSource SendGate;    // 非 null 时 SendAsync 先等它——用于测发送 FIFO 保序
            public UniTaskCompletionSource ConnectGate; // 非 null 时 ConnectAsync 先等它——用于测 Connecting 中途取消
            public UniTaskCompletionSource CloseGate;   // 非 null 时 CloseAsync 先等它——用于测关闭握手取消 / 重连竞态
            public UniTaskCompletionSource<byte[]> ReceiveGate; // 非 null 时仅下一次 ReceiveAsync 等它，可精确控制旧代际迟到终态
            public bool IgnoreCancellationForReceiveGate;
            public int ReceiveCancellationCount;

            private readonly Queue<UniTaskCompletionSource<byte[]>> _pendingReceives = new();
            private readonly Queue<byte[]> _buffered = new(); // null 元素 = 远端关闭

            public async UniTask ConnectAsync(Uri uri, CancellationToken ct)
            {
                if (ThrowOnConnectCancellation)
                    ct.Register(() => throw new InvalidOperationException("fake connect cancellation callback"));
                if (FailConnect) throw new Exception("fake connect failure");
                if (CancelConnectWithoutToken) throw new OperationCanceledException("fake provider canceled connect");
                if (ThrowDisposedWhenConnectTokenCanceled && ct.IsCancellationRequested)
                    throw new ObjectDisposedException("fake physical socket");
                if (ConnectGate != null)
                {
                    if (IgnoreCancellationForConnectGate) await ConnectGate.Task;
                    else await ConnectGate.Task.AttachExternalCancellation(ct);
                }
                if (CompleteConnectOnThreadPool) await UniTask.SwitchToThreadPool();
            }

            public async UniTask SendAsync(byte[] payload, bool binary, CancellationToken ct)
            {
                if (ThrowOnSendCancellation)
                    ct.Register(() => throw new InvalidOperationException("fake send cancellation callback"));
                UniTaskCompletionSource gate = SendGate; // 一次物理发送绑定调用入口时的 gate，模拟 provider 绑定底层 socket
                if (gate != null) await gate.Task.AttachExternalCancellation(ct);
                if (FailSend || FailSendCount > 0)
                {
                    if (FailSendCount > 0) FailSendCount--;
                    throw new Exception("fake send failure");
                }
                if (FailSendWithNetworkException)
                    throw new NetworkException(NetworkErrorKind.ConnectionError, "fake provider network failure");
                Sent.Add(payload);
                SentBinary.Add(binary);
            }

            public UniTask<byte[]> ReceiveAsync(CancellationToken ct)
            {
                if (ThrowOnReceiveCancellation)
                    ct.Register(() => throw new InvalidOperationException("fake receive cancellation callback"));
                if (_buffered.Count > 0) return UniTask.FromResult(_buffered.Dequeue());

                var tcs = ReceiveGate ?? new UniTaskCompletionSource<byte[]>();
                bool isExplicitGate = ReceiveGate != null;
                ReceiveGate = null;
                _pendingReceives.Enqueue(tcs);
                if (!isExplicitGate || !IgnoreCancellationForReceiveGate)
                {
                    ct.Register(() =>
                    {
                        ReceiveCancellationCount++;
                        tcs.TrySetCanceled(ct);
                    });
                }
                IgnoreCancellationForReceiveGate = false;
                return tcs.Task;
            }

            public async UniTask CloseAsync(CancellationToken ct)
            {
                if (ThrowOnCloseCancellation)
                    ct.Register(() => throw new InvalidOperationException("fake close cancellation callback"));
                if (CancelCloseWithoutToken) throw new OperationCanceledException("fake provider canceled close");
                if (CloseGate != null) await CloseGate.Task.AttachExternalCancellation(ct);
                if (CompleteCloseOnThreadPool) await UniTask.SwitchToThreadPool();
            }
            public void Dispose() => DisposeCount++;
            public void Abort() => AbortCount++;

            public void InjectMessage(byte[] msg) => Deliver(msg);
            public void InjectRemoteClose() => Deliver(null);
            public void InjectReceiveFailure(Exception error)
            {
                while (_pendingReceives.Count > 0)
                    if (_pendingReceives.Dequeue().TrySetException(error))
                        return;
                throw new InvalidOperationException("当前没有等待中的 ReceiveAsync，无法注入接收异常。");
            }

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
        public IEnumerator ProviderWorkerCompletion_StateAndClosedEventStillReturnToMainThread() => UniTask.ToCoroutine(async () =>
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int connectedThread = -1;
            int disconnectedThread = -1;
            int closedEventThread = -1;
            _fake.CompleteConnectOnThreadPool = true;
            _fake.CompleteCloseOnThreadPool = true;

            using var stateSub = _ws.State.Subscribe(state =>
            {
                if (state == NetworkConnectionState.Connected) connectedThread = Thread.CurrentThread.ManagedThreadId;
                if (state == NetworkConnectionState.Disconnected && connectedThread != -1)
                    disconnectedThread = Thread.CurrentThread.ManagedThreadId;
            });
            using var eventSub = _ctx.RegisterEvent<WebSocketClosedEvent>(_ =>
                closedEventThread = Thread.CurrentThread.ManagedThreadId);

            await _ws.Connect("ws://fake/");
            await _ws.Disconnect();

            Assert.AreEqual(mainThread, connectedThread, "Provider 在 worker 完成也不能让响应式 State 越过主线程边界");
            Assert.AreEqual(mainThread, disconnectedThread);
            Assert.AreEqual(mainThread, closedEventThread, "Framework Event 必须始终在主线程发布");
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                "主线程调用的公共网络 API 完成后也应回到主线程");
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
        public IEnumerator Connect_ProviderOceWithoutOwnerCancellation_IsConnectionError() => UniTask.ToCoroutine(async () =>
        {
            _fake.CancelConnectWithoutToken = true;
            await AssertConnectionError(_ws.Connect("ws://fake/"),
                "provider 自发 OCE 是传输失败，不能伪装成调用方取消");
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
        });

        [UnityTest]
        public IEnumerator Connect_ConnectingStateHandlerCanSynchronouslyCancelAttempt() => UniTask.ToCoroutine(async () =>
        {
            _fake.ConnectGate = new UniTaskCompletionSource();
            bool requested = false;
            using var stateSub = _ws.State.Subscribe(state =>
            {
                if (state != NetworkConnectionState.Connecting || requested) return;
                requested = true;
                _ws.Disconnect().Forget();
            });

            try
            {
                await _ws.Connect("ws://fake/");
                Assert.Fail("Connecting 发布前必须先安装 Connect owner，使同步重入 Disconnect 能取消本次连接");
            }
            catch (OperationCanceledException) { /* 预期 */ }

            Assert.IsTrue(requested);
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
        });

        [UnityTest]
        public IEnumerator Connect_FailureStateHandlerCanStartCancelableNextAttempt() => UniTask.ToCoroutine(async () =>
        {
            _fake.FailConnect = true;
            bool firstAttemptStarted = false;
            bool secondAttemptStarted = false;
            UniTask secondConnect = default;
            using var stateSub = _ws.State.Subscribe(state =>
            {
                if (state == NetworkConnectionState.Connecting)
                {
                    firstAttemptStarted = true;
                    return;
                }
                if (state != NetworkConnectionState.Disconnected || !firstAttemptStarted || secondAttemptStarted) return;

                secondAttemptStarted = true;
                _fake.FailConnect = false;
                _fake.ConnectGate = new UniTaskCompletionSource();
                secondConnect = _ws.Connect("ws://fake/retry");
            });

            await AssertConnectionError(_ws.Connect("ws://fake/"), "第一次连接应按传输失败收口");
            Assert.IsTrue(secondAttemptStarted, "Disconnected 订阅者应已同步启动第二次连接");

            await _ws.Disconnect();
            _fake.ConnectGate.TrySetResult(); // 错误实现若丢了新 owner，会在这里迟到成功，令下面断言失败
            try
            {
                await secondConnect;
                Assert.Fail("旧 Connect 的 finally 不得清掉订阅回调中新建的 owner");
            }
            catch (OperationCanceledException) { /* 预期 */ }

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
        });

        [UnityTest]
        public IEnumerator Connect_ContextDisposedFromConnectingHandler_StaysLifecycleCancellation() => UniTask.ToCoroutine(async () =>
        {
            _fake.ThrowDisposedWhenConnectTokenCanceled = true;
            using var stateSub = _ws.State.Subscribe(state =>
            {
                if (state == NetworkConnectionState.Connecting)
                    _ctx.Dispose();
            });

            try
            {
                await _ws.Connect("ws://fake/");
                Assert.Fail("Context 拆除导致的 provider ODE 必须按已取消的 owner 收口，不能包装成真实网络失败");
            }
            catch (OperationCanceledException) { /* 预期 */ }
        });

        [UnityTest]
        public IEnumerator Connect_DisposeAfterPhysicalSuccessStillReleasesConnectingDisconnectWaiter() => UniTask.ToCoroutine(async () =>
        {
            _fake.ConnectGate = new UniTaskCompletionSource();
            _fake.IgnoreCancellationForConnectGate = true;
            UniTask connecting = _ws.Connect("ws://fake/");
            UniTask disconnecting = _ws.Disconnect();

            _ctx.Dispose();
            _fake.ConnectGate.TrySetResult(); // provider 违规忽略取消并迟到“成功”；Utility 必须拒绝提交但完成 attempt outcome

            await disconnecting;
            try
            {
                await connecting;
                Assert.Fail("Dispose 后迟到成功不能建立 session");
            }
            catch (ObjectDisposedException) { /* 预期 */ }
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
        public IEnumerator Send_MidFlightFailure_EndsSessionAndWrapsAsConnectionError() => UniTask.ToCoroutine(async () =>
        {
            // EnsureConnected 只挡「调用时未连接」；写 socket 中途失败（对端刚断）也必须折叠为
            // NetworkException(ConnectionError)，并形成可观察的断线终态，不能留下假 Connected。
            int unexpectedCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (!e.ByUser) unexpectedCloseCount++;
            });
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
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.AreEqual(1, unexpectedCloseCount,
                "接收仍挂起时，发送失败也必须发布一次意外关闭事件，让业务有机会重连");
        });

        [UnityTest]
        public IEnumerator Send_ProviderNetworkException_StillEndsCurrentSession() => UniTask.ToCoroutine(async () =>
        {
            int unexpectedCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (!e.ByUser) unexpectedCloseCount++;
            });
            await _ws.Connect("ws://fake/");
            _fake.FailSendWithNetworkException = true;

            await AssertConnectionError(_ws.Send("network-error"),
                "Adapter 直接抛 NetworkException 也来自物理 Send，不能绕过 session 终态");

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.AreEqual(1, unexpectedCloseCount);
        });

        [UnityTest]
        public IEnumerator Send_FirstPhysicalFailureCancelsQueuedFrameBeforeWakingItsContinuation() => UniTask.ToCoroutine(async () =>
        {
            int unexpectedCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (!e.ByUser) unexpectedCloseCount++;
            });
            await _ws.Connect("ws://fake/");

            var firstGate = new UniTaskCompletionSource();
            _fake.SendGate = firstGate;
            _fake.FailSendCount = 1;
            UniTask first = _ws.Send("first-fails");
            _fake.SendGate = null;
            UniTask queued = _ws.Send("queued-must-not-send");

            firstGate.TrySetResult();
            await AssertConnectionError(first, "首帧物理失败应以 ConnectionError 收口");
            await AssertConnectionError(queued, "后帧被唤醒前 session 就应失效");

            Assert.AreEqual(0, _fake.Sent.Count,
                "UniTask continuation 可能同步内联；必须先 claim/cancel session，再释放失败帧的 FIFO gate");
            Assert.AreEqual(1, unexpectedCloseCount);
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
        });

        [UnityTest]
        public IEnumerator Disconnect_WhileConnecting_WaitsOwnerAndAllowsImmediateReconnect() => UniTask.ToCoroutine(async () =>
        {
            _fake.ConnectGate = new UniTaskCompletionSource(); // 让 Connect 挂在半路
            WebSocketClosedEvent? closed = null;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e => closed = e);

            UniTask connecting = _ws.Connect("ws://fake/");
            Assert.AreEqual(NetworkConnectionState.Connecting, _ws.State.CurrentValue);

            await _ws.Disconnect(); // 返回时旧 Connect owner 已清场、State 已落终态，不只是发出取消请求
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);

            _fake.ConnectGate = null;
            await _ws.Connect("ws://fake/retry");
            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue,
                "await Disconnect 后应能立即重连，不必再由业务等待旧 connecting task");

            try
            {
                await connecting;
                Assert.Fail("被 Disconnect 取消的 Connect 应抛 OCE");
            }
            catch (OperationCanceledException) { /* 预期 */ }

            Assert.IsFalse(closed.HasValue, "从未连接成功，不应发 ClosedEvent");
        });

        [UnityTest]
        public IEnumerator Disconnect_ConnectingCallerCancellation_StillAbortsUnpublishedSuccess() => UniTask.ToCoroutine(async () =>
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            _fake.ConnectGate = new UniTaskCompletionSource();
            _fake.IgnoreCancellationForConnectGate = true; // 让物理成功赢过 Disconnect 对 attempt token 的取消
            int connectedCount = 0;
            int userCloseCount = 0;
            using var stateSub = _ws.State.Subscribe(state =>
            {
                if (state == NetworkConnectionState.Connected) connectedCount++;
            });
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (e.ByUser) userCloseCount++;
            });

            UniTask connecting = _ws.Connect("ws://fake/");
            using var cts = new CancellationTokenSource();
            UniTask disconnecting = _ws.Disconnect(cts.Token);
            CancelOnThreadPool(cts).Forget();
            int cancellationThread = -1;
            try
            {
                await disconnecting;
                Assert.Fail("caller 可取消自己的等待");
            }
            catch (OperationCanceledException)
            {
                cancellationThread = Thread.CurrentThread.ManagedThreadId;
            }

            _fake.ConnectGate.TrySetResult();
            try
            {
                await connecting;
                Assert.Fail("Disconnect intent 先成立时，物理 success-win 也不得公开提交 session");
            }
            catch (OperationCanceledException) { /* 预期：物理连接已 Abort，但从未发布 Connected */ }

            Assert.AreEqual(mainThread, cancellationThread,
                "caller 从 worker 取消只脱离等待；公共 Disconnect 的 OCE continuation 仍须回主线程");
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.AreEqual(0, connectedCount, "Disconnect intent 早于逻辑提交时，不得短暂暴露可 Send/Push 的 Connected 窗口");
            Assert.AreEqual(0, userCloseCount, "session 从未公开成立，不发布 ClosedEvent");
            Assert.AreEqual(1, _fake.AbortCount, "物理 success-win 必须立即摘除，Provider 仍保持可重连");
        });

        [UnityTest]
        public IEnumerator Disconnect_OldConnectingOutcomeCannotCloseSynchronousRetrySession() => UniTask.ToCoroutine(async () =>
        {
            _fake.ConnectGate = new UniTaskCompletionSource();
            bool sawConnecting = false;
            bool retryStarted = false;
            UniTask retry = default;
            using var stateSub = _ws.State.Subscribe(state =>
            {
                if (state == NetworkConnectionState.Connecting)
                {
                    sawConnecting = true;
                    return;
                }
                if (state != NetworkConnectionState.Disconnected || !sawConnecting || retryStarted) return;

                retryStarted = true;
                _fake.ConnectGate = null;
                retry = _ws.Connect("ws://fake/retry");
            });

            UniTask first = _ws.Connect("ws://fake/");
            await _ws.Disconnect();
            await retry;

            try
            {
                await first;
                Assert.Fail("旧 attempt 应被取消");
            }
            catch (OperationCanceledException) { /* 预期 */ }

            Assert.IsTrue(retryStarted);
            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue,
                "旧 Disconnect 必须读取旧 attempt 的本地 outcome(null)，不能看到全局 Connected 就关闭同步重试的新 session");
        });

        [UnityTest]
        public IEnumerator Disconnect_ThrowingCancellationCallbacks_DoNotBreakOwnerCleanup() => UniTask.ToCoroutine(async () =>
        {
            _fake.ThrowOnSendCancellation = true;
            _fake.ThrowOnReceiveCancellation = true;
            _fake.SendGate = new UniTaskCompletionSource();
            int userCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (e.ByUser) userCloseCount++;
            });

            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);
            UniTask sending = _ws.Send("blocked");
            await _ws.Disconnect();

            await AssertConnectionError(sending,
                "Adapter 的 token 回调即使抛异常，发送 owner 仍应以 ConnectionError 清场");
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.AreEqual(1, userCloseCount);
        });

        [UnityTest]
        public IEnumerator Disconnect_ConnectingCancellationCallbackFailureStillCompletesAttempt() => UniTask.ToCoroutine(async () =>
        {
            _fake.ThrowOnConnectCancellation = true;
            _fake.ConnectGate = new UniTaskCompletionSource();
            UniTask connecting = _ws.Connect("ws://fake/");

            await _ws.Disconnect();
            try
            {
                await connecting;
                Assert.Fail("取消后的 Connect 应抛 OCE");
            }
            catch (OperationCanceledException) { /* 预期 */ }

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
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
        public IEnumerator Receive_OceWithoutSessionCancellation_TransitionsToDisconnected() => UniTask.ToCoroutine(async () =>
        {
            int unexpectedCloseCount = 0;
            WebSocketClosedEvent? closed = null;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                closed = e;
                if (!e.ByUser) unexpectedCloseCount++;
            });

            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);
            _fake.InjectReceiveFailure(new OperationCanceledException("provider canceled receive without session cancellation"));
            await UniTask.DelayFrame(2);

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue,
                "只有会话 token 真正取消时 OCE 才能静默退出；provider 自发 OCE 必须形成可观察的断线终态");
            Assert.AreEqual(1, unexpectedCloseCount);
            Assert.IsTrue(closed.HasValue);
            Assert.IsFalse(closed.Value.ByUser);
            StringAssert.Contains("取消", closed.Value.Reason);
        });

        [UnityTest]
        public IEnumerator Disconnect_CallerCancellation_PreservesOceAndStillCleansUp() => UniTask.ToCoroutine(async () =>
        {
            _fake.CloseGate = new UniTaskCompletionSource();
            int userCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (e.ByUser) userCloseCount++;
            });

            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);
            using var cts = new CancellationTokenSource();
            UniTask disconnecting = _ws.Disconnect(cts.Token);
            cts.Cancel();

            try
            {
                await disconnecting;
                Assert.Fail("调用者取消关闭握手时 Disconnect 必须保留 OCE，不能被 best-effort 日志吞掉");
            }
            catch (OperationCanceledException) { /* 预期 */ }

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.AreEqual(1, userCloseCount, "取消的是优雅握手等待，不应撤销已经提交的主动断开意图");
            Assert.GreaterOrEqual(_fake.ReceiveCancellationCount, 1, "即使关闭握手被取消，接收会话也必须完成清理");

            _fake.CloseGate = null;
            await _ws.Connect("ws://fake/");
            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue, "清理完成后同一 utility 应可立即重连");
        });

        [UnityTest]
        public IEnumerator Disconnect_ProviderOceWithoutCallerCancellation_IsBestEffortFailure() => UniTask.ToCoroutine(async () =>
        {
            int userCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (e.ByUser) userCloseCount++;
            });
            await _ws.Connect("ws://fake/");
            _fake.CancelCloseWithoutToken = true;

            await _ws.Disconnect();

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.AreEqual(1, userCloseCount,
                "provider 自发 OCE 只是关闭握手失败；已提交的主动断开仍应完成且不向调用方伪造取消");
        });

        [UnityTest]
        public IEnumerator Disconnect_PreCanceled_DoesNotCommitDisconnect() => UniTask.ToCoroutine(async () =>
        {
            int closeCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(_ => closeCount++);
            await _ws.Connect("ws://fake/");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try
            {
                await _ws.Disconnect(cts.Token);
                Assert.Fail("入口已经取消时 Disconnect 不应提交任何断开副作用");
            }
            catch (OperationCanceledException) { /* 预期 */ }

            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue);
            Assert.AreEqual(0, closeCount);
            await _ws.Disconnect();
        });

        [UnityTest]
        public IEnumerator Connect_WaitingForClose_CancellationOnlyDetachesThatWaiter() => UniTask.ToCoroutine(async () =>
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            _fake.CloseGate = new UniTaskCompletionSource();
            await _ws.Connect("ws://fake/");
            UniTask disconnecting = _ws.Disconnect();

            using var cts = new CancellationTokenSource();
            UniTask waitingConnect = _ws.Connect("ws://fake/", cts.Token);
            CancelOnThreadPool(cts).Forget();
            int cancellationThread = -1;
            try
            {
                await waitingConnect;
                Assert.Fail("等待旧 Close 的 Connect 应保留自己的 caller OCE");
            }
            catch (OperationCanceledException)
            {
                cancellationThread = Thread.CurrentThread.ManagedThreadId;
            }

            Assert.AreEqual(mainThread, cancellationThread,
                "等待 teardown barrier 的 Connect 即使从 worker 被取消，也要在主线程完成 OCE");
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            _fake.CloseGate.TrySetResult();
            await disconnecting; // waiter 取消不得反向取消旧 session 的 teardown owner

            _fake.CloseGate = null;
            await _ws.Connect("ws://fake/");
            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue);
        });

        [UnityTest]
        public IEnumerator Disconnect_OverlappedReconnect_OldReceiveTerminalCannotCloseNewSession() => UniTask.ToCoroutine(async () =>
        {
            var oldReceiveGate = new UniTaskCompletionSource<byte[]>();
            _fake.ReceiveGate = oldReceiveGate;
            _fake.IgnoreCancellationForReceiveGate = true; // 模拟不守 ct、会迟到完成的第三方 Adapter
            _fake.CloseGate = new UniTaskCompletionSource();

            int userCloseCount = 0;
            int unexpectedCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (e.ByUser) userCloseCount++;
                else unexpectedCloseCount++;
            });

            await _ws.Connect("ws://fake/");
            UniTask disconnecting = _ws.Disconnect(); // 阻塞在旧连接 CloseAsync
            UniTask reconnecting = _ws.Connect("ws://fake/"); // 等旧发送/Close owner 清场；违规 Adapter 的旧 Receive 可迟到但已失去发布权

            _fake.CloseGate.TrySetResult();
            await disconnecting;
            await reconnecting;
            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue);

            oldReceiveGate.TrySetException(new Exception("late terminal from old physical connection"));
            await UniTask.DelayFrame(2);

            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue,
                "旧连接迟到的物理终态不得覆盖新连接的逻辑状态");
            Assert.AreEqual(1, userCloseCount);
            Assert.AreEqual(0, unexpectedCloseCount, "旧代际迟到异常不得冒充新连接的意外断线事件");
        });

        [UnityTest]
        public IEnumerator Disconnect_CancelsOldSendQueue_NewSessionDoesNotWaitOrSendOldFrames() => UniTask.ToCoroutine(async () =>
        {
            await _ws.Connect("ws://fake/");
            var oldSendGate = new UniTaskCompletionSource();
            _fake.SendGate = oldSendGate;

            UniTask oldActive = _ws.Send("old-active");
            UniTask oldQueued = _ws.Send("old-queued");

            await _ws.Disconnect();
            await _ws.Connect("ws://fake/");
            _fake.SendGate = null;
            await _ws.Send("new-session");

            Assert.AreEqual(1, _fake.Sent.Count, "新连接的 FIFO 必须独立于旧连接仍未物理完成的发送");
            StringAssert.Contains("new-session", Encoding.UTF8.GetString(_fake.Sent[0]));

            oldSendGate.TrySetResult(); // 即使违规 Adapter 的旧物理操作迟到，也不能把排队旧帧写进新 socket
            await UniTask.DelayFrame(1);
            await AssertConnectionError(oldActive, "主动断开应让旧代际在途发送以 ConnectionError 收口");
            await AssertConnectionError(oldQueued, "排队旧帧必须在调用 provider 前被连接代际校验拦住");
            Assert.AreEqual(1, _fake.Sent.Count, "旧代际迟到完成后仍不得新增任何帧");
        });

        [UnityTest]
        public IEnumerator Disconnect_RacingRemoteClose_FiresExactlyOneUserEvent() => UniTask.ToCoroutine(async () =>
        {
            _fake.CloseGate = new UniTaskCompletionSource();
            int userCloseCount = 0;
            int unexpectedCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (e.ByUser) userCloseCount++;
                else unexpectedCloseCount++;
            });

            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);
            UniTask disconnecting = _ws.Disconnect();
            _fake.InjectRemoteClose(); // CloseAsync 仍挂起时，接收循环也得到远端 Close
            await UniTask.DelayFrame(1);
            _fake.CloseGate.TrySetResult();
            await disconnecting;

            Assert.AreEqual(1, userCloseCount);
            Assert.AreEqual(0, unexpectedCloseCount, "主动断开已 claim 终态后，远端 Close 不得再发布第二个意外事件");
        });

        [UnityTest]
        public IEnumerator Disconnect_ClosedEventHandlerCanRequestReconnectAfterTeardownBarrier() => UniTask.ToCoroutine(async () =>
        {
            UniTask reconnecting = default;
            bool reconnectRequested = false;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (!e.ByUser) return;
                reconnectRequested = true;
                reconnecting = _ws.Connect("ws://fake/");
            });

            await _ws.Connect("ws://fake/");
            await _ws.Disconnect();
            Assert.IsTrue(reconnectRequested);
            await reconnecting;

            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue,
                "ClosedEvent 在 owner 清理后发布；回调内表达的 Connect 会等 barrier 放行并安全建立新 session");
        });

        [UnityTest]
        public IEnumerator Receive_Exception_FiresExactlyOneUnexpectedClose() => UniTask.ToCoroutine(async () =>
        {
            int unexpectedCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (!e.ByUser) unexpectedCloseCount++;
            });

            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);
            _fake.InjectReceiveFailure(new Exception("transport broke"));
            await UniTask.DelayFrame(2);

            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.AreEqual(1, unexpectedCloseCount);
            await _ws.Disconnect(); // 已断开 no-op，不得补发用户事件
            Assert.AreEqual(1, unexpectedCloseCount);
        });

        [UnityTest]
        public IEnumerator Receive_Exception_CloseHandshakeTimeoutStillPublishesAndAllowsReconnect() => UniTask.ToCoroutine(async () =>
        {
            _fake.CloseGate = new UniTaskCompletionSource();
            _fake.ThrowOnCloseCancellation = true;
            int unexpectedCloseCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(e =>
            {
                if (!e.ByUser) unexpectedCloseCount++;
            });

            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);
            _fake.InjectReceiveFailure(new Exception("transport broke before close handshake"));

            await UniTask.Delay(TimeSpan.FromMilliseconds(1200));
            Assert.AreEqual(NetworkConnectionState.Disconnected, _ws.State.CurrentValue);
            Assert.AreEqual(1, unexpectedCloseCount,
                "意外终态没有 caller token；best-effort Close 必须内部限时，不能永久扣住事件与 barrier");

            _fake.CloseGate = null;
            await _ws.Connect("ws://fake/reconnect");
            Assert.AreEqual(NetworkConnectionState.Connected, _ws.State.CurrentValue);
        });

        [UnityTest]
        public IEnumerator Dispose_Connected_CancelsReceiveWithoutClosedEvent() => UniTask.ToCoroutine(async () =>
        {
            int closeCount = 0;
            using var sub = _ctx.RegisterEvent<WebSocketClosedEvent>(_ => closeCount++);
            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);

            _ctx.Dispose();
            await UniTask.DelayFrame(1);

            Assert.AreEqual(0, closeCount, "Context 正在整棵拆除，Dispose 不应再向其订阅者发布关闭事件");
            Assert.GreaterOrEqual(_fake.ReceiveCancellationCount, 1);
        });

        [UnityTest]
        public IEnumerator Dispose_ThrowingCancellationCallback_StillReleasesProvider() => UniTask.ToCoroutine(async () =>
        {
            _fake.ThrowOnReceiveCancellation = true;
            await _ws.Connect("ws://fake/");
            await UniTask.DelayFrame(1);

            Assert.DoesNotThrow(() => _ctx.Dispose(),
                "CancellationToken 回调异常必须被 owner 隔离，不能截断 Context 级联释放");
            Assert.AreEqual(1, _fake.DisposeCount, "即使取消回调抛异常，Provider 仍必须被释放");
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

        private static async UniTask AssertConnectionError(UniTask task, string message)
        {
            try
            {
                await task;
                Assert.Fail(message);
            }
            catch (NetworkException e)
            {
                Assert.AreEqual(NetworkErrorKind.ConnectionError, e.Kind, message);
            }
        }

        private static async UniTask CancelOnThreadPool(CancellationTokenSource cts)
        {
            await UniTask.SwitchToThreadPool();
            cts.Cancel();
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

        [UnityTest]
        public IEnumerator ClientProvider_DisposeThenConnect_CannotResurrectPhysicalSocket() => UniTask.ToCoroutine(async () =>
        {
            var provider = new ClientWebSocketProvider();
            provider.Dispose();
            try
            {
                await provider.ConnectAsync(new Uri("ws://127.0.0.1:1/"), CancellationToken.None);
                Assert.Fail("默认 provider 释放后不得再次建立或发布物理 socket");
            }
            catch (ObjectDisposedException) { /* 预期：无需真的访问网络 */ }
        });
    }
}
