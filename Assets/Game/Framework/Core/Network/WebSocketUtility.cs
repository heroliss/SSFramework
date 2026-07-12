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
    /// <b>关闭顺序</b>：<see cref="Disconnect"/> 先置 Disconnected（关闭事件去重：循环里的意外断开处理见状态
    /// 已变便不重复发 <see cref="WebSocketClosedEvent"/>），再发 Close 帧，最后才停循环——若先取消循环，
    /// 挂起的 ReceiveAsync 被取消会直接中止底层连接，Close 帧发不出去。Connecting 期间调 Disconnect
    /// 则取消在途 Connect（其 await 收到 OCE），不发 ClosedEvent。<br/>
    /// <b>Dispose</b>：取消循环 + 关闭连接 + 释放 provider，随宿主 Context 整棵撤；此路径不发 ClosedEvent
    /// （整个 Context 在拆，订阅者也在拆）。
    /// </remarks>
    public sealed class WebSocketUtility : IWebSocketUtility, IHasGameContext, IDisposable
    {
        // 默认（JSON）envelope wire 格式：payload 是「载荷的 JSON 文本」二次编码（ADR-0028 §4）。JsonUtility 需要公共字段。
        // 序列化器实现 IWebSocketEnvelopeSerializer 时不走此类型——envelope 编解码整体交给序列化器（payload 保持 byte[]）。
        [Serializable]
        private sealed class Envelope
        {
            public string type;
            public string payload;
        }

        private readonly IWebSocketProvider _provider;
        private readonly INetworkSerializer _serializer;
        private readonly IWebSocketEnvelopeSerializer _envelopeSerializer; // null = 走内置 JSON envelope 兼容路径
        private readonly Dictionary<string, Action<byte[]>> _pushHandlers = new();
        private readonly RP<NetworkConnectionState> _state = new(NetworkConnectionState.Disconnected);
        private readonly CancellationTokenSource _lifetimeCts = new();

        private GameContext _context; // RegisterOwned 注册即注入时由 AttachTo 回填
        private CancellationTokenSource _connectCts; // 在途 Connect 专用（让 Connecting 期的 Disconnect 能取消它）；Connect 的 finally 负责回收
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
            _envelopeSerializer = _serializer as IWebSocketEnvelopeSerializer; // 二进制格式（Protobuf 等）额外实现该接口即接管 envelope
        }

        IGameContext IHasGameContext.Context => _context;

        public ReadOnlyReactiveProperty<NetworkConnectionState> State => _state;

        public void RegisterPush<TEvent>(string type) where TEvent : IEvent
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("推送 type 不能为空。", nameof(type));
            if (_pushHandlers.ContainsKey(type))
                throw new InvalidOperationException($"[WebSocketUtility] 推送 type '{type}' 已注册过——一个 type 只能映射一个事件类型。");

            // 闭包捕获 TEvent：收到该 type 时把 payload 反序列化为 TEvent 再发事件。
            // 空 payload（无载荷推送）：struct 事件取 default(TEvent)（零值即合法）；引用类型（class 消息，如 Protobuf）
            // 无法凭空造默认实例——丢弃告警（约束已从 struct 放宽到 IEvent 以支持二进制序列化器的 class 消息）。
            _pushHandlers[type] = payloadBytes =>
            {
                TEvent evt = default;
                if (payloadBytes != null && payloadBytes.Length > 0)
                {
                    try
                    {
                        evt = _serializer.Deserialize<TEvent>(payloadBytes);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[WebSocketUtility] 推送 '{type}' 载荷无法反序列化为 {typeof(TEvent).Name}，已丢弃（{e.GetType().Name}: {e.Message}）。");
                        return;
                    }
                }
                else if (evt == null) // 引用类型事件的空 payload：无默认实例可发
                {
                    Debug.LogWarning($"[WebSocketUtility] 推送 '{type}' 无载荷、而 {typeof(TEvent).Name} 是引用类型（无法取默认实例），已丢弃。");
                    return;
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
            var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            _connectCts = connectCts; // 存进字段：Connecting 期间的 Disconnect 靠它取消在途连接
            try
            {
                await _provider.ConnectAsync(uri, connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!_disposed) _state.Value = NetworkConnectionState.Disconnected; // 宿主已 Dispose 时 _state 已释放，不能再写
                throw; // 外部取消 / 宿主释放 / Disconnect 取消：原样抛，不包装
            }
            catch (Exception e)
            {
                if (!_disposed) _state.Value = NetworkConnectionState.Disconnected;
                throw new NetworkException(NetworkErrorKind.ConnectionError, $"WebSocket 连接失败：{url}（{e.Message}）", inner: e);
            }
            finally
            {
                _connectCts = null;
                connectCts.Dispose();
            }

            _state.Value = NetworkConnectionState.Connected;
            _loopCts?.Dispose(); // 上一条连接意外断开时循环自行退出、CTS 留到此刻回收——不回收会随重连次数累积对 _lifetimeCts 的注册
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            ReceiveLoop(_loopCts.Token).Forget();
        }

        public async UniTask Disconnect(CancellationToken ct = default)
        {
            if (_disposed || _state.Value == NetworkConnectionState.Disconnected) return; // 未连接 = no-op

            if (_state.Value == NetworkConnectionState.Connecting)
            {
                // 取消在途 Connect（其 await 收到 OCE、状态由 Connect 的 catch 回滚）；从未连接成功，不发 ClosedEvent。
                _connectCts?.Cancel();
                return;
            }

            // 先置 Disconnected：接收循环随后无论因收到 Close ack 还是被取消退出，都不再重复发 ClosedEvent（去重）。
            _state.Value = NetworkConnectionState.Disconnected;

            try
            {
                // 先发 Close 帧、后停循环——若先取消循环，挂起的 ReceiveAsync 被取消会直接中止（abort）底层连接，
                // Close 帧根本发不出去，对端只能看到异常断开。「优雅关闭」对这个顺序敏感。
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
                await _provider.CloseAsync(linked.Token);
            }
            catch (Exception e)
            {
                // 关闭握手失败无关紧要（对端可能已走）——记一条不抛，Disconnect 的语义是「尽力优雅关」
                Debug.LogWarning($"[WebSocketUtility] 关闭握手未完成（{e.GetType().Name}: {e.Message}），连接已按断开处理。");
            }

            // Close 帧已发出（或已尽力），不等对端 ack：取消并回收循环 CTS。
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;

            _context?.SendEvent(new WebSocketClosedEvent(byUser: true, reason: "用户主动断开"));
        }

        public UniTask Send<T>(string type, T payload, CancellationToken ct = default) where T : class
        {
            EnsureConnected();
            if (payload == null) throw new ArgumentNullException(nameof(payload), "无载荷消息用 Send(type) 重载。");
            return SendEnvelope(type, _serializer.Serialize(payload), ct);
        }

        public UniTask Send(string type, CancellationToken ct = default)
        {
            EnsureConnected();
            return SendEnvelope(type, Array.Empty<byte>(), ct);
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

        private UniTask SendEnvelope(string type, byte[] payloadBytes, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("type 不能为空。", nameof(type));
            // envelope 序列化器接管时 payload 保持 byte[]；兼容路径按既有 JSON wire 格式把 payload 转为文本二次编码
            // （对 JSON 字节无损；二进制格式不实现 envelope 接口就走不到正确编码，所以接口 remarks 标了"必须实现"）。
            byte[] frame = _envelopeSerializer != null
                ? _envelopeSerializer.EncodeEnvelope(type, payloadBytes)
                : _serializer.Serialize(new Envelope
                {
                    type = type,
                    payload = payloadBytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(payloadBytes),
                });
            return EnqueueSend(frame, ct);
        }

        // 发送 FIFO 尾链（照 StorageUtility）：单 socket 不允许并发写，逐个发；哨兵 finally 必然完成，异常只传各自调用方。
        private async UniTask EnqueueSend(byte[] frame, CancellationToken ct)
        {
            UniTask prev = _sendTail;
            var gate = new UniTaskCompletionSource();
            _sendTail = gate.Task;
            // linked CTS 在排队等待前创建：若等待期间宿主 Dispose，之后再取 _lifetimeCts.Token 会抛 ODE 而非约定的取消。
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            try
            {
                await prev; // 前一条的哨兵必然完成（finally 保证），这里永不抛
                linked.Token.ThrowIfCancellationRequested(); // 排队期间被取消 / 宿主释放：不再碰 socket
                try
                {
                    await _provider.SendAsync(frame, _envelopeSerializer?.UseBinaryFrames ?? false, linked.Token);
                }
                catch (OperationCanceledException) { throw; } // 取消原样抛（调用方意图 / 宿主释放）
                catch (Exception e)
                {
                    // 发送中途 socket 断掉（EnsureConnected 只挡得住「调用时未连接」）：折叠为 ConnectionError，
                    // 不让 WebSocketException 之类传输层原始异常泄给业务；连接失效由接收循环兜底发 ClosedEvent。
                    throw new NetworkException(NetworkErrorKind.ConnectionError, $"WebSocket 发送失败：{e.Message}", inner: e);
                }
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
            string type;
            byte[] payload;
            try
            {
                if (_envelopeSerializer != null)
                {
                    _envelopeSerializer.DecodeEnvelope(message, out type, out payload);
                }
                else
                {
                    var env = _serializer.Deserialize<Envelope>(message);
                    type = env?.type;
                    payload = string.IsNullOrEmpty(env?.payload) ? null : Encoding.UTF8.GetBytes(env.payload);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WebSocketUtility] 收到无法解析的 envelope，已丢弃（{e.GetType().Name}: {e.Message}）。");
                return;
            }
            if (string.IsNullOrEmpty(type))
            {
                Debug.LogWarning("[WebSocketUtility] 收到缺 type 的消息，已丢弃。");
                return;
            }
            if (!_pushHandlers.TryGetValue(type, out var handler))
            {
                WarnUnknownType(type);
                return;
            }
            handler(payload);
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
