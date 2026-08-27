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
    ///   <item><see cref="ConnectAsync"/> 可被顺序重复调用（断线后重连）——实现应每次建立全新底层连接。
    ///         utility 会串行化 Connect 与前一次 Close，Adapter 无需为两者自建并发锁；但 <see cref="IDisposable.Dispose"/>
    ///         可在 Connect 挂起时发生，实现必须同时拥有并中止“连接中”与“已连接”的物理实例，禁止 Dispose 后迟到发布 socket。
    ///         成功返回就是物理连接 ownership 的提交点：取消与完成竞态时可以由成功赢，但此时必须已发布一条可用连接；
    ///         不能“成功返回、随后靠调用方 post-check 丢弃”。</item>
    ///   <item>所有异步方法都允许在任意线程完成；utility 会在触碰响应式 State、Framework Event、session owner 或完成
    ///         主线程公共调用前切回主线程。Adapter 不得假设自己的 continuation 线程会成为框架线程。</item>
    ///   <item><see cref="ReceiveAsync"/> 返回一条<b>完整</b>消息（聚合分帧）；对端正常关闭 → 返回 null；
    ///         异常断开 → 抛。</item>
    ///   <item><see cref="SendAsync"/> 发一帧（<c>binary</c> 决定帧类型：false = 文本帧 UTF-8 / true = 二进制帧，
    ///         由序列化格式经 <see cref="IWebSocketEnvelopeSerializer.UseBinaryFrames"/> 决定）；utility 已串行化调用，实现无需自加锁。</item>
    ///   <item>每次 Send / Receive / Close 必须在方法入口绑定当时的物理连接；实现内部不得因后续重连改读可变字段，
    ///         尤其分片 Receive 必须在同一 socket 上聚合完整消息。</item>
    ///   <item><see cref="CloseAsync"/> 尽力优雅关闭：发出 Close 帧即可返回、不必等对端 ack；
    ///         会在仍有挂起 <see cref="ReceiveAsync"/> 时被调用，实现不得与挂起接收互斥或死锁；未连接 / 已关闭 = no-op。</item>
    ///   <item><see cref="Abort"/> 立即中止并摘除当前已提交的物理连接，且 Provider 之后仍可再次 Connect；
    ///         用于取消赢在逻辑发布前、关闭超时或传输损坏，不能把半关闭 socket 留给下一代。</item>
    ///   <item>所有方法尊重 ct 取消（抛 <see cref="OperationCanceledException"/>）；token 未取消时 Adapter 自发 OCE 会被 utility
    ///         视为传输失败，而不是伪造调用方取消。Adapter 的 token 回调不应抛异常；若仍抛出，utility 会记录并隔离，
    ///         不允许回调异常截断 Connect / Session / lifetime owner 的清理。</item>
    /// </list>
    /// </remarks>
    public interface IWebSocketProvider : IDisposable
    {
        UniTask ConnectAsync(Uri uri, CancellationToken ct);

        UniTask SendAsync(byte[] payload, bool binary, CancellationToken ct);

        /// <summary>收一条完整消息。对端正常关闭 → null；异常断开 → 抛。</summary>
        UniTask<byte[]> ReceiveAsync(CancellationToken ct);

        /// <summary>发起优雅关闭（见 remarks 的 Close 实现契约）。</summary>
        UniTask CloseAsync(CancellationToken ct);

        /// <summary>立即中止并摘除当前物理连接；未连接 = no-op，调用后 Provider 仍可重连。</summary>
        void Abort();
    }
}
