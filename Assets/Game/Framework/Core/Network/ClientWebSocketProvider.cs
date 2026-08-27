using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Network
{
    /// <summary>
    /// 默认 WebSocket 传输：System.Net.WebSockets.ClientWebSocket 包装。除 WebGL 外全平台可用
    /// （WebGL 无 ClientWebSocket，见 ADR-0028 §9；需要时写 JS-bridge 第二 provider）。
    /// </summary>
    /// <remarks>
    /// <b>直连、不走系统代理</b>（<c>Options.Proxy = null</c>）：游戏直连自己的服务器是常态；且实测拦截式系统代理
    /// 会挡掉 ClientWebSocket 的 localhost 连接（ADR-0028 §8）。需要经代理的少数场景自写 provider。<br/>
    /// <b>可重连</b>：每次 <see cref="ConnectAsync"/> new 一个全新 ClientWebSocket（ClientWebSocket 一旦关闭不可复用）。<br/>
    /// <b>接收聚合</b>：<see cref="ReceiveAsync"/> 循环收帧直到 EndOfMessage 拼成整条消息；收到 Close 帧返回 null。
    /// </remarks>
    public sealed class ClientWebSocketProvider : IWebSocketProvider
    {
        private const int ReceiveBufferSize = 4096;

        private readonly object _lifecycleGate = new();
        private ClientWebSocket _ws;
        private ClientWebSocket _connecting;
        private bool _disposed;

        public async UniTask ConnectAsync(Uri uri, CancellationToken ct)
        {
            var next = new ClientWebSocket();
            next.Options.Proxy = null; // 直连（ADR-0028 §8）——系统代理会挡 localhost，且游戏本就直连服务器

            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    next.Dispose();
                    throw new ObjectDisposedException(nameof(ClientWebSocketProvider));
                }
                if (_connecting != null)
                {
                    next.Dispose();
                    throw new InvalidOperationException("ClientWebSocketProvider 不支持并发 Connect；由 WebSocketUtility 串行化连接生命周期。");
                }
                _connecting = next;
            }

            try
            {
                await next.ConnectAsync(uri, ct);
                // 底层实现可能在取消与握手完成的竞态中返回成功；取消已经成立时不能再发布无人拥有的 socket。
                ct.ThrowIfCancellationRequested();

                ClientWebSocket previous;
                lock (_lifecycleGate)
                {
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(ClientWebSocketProvider));

                    _connecting = null;
                    previous = _ws;
                    _ws = next;
                }
                previous?.Dispose();
            }
            catch
            {
                lock (_lifecycleGate)
                {
                    if (ReferenceEquals(_connecting, next))
                        _connecting = null;
                }
                next.Dispose();
                throw;
            }
        }

        public async UniTask SendAsync(byte[] payload, bool binary, CancellationToken ct)
        {
            ClientWebSocket ws = _ws ?? throw new InvalidOperationException("WebSocket 尚未连接。");
            await ws.SendAsync(new ArraySegment<byte>(payload),
                binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        public async UniTask<byte[]> ReceiveAsync(CancellationToken ct)
        {
            // 必须固定到方法入口时的物理 socket：分片循环期间即便下一代已建立，也不能跨 socket 拼接消息或回 Close ack。
            ClientWebSocket ws = _ws ?? throw new InvalidOperationException("WebSocket 尚未连接。");
            using var ms = new MemoryStream();
            var buffer = new byte[ReceiveBufferSize];
            while (true)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // RFC6455：收到对端 Close 帧要回一个 Close ack 才算完成关闭握手（best-effort——
                    // 对端可能已直接断线、或这是我方 Close 的 ack（CloseSent 状态回帧会抛），失败按已关闭处理）。
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* 已关闭 / 状态不允许 */ }
                    return null; // 对端发起关闭握手 = 正常关闭
                }
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    break;
            }
            return ms.ToArray();
        }

        public async UniTask CloseAsync(CancellationToken ct)
        {
            // CloseOutputAsync 而非 CloseAsync：后者发完 Close 帧还要等收对端 ack，与 utility 挂起中的
            // ReceiveAsync 冲突（同一 socket 两个并发接收）；前者只发帧即返回，ack 由挂起的接收收到（返回 null）。
            // 仅在能发帧的状态发（Open / 已收对端 Close 待回应）；其余状态发帧会抛，静默跳过。
            ClientWebSocket ws = _ws;
            if (ws != null && (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived))
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
        }

        public void Abort()
        {
            ClientWebSocket active;
            lock (_lifecycleGate)
            {
                active = _ws;
                _ws = null;
            }
            if (active == null) return;
            try { active.Abort(); }
            finally { active.Dispose(); }
        }

        public void Dispose()
        {
            ClientWebSocket active;
            ClientWebSocket connecting;
            lock (_lifecycleGate)
            {
                if (_disposed) return;
                _disposed = true;
                active = _ws;
                connecting = _connecting;
                _ws = null;
                _connecting = null;
            }

            // 连接中的实例也属于 provider；Dispose 必须能看见并中止它，不能只清理已经发布的 _ws。
            connecting?.Dispose();
            if (!ReferenceEquals(active, connecting)) active?.Dispose();
        }
    }
}
