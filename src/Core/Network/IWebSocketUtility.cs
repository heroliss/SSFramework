using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Event;
using Game.Framework.Utility;
using R3;

namespace Game.Framework.Network
{
    /// <summary>连接状态（3 档足够：无自动重连所以没有 Reconnecting 档）。
    /// 刻意不叫 <c>WebSocketState</c>——撞 <c>System.Net.WebSockets.WebSocketState</c>。</summary>
    public enum NetworkConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
    }

    /// <summary>
    /// 连接关闭事件：每次成功连接至多发布一次；用户主动 <see cref="IWebSocketUtility.Disconnect"/> 与意外断开都发，
    /// 业务重连逻辑过滤 <c>!ByUser</c>。随 Context <c>Dispose</c> 的整棵拆除不发事件。
    /// 写法照 <c>FlowChangedEvent</c> 用 <c>readonly struct</c> + 显式字段
    /// （内核程序集无 IsExternalInit polyfill，位置参数 record 的 init 访问器编译不过）。
    /// </summary>
    public readonly struct WebSocketClosedEvent : IEvent
    {
        /// <summary>true = 用户主动 Disconnect；false = 意外断开（对端关闭 / 收发异常）。</summary>
        public readonly bool ByUser;

        /// <summary>
        /// 框架拥有的稳定关闭原因（日志 / 提示用），不会直接透出平台或第三方 Adapter 的异常消息；
        /// 原始异常另由结构化日志或调用异常的 inner 保留。不要按完整句子驱动业务分支，重连只判断 <see cref="ByUser"/>。
        /// </summary>
        public readonly string Reason;

        public WebSocketClosedEvent(bool byUser, string reason)
        {
            ByUser = byUser;
            Reason = reason;
        }
    }

    /// <summary>
    /// WebSocket 长连接通道（一个实例 = 一条逻辑连接）。框架统一的有状态网络入口，
    /// 业务层通过 <c>GetUtility&lt;IWebSocketUtility&gt;</c> 访问。
    ///
    /// <para><b>消息建模双轨之二</b>（ADR-0028）：服务器推送/广播转框架 Event——<see cref="RegisterPush{TEvent}"/>
    /// 把推送的 <c>type</c> 映射为强类型事件，业务用 <c>Bag.Subscribe&lt;TEvent&gt;</c> 消费，与订 Model 事件同一套心智。
    /// 发送用 <see cref="Send{T}"/>（客户端 → 服务器）。</para>
    ///
    /// <para><b>wire 协议</b>：默认 JSON envelope <c>{"type":"xxx","payload":"&lt;载荷的 JSON 文本&gt;"}</c>——
    /// payload 二次编码是默认 JsonUtility 无法提取嵌套原始 JSON 的最省事解。二进制格式（Protobuf 等）的序列化器
    /// 实现 <see cref="IWebSocketEnvelopeSerializer"/> 接管 envelope 编解码与帧类型（payload 全程 byte[]、二进制帧）。</para>
    /// </summary>
    /// <remarks>
    /// <b>注册：</b><c>builder.RegisterOwned(new WebSocketUtility(), typeof(IWebSocketUtility))</c>——注册即注入
    /// （ADR-0019）回填 Context（<see cref="Send{T}"/> 转事件所需）；脱离容器 new 后未 Attach 就 <see cref="Connect"/> 抛。
    /// 战斗专用连接注册进 <c>FlowState</c> 子 Context，退出阶段整棵撤（含断开）。<br/>
    /// <b>线程：</b>公共 API 主线程调用；接收循环在后台收帧、每条消息切回主线程后再解析 + <c>SendEvent</c>
    /// （事件系统主线程独占的铁律，框架兜住）；<see cref="Send{T}"/> 内部 FIFO 串行（单 socket 不允许并发写）。<br/>
    /// <b>失败语义</b>（ADR-0028 §2）：<see cref="Connect"/> 失败/超时 → <see cref="NetworkException"/>；
    /// <see cref="Send{T}"/> 在未连接、或发送中途连接断掉时 → <see cref="NetworkException"/>
    /// （<see cref="NetworkErrorKind.ConnectionError"/>，传输层原始异常不外泄）；
    /// 旧连接排队中的发送在断开后同样以该错误收口，绝不会转发到新连接；
    /// 未注册的推送 type → Editor/Dev 一次性 warning + 丢弃；
    /// 坏消息（烂 JSON / 载荷不符）→ warning + 丢弃当条，不毒化接收循环。<br/>
    /// <b>推送事件类型约定：</b>默认 JSON 序列化器下用 <c>[Serializable] struct XxxPushEvent : IEvent</c> + <b>公共字段</b>承载数据
    /// （JsonUtility 只认字段，<b>不能用 record 位置参数</b>——那是属性、反序列化不出来）；
    /// <b>class 事件也允许</b>（约束只要求 <see cref="IEvent"/>）——二进制序列化器（如 Google.Protobuf 生成的 <c>IMessage</c> class）即走此路，
    /// 但引用类型事件<b>必须带 payload</b>（空 payload 无法构造默认实例，会被丢弃告警；struct 事件空 payload 仍取 <c>default</c>）。<br/>
    /// <b>刻意不做</b>：自动重连（订 <see cref="WebSocketClosedEvent"/> + 退避 Connect 样板见 guide §25）、
    /// WebGL（<see cref="ClientWebSocketProvider"/> 不支持）、RPC correlation id。<br/>
    /// <b>扩展点：</b>换传输 <see cref="IWebSocketProvider"/> / 换格式 <see cref="INetworkSerializer"/>（构造注入；
    /// 二进制格式额外实现 <see cref="IWebSocketEnvelopeSerializer"/>）。
    /// </remarks>
    public interface IWebSocketUtility : IUtility
    {
        /// <summary>连接状态（响应式，订阅即得当前值）。UI 直接 <c>Bag.Subscribe(ws.State, ...)</c>。</summary>
        ReadOnlyReactiveProperty<NetworkConnectionState> State { get; }

        /// <summary>
        /// 把服务器推送的 <paramref name="type"/> 映射为框架事件：收到该 type 时把 payload 反序列化为 TEvent 并 SendEvent。
        /// 连接前后均可注册；同 type 重复注册抛 <see cref="InvalidOperationException"/>（代码写错了）。
        /// TEvent 默认 JSON 下用 <c>[Serializable] struct</c> + 公共字段；二进制序列化器可用 class 消息（见类型 remarks 的约定）。
        /// </summary>
        void RegisterPush<TEvent>(string type) where TEvent : IEvent;

        /// <summary>以绝对 <c>ws://</c> / <c>wss://</c> 地址建立连接；地址必须包含 host，且不能包含
        /// userinfo 或 fragment。格式不符合时抛 <see cref="ArgumentException"/>，且不会调用 Provider。
        /// 已在 Connecting/Connected 时调用抛
        /// <see cref="InvalidOperationException"/>；失败/超时（含 provider 在 token 未取消时自发 OCE）抛
        /// <see cref="NetworkException"/> 且状态回 Disconnected。调用方 / Context / Disconnect 取消仍原样抛 OCE。
        /// provider 成功返回是物理 ownership 提交点；普通 caller 取消与完成竞态时允许成功赢并建立 session。
        /// 若更早已有 Connecting-Disconnect intent，则物理成功会被立即 Abort，不发布 Connected / Push / ClosedEvent。
        /// 若前一次 <see cref="Disconnect"/> 已把公开状态置为 Disconnected、但底层关闭仍在收尾，本调用会在内部等待其退场；
        /// 等待只受本调用的 <paramref name="ct"/> 控制，不继承前一次 Disconnect 的异常。</summary>
        UniTask Connect(string url, CancellationToken ct = default);

        /// <summary>
        /// 优雅关闭（发 Close 帧）。未连接 = no-op；Connecting 中 = 取消并等待在途 <see cref="Connect"/> owner 清场，
        /// 常规取消令其 await 收到 OCE 且不发事件；若物理成功恰好赢得竞态，也会在逻辑发布前 Abort，仍不暴露 session / 事件。
        /// 本调用返回后可立即重连。已连接时会立即提交逻辑断开（State→Disconnected），取消该连接的发送队列，
        /// 等发送退场后尽力发 Close，再停止接收并发布一次 <see cref="WebSocketClosedEvent"/>(ByUser:true)。
        /// <para><paramref name="ct"/> 在入口已经取消时不提交断开；关闭开始后再取消只停止优雅握手等待，
        /// session 清理与关闭事件仍完成，随后向调用方原样抛 <see cref="OperationCanceledException"/>；若 token 未取消而
        /// provider 自发 OCE，则只按 best-effort 关闭握手失败记录，不向调用方伪造取消。若 owner 已取消而 Adapter 以
        /// ODE / socket error 等其它异常形态退场，框架会保留其为 inner 并统一收口成 OCE。</para>
        /// </summary>
        UniTask Disconnect(CancellationToken ct = default);

        /// <summary>发送一条 envelope 消息（type + 序列化后的 payload）。多次调用保序（内部 FIFO）。
        /// 未连接、发送中途断掉、或该帧仍排队时所属连接已被替换，均抛 <see cref="NetworkException"/>
        /// （<see cref="NetworkErrorKind.ConnectionError"/>）；旧帧不会跨连接发送。current session 的物理发送失败还会发布一次
        /// <see cref="WebSocketClosedEvent"/>(ByUser:false)，不依赖接收循环随后也报错。调用方 / Context 取消优先保持 OCE；
        /// Adapter 在已取消 owner 下抛出的其它异常形态只作为 inner 保留。</summary>
        UniTask Send<T>(string type, T payload, CancellationToken ct = default) where T : class;

        /// <summary>发送无载荷消息（如心跳 ping）。</summary>
        UniTask Send(string type, CancellationToken ct = default);
    }
}
