using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Network
{
    /// <summary>
    /// WebSocket 传输接缝：一条逻辑连接的建连 / 收 / 发 / 关。默认 <see cref="ClientWebSocketProvider"/>
    /// （System.Net.WebSockets.ClientWebSocket，除 WebGL 全平台）；换 BestHTTP / NativeWebSocket 实现本接口
    /// 经 <see cref="WebSocketUtility"/> 构造注入。适配层保留 Async 后缀。ADR-0028 §5。
    /// </summary>
    /// <remarks>
    /// 实现契约：
    /// <list type="bullet">
    ///   <item><see cref="ConnectAsync"/> 可被重复调用（断线后重连）——实现应每次建立全新底层连接。</item>
    ///   <item><see cref="ReceiveAsync"/> 返回一条<b>完整</b>消息（聚合分帧）；对端正常关闭 → 返回 null；
    ///         异常断开 → 抛。可能在后台线程完成（utility 收到后自行切主线程）。</item>
    ///   <item><see cref="SendAsync"/> 发一帧（<c>binary</c> 决定帧类型：false = 文本帧 UTF-8 / true = 二进制帧，
    ///         由序列化格式经 <see cref="IWebSocketEnvelopeSerializer.UseBinaryFrames"/> 决定）；utility 已串行化调用，实现无需自加锁。</item>
    ///   <item><see cref="CloseAsync"/> 尽力优雅关闭：发出 Close 帧即可返回、不必等对端 ack；
    ///         会在仍有挂起 <see cref="ReceiveAsync"/> 时被调用，实现不得与挂起接收互斥或死锁；未连接 / 已关闭 = no-op。</item>
    ///   <item>所有方法尊重 ct 取消（抛 <see cref="OperationCanceledException"/>）。</item>
    /// </list>
    /// </remarks>
    public interface IWebSocketProvider : IDisposable
    {
        UniTask ConnectAsync(Uri uri, CancellationToken ct);

        UniTask SendAsync(byte[] payload, bool binary, CancellationToken ct);

        /// <summary>收一条完整消息。对端正常关闭 → null；异常断开 → 抛。</summary>
        UniTask<byte[]> ReceiveAsync(CancellationToken ct);

        /// <summary>发起优雅关闭（见 remarks 实现契约第 3 条）。</summary>
        UniTask CloseAsync(CancellationToken ct);
    }
}
