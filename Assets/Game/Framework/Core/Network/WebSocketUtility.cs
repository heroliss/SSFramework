using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Event;
using Game.Framework.Internal;
using R3;
using UnityEngine;

namespace Game.Framework.Network
{
    /// <summary>
    /// <see cref="IWebSocketUtility"/> 的默认实现：连接状态机 + envelope 编解码 + 推送→事件注册表 +
    /// 后台接收循环（切主线程再扇出）+ 发送 FIFO。传输与格式委托给 <see cref="IWebSocketProvider"/> /
    /// <see cref="INetworkSerializer"/>（构造注入，默认 ClientWebSocket + JSON）。
    /// </summary>
    /// <remarks>
    /// <b>Context 回填</b>：实现 <see cref="IHasGameContext"/>，<c>RegisterOwned</c> 注册即注入时 <c>AttachTo</c>
    /// 反射回写 <see cref="_context"/>（照 GameFlow 姿势）——<see cref="Send{T}"/> 转事件需要它。<br/>
    /// <b>接收循环线程模型</b>：后台 <c>ReceiveAsync</c> → 每条消息 <c>SwitchToMainThread</c> → 解析 envelope +
    /// 查注册表 + <c>SendEvent</c>（事件系统主线程独占的铁律）。坏消息 warning + 丢弃当条、不毒化循环。<br/>
    /// <b>关闭事件去重</b>：<see cref="Disconnect"/> 先把状态置 Disconnected 再取消循环，循环里的意外断开处理
    /// 见状态已是 Disconnected 便不重复发 <see cref="WebSocketClosedEvent"/>。<br/>
    /// <b>Dispose</b>：取消循环 + 关闭连接 + 释放 provider，随宿主 Context 整棵撤；此路径不发 ClosedEvent
    /// （整个 Context 在拆，订阅者也在拆）。
    /// </remarks>
    public sealed class WebSocketUtility : IWebSocketUtility, IHasGameContext, IDisposable
    {
        // envelope wire 格式：payload 是「载荷的 JSON 文本」二次编码（ADR-0028 §4）。JsonUtility 需要公共字段。
        [Serializable]
        private sealed class Envelope
        {
            public string type;
            public string payload;
        }

        private readonly IWebSocketProvider _provider;
        private readonly INetworkSerializer _serializer;
        private readonly Dictionary<string, Action<string>> _pushHandlers = new();
        private readonly RP<NetworkConnectionState> _state = new(NetworkConnectionState.Disconnected);
        private readonly CancellationTokenSource _lifetimeCts = new();

        private GameContext _context; // RegisterOwned 注册即注入时由 AttachTo 回填
        private CancellationTokenSource _loopCts;
        private UniTask _sendTail = UniTask.CompletedTask; // 发送 FIFO 队尾（主线程独占，无锁）
        private bool _disposed;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private readonly HashSet<string> _warnedUnknownTypes = new();
#endif

        /// <param name="provider">WS 传输；null = 默认 <see cref="ClientWebSocketProvider"/>。</param>
        /// <param name="serializer">序列化格式；null = 默认 <see cref="JsonUtilityNetworkSerializer"/>。</param>
        public WebSocketUtility(IWebSocketProvider provider = null, INetworkSerializer serializer = null)
        {
            _provider = provider ?? new ClientWebSocketProvider();
            _serializer = serializer ?? new JsonUtilityNetworkSerializer();
        }

        IGameContext IHasGameContext.Context => _context;

        public ReadOnlyReactiveProperty<NetworkConnectionState> State => _state;

        public void RegisterPush<TEvent>(string type) where TEvent : struct, IEvent
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("推送 type 不能为空。", nameof(type));
            if (_pushHandlers.ContainsKey(type))
                throw new InvalidOperationException($"[WebSocketUtility] 推送 type '{type}' 已注册过——一个 type 只能映射一个事件类型。");

            // 闭包捕获 TEvent：收到该 type 时把 payload 反序列化为 TEvent 再发事件。空 payload → default(TEvent)（无载荷推送）。
            _pushHandlers[type] = payloadJson =>
            {
                TEvent evt = default;
                if (!string.IsNullOrEmpty(payloadJson))
                {
                    try
                    {
                        evt = _serializer.Deserialize<TEvent>(Encoding.UTF8.GetBytes(payloadJson));
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[WebSocketUtility] 推送 '{type}' 载荷无法反序列化为 {typeof(TEvent).Name}，已丢弃（{e.GetType().Name}: {e.Message}）。");
                        return;
                    }
                }
                _context?.SendEvent(evt);
            };
        }

        public async UniTask Connect(string url, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (_context == null)
                throw new InvalidOperationException(
                    "[WebSocketUtility] 尚未挂到宿主 Context——用 builder.RegisterOwned(new WebSocketUtility(), typeof(IWebSocketUtility)) 注册（注册即注入自动回填），不要脱离容器直接使用。");
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("url 不能为空。", nameof(url));
            if (_state.Value != NetworkConnectionState.Disconnected)
                throw new InvalidOperationException($"[WebSocketUtility] 当前状态 {_state.Value}，不能重复 Connect——先 Disconnect。");

            Uri uri;
            try { uri = new Uri(url); }
            catch (Exception e) { throw new ArgumentException($"url '{url}' 格式非法：{e.Message}", nameof(url)); }

            _state.Value = NetworkConnectionState.Connecting;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
                await _provider.ConnectAsync(uri, linked.Token);
            }
            catch (OperationCanceledException)
            {
                _state.Value = NetworkConnectionState.Disconnected;
                throw; // 外部取消 / 宿主释放：原样抛，不包装
            }
            catch (Exception e)
            {
                _state.Value = NetworkConnectionState.Disconnected;
                throw new NetworkException(NetworkErrorKind.ConnectionError, $"WebSocket 连接失败：{url}（{e.Message}）", inner: e);
            }

            _state.Value = NetworkConnectionState.Connected;
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            ReceiveLoop(_loopCts.Token).Forget();
        }

        public async UniTask Disconnect(CancellationToken ct = default)
        {
            if (_disposed || _state.Value == NetworkConnectionState.Disconnected) return; // 未连接 = no-op

            // 先置 Disconnected + 取消循环：循环里的意外断开处理会因状态已变而不再发 ClosedEvent（去重）。
            _state.Value = NetworkConnectionState.Disconnected;
            _loopCts?.Cancel();

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
                await _provider.CloseAsync(linked.Token);
            }
            catch (Exception e)
            {
                // 关闭握手失败无关紧要（对端可能已走）——记一条不抛，Disconnect 的语义是「尽力优雅关」
                Debug.LogWarning($"[WebSocketUtility] 关闭握手未完成（{e.GetType().Name}: {e.Message}），连接已按断开处理。");
            }

            _context?.SendEvent(new WebSocketClosedEvent(byUser: true, reason: "用户主动断开"));
        }

        public UniTask Send<T>(string type, T payload, CancellationToken ct = default) where T : class
        {
            EnsureConnected();
            if (payload == null) throw new ArgumentNullException(nameof(payload), "无载荷消息用 Send(type) 重载。");
            byte[] payloadBytes = _serializer.Serialize(payload);
            return SendEnvelope(type, Encoding.UTF8.GetString(payloadBytes), ct);
        }

        public UniTask Send(string type, CancellationToken ct = default)
        {
            EnsureConnected();
            return SendEnvelope(type, string.Empty, ct);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetimeCts.Cancel();
            _loopCts?.Cancel();
            _lifetimeCts.Dispose();
            _loopCts?.Dispose();
            _provider.Dispose();
            _state.Dispose();
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        private UniTask SendEnvelope(string type, string payloadJson, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("type 不能为空。", nameof(type));
            byte[] frame = _serializer.Serialize(new Envelope { type = type, payload = payloadJson });
            return EnqueueSend(frame, ct);
        }

        // 发送 FIFO 尾链（照 StorageUtility）：单 socket 不允许并发写，逐个发；哨兵 finally 必然完成，异常只传各自调用方。
        private async UniTask EnqueueSend(byte[] frame, CancellationToken ct)
        {
            UniTask prev = _sendTail;
            var gate = new UniTaskCompletionSource();
            _sendTail = gate.Task;
            await prev;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
                await _provider.SendAsync(frame, linked.Token);
            }
            finally { gate.TrySetResult(); }
        }

        private async UniTaskVoid ReceiveLoop(CancellationToken loopCt)
        {
            while (!loopCt.IsCancellationRequested)
            {
                byte[] message = null;
                string lostReason = null;
                try
                {
                    message = await _provider.ReceiveAsync(loopCt);
                    if (message == null) lostReason = "服务器关闭了连接"; // 对端正常关闭
                }
                catch (OperationCanceledException)
                {
                    return; // Disconnect / Dispose 取消：静默退出，关闭事件由 Disconnect 负责发
                }
                catch (Exception e)
                {
                    lostReason = e.Message; // 异常断开
                }

                await UniTask.SwitchToMainThread(); // 之后一切触碰框架（RP / SendEvent）都在主线程
                if (loopCt.IsCancellationRequested) return;

                if (lostReason != null)
                {
                    OnConnectionLost(lostReason);
                    return;
                }
                Dispatch(message);
            }
        }

        // 主线程：解析 envelope → 查注册表 → 交给对应闭包（内部再反序列化 payload 并 SendEvent）。
        private void Dispatch(byte[] message)
        {
            Envelope env;
            try { env = _serializer.Deserialize<Envelope>(message); }
            catch (Exception e)
            {
                Debug.LogWarning($"[WebSocketUtility] 收到无法解析的 envelope，已丢弃（{e.GetType().Name}: {e.Message}）。");
                return;
            }
            if (env == null || string.IsNullOrEmpty(env.type))
            {
                Debug.LogWarning("[WebSocketUtility] 收到缺 type 的消息，已丢弃。");
                return;
            }
            if (!_pushHandlers.TryGetValue(env.type, out var handler))
            {
                WarnUnknownType(env.type);
                return;
            }
            handler(env.payload);
        }

        // 意外断开（对端关闭 / 收发异常）——主线程调用。Disconnect 已把状态置 Disconnected 时不重复发事件。
        private void OnConnectionLost(string reason)
        {
            if (_state.Value == NetworkConnectionState.Disconnected) return;
            _state.Value = NetworkConnectionState.Disconnected;
            _context?.SendEvent(new WebSocketClosedEvent(byUser: false, reason: reason));
        }

        private void EnsureConnected()
        {
            ThrowIfDisposed();
            if (_state.Value != NetworkConnectionState.Connected)
                throw new NetworkException(NetworkErrorKind.ConnectionError,
                    $"WebSocket 未连接（当前 {_state.Value}），无法发送——先 await Connect(url)。");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WebSocketUtility), "WebSocket 工具已随 Context 释放——检查是否持有了过期引用。");
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void WarnUnknownType(string type)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_warnedUnknownTypes.Add(type)) // 同 type 只警告一次，避免高频推送刷屏
                Debug.LogWarning($"[WebSocketUtility] 收到未注册的推送 type '{type}'，已丢弃——用 RegisterPush<TEvent>(\"{type}\") 注册后才会转成事件。");
#endif
        }
    }
}
