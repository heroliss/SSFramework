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

        private ClientWebSocket _ws;

        public async UniTask ConnectAsync(Uri uri, CancellationToken ct)
        {
            _ws?.Dispose();
            _ws = new ClientWebSocket();
            _ws.Options.Proxy = null; // 直连（ADR-0028 §8）——系统代理会挡 localhost，且游戏本就直连服务器
            await _ws.ConnectAsync(uri, ct);
        }

        public async UniTask SendAsync(byte[] payload, CancellationToken ct)
        {
            await _ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        public async UniTask<byte[]> ReceiveAsync(CancellationToken ct)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[ReceiveBufferSize];
            while (true)
            {
                WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // RFC6455：收到对端 Close 帧要回一个 Close ack 才算完成关闭握手（best-effort——
                    // 对端可能已直接断线、或这是我方 Close 的 ack（CloseSent 状态回帧会抛），失败按已关闭处理）。
                    try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None); }
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
            if (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived))
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
        }

        public void Dispose() => _ws?.Dispose();
    }
}
