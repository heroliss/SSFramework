using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Logging;
using R3;

namespace Game.Framework.Network
{
    /// <summary>
    /// <see cref="IWebSocketUtility"/> 的默认实现：连接状态机 + envelope 编解码 + 推送→事件注册表 +
    /// 后台接收循环（切主线程再扇出）+ 发送 FIFO。传输与格式委托给 <see cref="IWebSocketProvider"/> /
    /// <see cref="INetworkSerializer"/>（构造注入，默认 ClientWebSocket + JSON）。
    /// </summary>
    /// <remarks>
    /// <b>Context 回填</b>：实现 <see cref="IHasGameContext"/>，<c>RegisterOwnedUtility</c> 注册即注入时 <c>AttachTo</c>
    /// 反射回写 <see cref="_context"/>（照 GameFlow 姿势）——<see cref="Send{T}"/> 转事件需要它。<br/>
    /// <b>Connection Session</b>：每次成功 Connect 建立一个内部代际 owner，独占接收 token、发送 token 与 FIFO 队尾；
    /// 只有仍是 current 的 session 能发布终态。旧接收 continuation 迟到只结束自己，旧排队帧以 ConnectionError 收口，
    /// 不会触碰新连接。<br/>
    /// <b>接收循环线程模型</b>：后台 <c>ReceiveAsync</c> → 每条消息 <c>SwitchToMainThread</c> → 解析 envelope +
    /// 查注册表 + <c>SendEvent</c>（事件系统主线程独占的铁律）。坏消息 warning + 丢弃当条、不毒化循环；
    /// 只有 session token 已取消的 OCE 才静默，provider 自发 OCE 也是可观察的意外断线。<br/>
    /// <b>关闭顺序</b>：<see cref="Disconnect"/> 先 claim 当前 session、建立 teardown barrier、置 Disconnected 并停止本代发送；
    /// 等发送退场后发 Close 帧，最后才停接收。后续 Connect 只等永远成功的内部 barrier，不继承前一个调用者的 OCE；
    /// Connecting 期间调 Disconnect 则取消在途 Connect（其 await 收到 OCE），不发 ClosedEvent。<br/>
    /// <b>Dispose</b>：取消循环 + 关闭连接 + 释放 provider，随宿主 Context 整棵撤；此路径不发 ClosedEvent
    /// （整个 Context 在拆，订阅者也在拆）。
    /// </remarks>
    public sealed class WebSocketUtility : IWebSocketUtility, IHasGameContext, IDisposable
    {
        private static readonly TimeSpan UnexpectedCloseTimeout = TimeSpan.FromSeconds(1);
        private const string SendUnexpectedCancellationReason = "WebSocket 发送被传输层意外取消";
        private const string SendFailureReason = "WebSocket 发送异常结束";
        private const string ReceiveUnexpectedCancellationReason = "WebSocket 接收被传输层意外取消";
        private const string ReceiveFailureReason = "WebSocket 接收异常结束";

        // 默认（JSON）envelope wire 格式：payload 是「载荷的 JSON 文本」二次编码（ADR-0028 §4）。JsonUtility 需要公共字段。
        // 序列化器实现 IWebSocketEnvelopeSerializer 时不走此类型——envelope 编解码整体交给序列化器（payload 保持 byte[]）。
        [Serializable]
        private sealed class Envelope
        {
            public string type;
            public string payload;
        }

        /// <summary>
        /// 一次成功连接的内部 owner。接收取消、发送取消与 FIFO 队尾都归当前代际，避免旧连接的迟到 continuation
        /// 触碰新连接。Generation 只用于诊断；是否仍有发布权以对象引用相等为准。
        /// </summary>
        private sealed class ConnectionSession : IDisposable
        {
            private readonly CancellationTokenSource _receiveCts;
            private readonly CancellationTokenSource _sendCts;
            private int _disposed;
            private int _terminalClaimed;

            public readonly long Generation;
            public readonly CancellationToken ReceiveToken;
            public readonly CancellationToken SendToken;
            public bool IsClosing { get; private set; }
            public UniTask SendTail = UniTask.CompletedTask;

            public ConnectionSession(long generation, CancellationToken lifetimeToken)
            {
                Generation = generation;
                _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                _sendCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                ReceiveToken = _receiveCts.Token;
                SendToken = _sendCts.Token;
            }

            /// <summary>停止接受本代新发送，并取消已经排队/在途的发送；接收保留到 Close 帧尝试完成。</summary>
            public void BeginClosing()
            {
                if (IsClosing) return;
                IsClosing = true;
                CancelOwnerSafely(_sendCts, $"WebSocket 会话 #{Generation} 的发送 owner");
            }

            public void CancelAll()
            {
                CancelOwnerSafely(_sendCts, $"WebSocket 会话 #{Generation} 的发送 owner");
                CancelOwnerSafely(_receiveCts, $"WebSocket 会话 #{Generation} 的接收 owner");
            }

            public bool TryClaimTerminal()
                => Interlocked.CompareExchange(ref _terminalClaimed, 1, 0) == 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _sendCts.Dispose();
                _receiveCts.Dispose();
            }
        }

        /// <summary>一次在途 Connect 的本地 owner 与结果。Completion 只返回本次尝试提交的 session，不读全局 State。</summary>
        private sealed class ConnectAttempt : IDisposable
        {
            private readonly CancellationTokenSource _cts;
            private int _disconnectRequested;
            private int _disposed;

            public readonly CancellationToken Token;
            public readonly UniTaskCompletionSource<ConnectionSession> Completion = new();
            public bool IsDisconnectRequested => Volatile.Read(ref _disconnectRequested) != 0;

            public ConnectAttempt(CancellationToken callerToken, CancellationToken lifetimeToken)
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, lifetimeToken);
                Token = _cts.Token;
            }

            public void RequestDisconnect()
            {
                if (Interlocked.Exchange(ref _disconnectRequested, 1) != 0) return;
                CancelOwnerSafely(_cts, "WebSocket 连接 owner");
            }

            public void Complete(ConnectionSession session) => Completion.TrySetResult(session);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _cts.Dispose();
            }
        }

        private readonly IWebSocketProvider _provider;
        private readonly INetworkSerializer _serializer;
        private readonly IWebSocketEnvelopeSerializer _envelopeSerializer; // null = 走内置 JSON envelope 兼容路径
        private readonly Dictionary<string, Action<byte[]>> _pushHandlers = new();
        private readonly RP<NetworkConnectionState> _state = new(NetworkConnectionState.Disconnected);
        private readonly CancellationTokenSource _lifetimeCts = new();

        private GameContext _context; // RegisterOwnedUtility 注册即注入时由 AttachTo 回填
        private ConnectAttempt _connectAttempt;
        private ConnectionSession _activeSession;
        private UniTask _disconnectBarrier = UniTask.CompletedTask; // 只等待旧 Close/Send owner 清场；永远成功，不泄露其调用者取消
        private long _nextSessionGeneration;
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
                        Log.Write(
                            LogLevel.Warning,
                            $"推送 '{type}' 载荷无法反序列化为 {typeof(TEvent).Name}，已丢弃。",
                            category: nameof(WebSocketUtility),
                            exception: e);
                        return;
                    }
                }
                else if (evt == null) // 引用类型事件的空 payload：无默认实例可发
                {
                    Log.Warning($"推送 '{type}' 无载荷、而 {typeof(TEvent).Name} 是引用类型（无法取默认实例），已丢弃。", "WebSocketUtility");
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
                    "[WebSocketUtility] 尚未挂到宿主 Context——用 builder.RegisterOwnedUtility(new WebSocketUtility()) 注册（注册即注入自动回填），不要脱离容器直接使用。");
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("url 不能为空。", nameof(url));
            if (_state.Value != NetworkConnectionState.Disconnected)
                throw new InvalidOperationException($"[WebSocketUtility] 当前状态 {_state.Value}，不能重复 Connect——先 Disconnect。");

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                throw new ArgumentException(
                    $"WebSocket url 必须是绝对地址（ws:// 或 wss://）：'{url}'。", nameof(url));
            if (!string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"WebSocket url 只支持 ws:// 或 wss://，当前 scheme 为 '{uri.Scheme}'：'{url}'。", nameof(url));
            if (string.IsNullOrWhiteSpace(uri.Host))
                throw new ArgumentException(
                    $"WebSocket url 必须包含服务器 host（ws:// 或 wss://）：'{url}'。", nameof(url));
            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException(
                    $"WebSocket url 不支持 userinfo，请通过协议约定传递认证信息：'{url}'。", nameof(url));
            if (!string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException(
                    $"WebSocket url 不支持 fragment（#...）：'{url}'。", nameof(url));

            // Disconnect 已把公开 State 置为 Disconnected 时，底层 Close 可能仍在退场。只等内部成功 barrier，
            // 不 await 上一个调用者的公共 Disconnect task（否则它的 OCE 会错误传给无关的 Connect）。
            try
            {
                await WaitForDisconnectBarrier(ct);
            }
            catch
            {
                // caller/lifetime token 可能从 worker 取消 barrier waiter；公共主线程 API 的异常 continuation 仍回主线程。
                await UniTask.SwitchToMainThread();
                throw;
            }
            ThrowIfDisposed();
            if (_state.Value != NetworkConnectionState.Disconnected || _activeSession != null)
                throw new InvalidOperationException($"[WebSocketUtility] 当前状态 {_state.Value}，不能重复 Connect——先 Disconnect。旧连接仍在收尾时 Connect 会自动等待。");

            var attempt = new ConnectAttempt(ct, _lifetimeCts.Token);
            ConnectionSession committedSession = null;
            // owner 必须先于响应式状态发布：State 订阅者可能在 Connecting 回调里同步 Disconnect。
            _connectAttempt = attempt;
            try
            {
                try
                {
                    _state.Value = NetworkConnectionState.Connecting;
                    await _provider.ConnectAsync(uri, attempt.Token);
                    await UniTask.SwitchToMainThread();
                    // Provider 成功返回就是物理连接 ownership 的提交点。取消若与成功竞态，允许成功赢；
                    // 再做 post-check 会制造“provider 已发布 socket、utility 却不建 session”的无 owner 缝隙。
                }
                catch (OperationCanceledException e)
                {
                    await UniTask.SwitchToMainThread();
                    bool ownerCanceled = attempt.Token.IsCancellationRequested;
                    // 先按 identity 摘除旧 owner，再发布 Disconnected：订阅者同步发起的新 Connect 不会被旧 finally 清掉。
                    ClearConnectOwner(attempt);
                    if (!_disposed) _state.Value = NetworkConnectionState.Disconnected;

                    if (ownerCanceled)
                        throw; // caller / lifetime / Disconnect 取消：原样保留 OCE

                    throw new NetworkException(NetworkErrorKind.ConnectionError,
                        $"WebSocket 连接被传输层意外取消：{url}（{e.Message}）", inner: e);
                }
                catch (Exception e)
                {
                    await UniTask.SwitchToMainThread();
                    bool ownerCanceled = attempt.Token.IsCancellationRequested;
                    ClearConnectOwner(attempt);
                    if (!_disposed) _state.Value = NetworkConnectionState.Disconnected;
                    if (ownerCanceled)
                        throw new OperationCanceledException("WebSocket 建连随调用方或宿主生命周期取消。", e, attempt.Token);
                    throw new NetworkException(NetworkErrorKind.ConnectionError, $"WebSocket 连接失败：{url}（{e.Message}）", inner: e);
                }

                // provider 若违规忽略 Dispose 取消并迟到成功，也不能把已结束的宿主重新写成 Connected。
                if (_disposed)
                    throw new ObjectDisposedException(nameof(WebSocketUtility), "WebSocket 在连接完成前已随 Context 释放。");

                if (attempt.IsDisconnectRequested)
                {
                    // Disconnect 意图早于逻辑提交：即使物理成功赢得取消竞态，也不能短暂发布 Connected、允许 Send/Push。
                    // Abort 是 Provider 的可重连物理重置，不等 Close 握手，也不产生 ClosedEvent（本 session 从未公开成立）。
                    AbortProviderSafely("Connecting 期取消在物理成功后收尾");
                    ClearConnectOwner(attempt);
                    _state.Value = NetworkConnectionState.Disconnected;
                    throw new OperationCanceledException("WebSocket 在逻辑提交前被 Disconnect 取消。", attempt.Token);
                }

                var session = new ConnectionSession(++_nextSessionGeneration, _lifetimeCts.Token);
                committedSession = session; // completion 的本地 outcome；必须先于任何 State 同步重入写入
                _activeSession = session;
                _state.Value = NetworkConnectionState.Connected;
                ReceiveLoop(session).Forget(e => Log.Error(
                    $"WebSocket 接收会话 #{session.Generation} 越过统一终态边界抛出异常。",
                    e, nameof(WebSocketUtility)));
            }
            finally
            {
                ClearConnectOwner(attempt);
                // 所有成功、失败、Dispose 与 State 回调异常路径都必须放行本 attempt 的 waiter。
                attempt.Complete(committedSession);
                attempt.Dispose();
            }
        }

        public async UniTask Disconnect(CancellationToken ct = default)
        {
            if (_disposed || _state.Value == NetworkConnectionState.Disconnected) return; // 未连接 / 已在关闭 = no-op
            ct.ThrowIfCancellationRequested(); // 尚未提交断开意图：入口取消不改变连接

            if (_state.Value == NetworkConnectionState.Connecting)
            {
                ConnectAttempt attempt = _connectAttempt;
                if (attempt == null) return;

                // cleanup 不继承 caller ct：意图一经提交，即使 caller 随后取消，也要继续观察本 attempt 的本地 outcome。
                // 外层 Attach 只让调用方脱离等待；底层清理由此 task 持续拥有。
                UniTask cleanup = CancelConnectAttemptAndCommittedSession(attempt);
                try
                {
                    await cleanup.AttachExternalCancellation(ct);
                }
                finally
                {
                    await UniTask.SwitchToMainThread();
                }
                return;
            }

            ConnectionSession session = _activeSession;
            if (session == null || session.IsClosing || !session.TryClaimTerminal()) return;

            // barrier 必须先于 State 更新建立：State 订阅若同步发起 Connect，会等待本次 provider Close 退场，
            // 不会与旧 socket 的关闭握手交叠。公开状态立即变 Disconnected，同时 session 仍占位阻止重入误判。
            var teardownGate = new UniTaskCompletionSource();
            _disconnectBarrier = teardownGate.Task.Preserve();
            session.BeginClosing();
            _state.Value = NetworkConnectionState.Disconnected;

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);

                // SendToken 已取消：在途/排队发送会沿各自 task 以 ConnectionError 收口。等本代 FIFO 物理退场后再发 Close，
                // 避免 ClientWebSocket 的「单时刻只允许一个 send」约束与 CloseOutputAsync 冲突。
                await session.SendTail.AttachExternalCancellation(linked.Token);

                // 先发 Close 帧、后停接收——若先取消 ReceiveAsync，ClientWebSocket 会 abort 底层连接，
                // Close 帧根本发不出去，对端只能看到异常断开。「优雅关闭」对这个顺序敏感。
                await _provider.CloseAsync(linked.Token);
                await UniTask.SwitchToMainThread();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposed || session.ReceiveToken.IsCancellationRequested)
            {
                await UniTask.SwitchToMainThread();
                throw; // 调用者 / 宿主取消只终止优雅握手等待；逻辑断开与 session 清理由 finally 保证
            }
            catch (OperationCanceledException e)
            {
                await UniTask.SwitchToMainThread();
                // linked token 未取消时，OCE 来自 provider 自身：这是关闭握手失败，不是调用方取消。
                Log.Write(
                    LogLevel.Warning,
                    "关闭握手被传输层意外取消，连接已按断开处理。",
                    category: nameof(WebSocketUtility),
                    exception: e);
            }
            catch (Exception e) when (ct.IsCancellationRequested || _disposed || session.ReceiveToken.IsCancellationRequested)
            {
                await UniTask.SwitchToMainThread();
                // 某些 Adapter 在 token 取消 / Dispose 竞态里会抛 ODE、socket error 等非 OCE。
                // 分类以 owner 意图为准，不能把调用方或宿主取消伪装成 best-effort 传输失败。
                throw new OperationCanceledException(
                    "WebSocket 断开随调用方或宿主生命周期取消。",
                    e,
                    ct.IsCancellationRequested ? ct : session.ReceiveToken);
            }
            catch (Exception e)
            {
                await UniTask.SwitchToMainThread();
                // 关闭握手失败无关紧要（对端可能已走）——记一条不抛，Disconnect 的语义是「尽力优雅关」
                Log.Write(
                    LogLevel.Warning,
                    "关闭握手未完成，连接已按断开处理。",
                    category: nameof(WebSocketUtility),
                    exception: e);
            }
            finally
            {
                await UniTask.SwitchToMainThread();
                if (ReferenceEquals(_activeSession, session))
                    _activeSession = null;
                AbortProviderSafely("主动断开 session 收尾");
                session.CancelAll();
                session.Dispose();

                // ClosedEvent 先于 barrier 放行：业务从官方事件发起的重连会排在旧会话事件之后，不会看到
                // 「新连接已 Connected，随后却收到旧连接关闭事件」的时序倒挂。
                try
                {
                    if (!_disposed)
                        _context?.SendEvent(new WebSocketClosedEvent(byUser: true, reason: "用户主动断开"));
                }
                finally
                {
                    _disconnectBarrier = UniTask.CompletedTask;
                    teardownGate.TrySetResult();
                }
            }
        }

        public UniTask Send<T>(string type, T payload, CancellationToken ct = default) where T : class
        {
            ConnectionSession session = EnsureConnected();
            if (payload == null) throw new ArgumentNullException(nameof(payload), "无载荷消息用 Send(type) 重载。");
            return SendEnvelope(type, _serializer.Serialize(payload), session, ct);
        }

        public UniTask Send(string type, CancellationToken ct = default)
        {
            ConnectionSession session = EnsureConnected();
            return SendEnvelope(type, Array.Empty<byte>(), session, ct);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ConnectionSession session = _activeSession;
            _activeSession = null;
            session?.TryClaimTerminal();
            CancelOwnerSafely(_lifetimeCts, "WebSocket 工具生命周期 owner");
            session?.CancelAll();
            session?.Dispose();
            _lifetimeCts.Dispose();
            try
            {
                _provider.Dispose();
            }
            finally
            {
                // Provider 释放失败仍应交给 Context owner 记录，但不能截断响应式 State 自身的释放。
                _state.Dispose();
            }
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        private UniTask SendEnvelope(string type, byte[] payloadBytes, ConnectionSession session, CancellationToken ct)
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
            return EnqueueSend(frame, session, ct);
        }

        // FIFO 队尾属于连接 session 而非整个 utility：新连接不等待旧代队列，排队旧帧也绝不会写进新 socket。
        private async UniTask EnqueueSend(byte[] frame, ConnectionSession session, CancellationToken ct)
        {
            UniTask prev = session.SendTail;
            var gate = new UniTaskCompletionSource();
            session.SendTail = gate.Task;
            // linked CTS 在排队等待前创建：若等待期间宿主 Dispose，之后再取 _lifetimeCts.Token 会抛 ODE 而非约定的取消。
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token, session.SendToken);
            NetworkException transportFailure = null;
            string terminalReason = null;
            bool providerInvoked = false;
            UniTaskCompletionSource teardownGate = null;
            try
            {
                try
                {
                    await prev; // 前一条的哨兵必然完成（finally 保证），这里永不抛
                    linked.Token.ThrowIfCancellationRequested(); // 排队期间被取消 / 宿主释放：不再碰 socket
                    if (!ReferenceEquals(_activeSession, session) || session.IsClosing ||
                        _state.Value != NetworkConnectionState.Connected)
                    {
                        throw new NetworkException(NetworkErrorKind.ConnectionError,
                            $"WebSocket 发送所属的连接会话 #{session.Generation} 已结束，旧帧不会转发到新连接。");
                    }

                    providerInvoked = true;
                    await _provider.SendAsync(frame, _envelopeSerializer?.UseBinaryFrames ?? false, linked.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposed)
                {
                    throw; // 调用方意图 / 宿主释放：OCE 原样保留
                }
                catch (Exception e) when (ct.IsCancellationRequested || _disposed)
                {
                    // Adapter 可能在取消 / Dispose 后用 ODE、socket error 等形态退场；owner 意图优先于异常外形。
                    throw new OperationCanceledException(
                        "WebSocket 发送随调用方或宿主生命周期取消。",
                        e,
                        ct.IsCancellationRequested ? ct : linked.Token);
                }
                catch (OperationCanceledException e)
                {
                    // session 被 Disconnect 关闭，或 provider 在 token 未取消时自发 OCE：对这个 Send 都是连接失效，
                    // 不能把内部 owner 的取消伪装成调用方取消。排队期与物理发送期统一在这里收口。
                    transportFailure = new NetworkException(NetworkErrorKind.ConnectionError,
                        $"WebSocket 发送失败：连接会话 #{session.Generation} 已结束或传输被意外取消。", inner: e);
                    if (!session.SendToken.IsCancellationRequested)
                        terminalReason = SendUnexpectedCancellationReason;
                }
                catch (NetworkException e) when (providerInvoked)
                {
                    // Adapter 可以主动使用框架异常形态，但其 message 仍属于接缝外实现细节；
                    // Utility 重新建立稳定的公共错误正文，并把完整 Adapter 异常保留为 inner。
                    transportFailure = new NetworkException(
                        NetworkErrorKind.ConnectionError,
                        "WebSocket 发送失败：连接已断开或传输异常。",
                        inner: e);
                    terminalReason = SendFailureReason;
                }
                catch (NetworkException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // 发送中途 socket 断掉（EnsureConnected 只挡得住「调用时未连接」）：折叠为 ConnectionError，
                    // 不让 WebSocketException 之类传输层原始异常泄给业务；发送本身也必须终结 session，不能依赖接收恰好随后失败。
                    transportFailure = new NetworkException(
                        NetworkErrorKind.ConnectionError,
                        "WebSocket 发送失败：连接已断开或传输异常。",
                        inner: e);
                    terminalReason = SendFailureReason;
                }

                if (transportFailure != null && terminalReason != null)
                {
                    // 必须先 claim + 取消本代队列，再释放本帧 gate。UniTask continuation 可能同步内联；顺序反过来时，
                    // 下一帧会在本帧调用 Complete 前仍看到 Connected，并错误地再次碰 provider。
                    await UniTask.SwitchToMainThread();
                    teardownGate = TryBeginUnexpectedSession(session);
                }
            }
            finally
            {
                // Provider 可在 worker 完成；FIFO continuation 与公共 API 完成统一回主线程。
                await UniTask.SwitchToMainThread();
                gate.TrySetResult();
            }

            if (transportFailure == null) return;
            if (teardownGate != null)
                await FinishUnexpectedSession(session, terminalReason, teardownGate);
            throw transportFailure;
        }

        private async UniTask ReceiveLoop(ConnectionSession session)
        {
            string lostReason = null;
            Exception terminalError = null;
            bool infrastructureFailure = false;

            try
            {
                while (!session.ReceiveToken.IsCancellationRequested)
                {
                    byte[] message;
                    try
                    {
                        message = await _provider.ReceiveAsync(session.ReceiveToken);
                    }
                    catch (OperationCanceledException) when (session.ReceiveToken.IsCancellationRequested)
                    {
                        return; // Disconnect / Dispose 的 owner 取消：静默退出，终态由 owner 负责
                    }
                    catch (OperationCanceledException e)
                    {
                        terminalError = e;
                        lostReason = ReceiveUnexpectedCancellationReason;
                        break;
                    }
                    catch (Exception e)
                    {
                        terminalError = e;
                        lostReason = ReceiveFailureReason;
                        break;
                    }

                    await UniTask.SwitchToMainThread(); // 之后一切触碰框架（RP / SendEvent）都在主线程
                    if (session.ReceiveToken.IsCancellationRequested ||
                        !ReferenceEquals(_activeSession, session) || session.IsClosing)
                        return;

                    if (message == null)
                    {
                        lostReason = "服务器关闭了连接";
                        break;
                    }

                    Dispatch(message);
                }
            }
            catch (OperationCanceledException) when (session.ReceiveToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                terminalError = e;
                infrastructureFailure = true;
                lostReason = "WebSocket 接收循环内部异常";
            }

            if (lostReason == null || session.ReceiveToken.IsCancellationRequested) return;

            await UniTask.SwitchToMainThread();
            if (_disposed || session.ReceiveToken.IsCancellationRequested ||
                !ReferenceEquals(_activeSession, session) || session.IsClosing)
                return;

            if (terminalError != null)
            {
                if (infrastructureFailure)
                    Log.Error($"{lostReason}（会话 #{session.Generation}）。", terminalError, nameof(WebSocketUtility));
                else
                    Log.Write(
                        LogLevel.Warning,
                        $"{lostReason}（会话 #{session.Generation}）。",
                        category: nameof(WebSocketUtility),
                        exception: terminalError);
            }

            await CompleteUnexpectedSession(session, lostReason);
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
                Log.Write(
                    LogLevel.Warning,
                    "收到无法解析的 envelope，已丢弃。",
                    category: nameof(WebSocketUtility),
                    exception: e);
                return;
            }
            if (string.IsNullOrEmpty(type))
            {
                Log.Warning("收到缺 type 的消息，已丢弃。", "WebSocketUtility");
                return;
            }
            if (!_pushHandlers.TryGetValue(type, out var handler))
            {
                WarnUnknownType(type);
                return;
            }
            handler(payload);
        }

        // 意外断开（对端关闭 / 收发异常）——只允许当前 session claim 一次终态；统一尝试关闭物理连接后再放行重连。
        private async UniTask CompleteUnexpectedSession(ConnectionSession session, string reason)
        {
            await UniTask.SwitchToMainThread();
            UniTaskCompletionSource teardownGate = TryBeginUnexpectedSession(session);
            if (teardownGate == null) return;
            await FinishUnexpectedSession(session, reason, teardownGate);
        }

        /// <summary>主线程：抢占终态并在任何 FIFO continuation 被唤醒前撤销本代发送资格。</summary>
        private UniTaskCompletionSource TryBeginUnexpectedSession(ConnectionSession session)
        {
            if (_disposed || !ReferenceEquals(_activeSession, session) || !session.TryClaimTerminal()) return null;

            // barrier 必须先于 State 发布。State / ClosedEvent 的同步回调都可以表达重连，但会等本次 Close/Send owner 清场。
            var teardownGate = new UniTaskCompletionSource();
            _disconnectBarrier = teardownGate.Task.Preserve();
            session.BeginClosing();
            _state.Value = NetworkConnectionState.Disconnected;
            return teardownGate;
        }

        private async UniTask FinishUnexpectedSession(
            ConnectionSession session,
            string reason,
            UniTaskCompletionSource teardownGate)
        {
            try
            {
                // Begin 已取消所有排队帧；当前失败帧释放 gate 后，FIFO 会依次以 ConnectionError 收口，最后再碰 Close。
                await session.SendTail;
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(session.ReceiveToken);
                UniTask closeTask = _provider.CloseAsync(closeCts.Token).Preserve();
                int winner = await UniTask.WhenAny(closeTask, UniTask.Delay(UnexpectedCloseTimeout));
                if (winner != 0)
                {
                    // 不用 CTS.CancelAfter：timer 线程触发时，Adapter 的坏取消回调异常会越过本 owner 的 try/catch。
                    // 显式竞速后由框架 owner 安全取消，再观察 Close task 到终态。
                    CancelOwnerSafely(closeCts, $"WebSocket 会话 #{session.Generation} 的意外 Close 超时 owner");
                }
                await closeTask;
            }
            catch (OperationCanceledException) when (_disposed || session.ReceiveToken.IsCancellationRequested)
            {
                // 宿主拆除已经接管清理，不再对外发布终态。
            }
            catch (Exception e)
            {
                await UniTask.SwitchToMainThread();
                Log.Write(
                    LogLevel.Warning,
                    "意外断线后的关闭收尾未完成，已继续释放本次会话。",
                    category: nameof(WebSocketUtility),
                    exception: e);
            }
            finally
            {
                await UniTask.SwitchToMainThread();
                if (ReferenceEquals(_activeSession, session))
                    _activeSession = null;
                AbortProviderSafely("意外断线 session 收尾");
                session.CancelAll();
                session.Dispose();

                try
                {
                    if (!_disposed)
                        _context?.SendEvent(new WebSocketClosedEvent(byUser: false, reason: reason));
                }
                finally
                {
                    _disconnectBarrier = UniTask.CompletedTask;
                    teardownGate.TrySetResult();
                }
            }
        }

        private ConnectionSession EnsureConnected()
        {
            ThrowIfDisposed();
            ConnectionSession session = _activeSession;
            if (_state.Value != NetworkConnectionState.Connected || session == null || session.IsClosing)
                throw new NetworkException(NetworkErrorKind.ConnectionError,
                    $"WebSocket 未连接（当前 {_state.Value}），无法发送——先 await Connect(url)。");
            return session;
        }

        private async UniTask WaitForDisconnectBarrier(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            linked.Token.ThrowIfCancellationRequested();
            UniTask barrier = _disconnectBarrier;
            await barrier.AttachExternalCancellation(linked.Token);
        }

        private async UniTask CancelConnectAttemptAndCommittedSession(ConnectAttempt attempt)
        {
            attempt.RequestDisconnect();
            ConnectionSession committedSession = await attempt.Completion.Task;
            await UniTask.SwitchToMainThread();

            // outcome 属于本 attempt，不能用全局 State 推断；旧失败回调中新建的 session 与这里无关。
            if (_disposed || committedSession == null || !ReferenceEquals(_activeSession, committedSession)) return;
            try
            {
                await Disconnect(CancellationToken.None);
            }
            catch (OperationCanceledException) when (_disposed)
            {
                // Context Dispose 已接管清理。
            }
            catch (Exception e)
            {
                // caller 可能已经取消并脱离，后台 cleanup 不能留下未观察异常；Disconnect 的传输失败本就 best-effort。
                Log.Error("Connecting 期已提交的断开意图在清理成功竞态 session 时异常。", e, nameof(WebSocketUtility));
            }
        }

        private void ClearConnectOwner(ConnectAttempt attempt)
        {
            if (ReferenceEquals(_connectAttempt, attempt)) _connectAttempt = null;
        }

        private static void CancelOwnerSafely(CancellationTokenSource cts, string owner)
        {
            if (cts == null) return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* 迟到 owner 的幂等收尾 */ }
            catch (Exception e)
            {
                // CancellationToken 回调异常会从 Cancel 聚合抛出；取消已成立，不能让业务回调破坏框架 owner 清理。
                Log.Write(
                    LogLevel.Warning,
                    $"{owner} 的取消回调抛出异常，已隔离并继续清理。",
                    category: nameof(WebSocketUtility),
                    exception: e);
            }
        }

        private void AbortProviderSafely(string reason)
        {
            try { _provider.Abort(); }
            catch (Exception e)
            {
                Log.Write(
                    LogLevel.Warning,
                    $"{reason}时 Provider Abort 失败，已继续逻辑清理。",
                    category: nameof(WebSocketUtility),
                    exception: e);
            }
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
                Log.Warning($"收到未注册的推送 type '{type}'，已丢弃——用 RegisterPush<TEvent>(\"{type}\") 注册后才会转成事件。", "WebSocketUtility");
#endif
        }
    }
}
