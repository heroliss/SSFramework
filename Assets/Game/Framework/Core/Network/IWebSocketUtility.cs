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
    /// 连接关闭事件：用户主动 <see cref="IWebSocketUtility.Disconnect"/> 与意外断开都发，
    /// 业务重连逻辑过滤 <c>!ByUser</c>。写法照 <c>FlowChangedEvent</c> 用 <c>readonly struct</c> + 显式字段
    /// （内核程序集无 IsExternalInit polyfill，位置参数 record 的 init 访问器编译不过）。
    /// </summary>
    public readonly struct WebSocketClosedEvent : IEvent
    {
        /// <summary>true = 用户主动 Disconnect；false = 意外断开（对端关闭 / 收发异常）。</summary>
        public readonly bool ByUser;

        /// <summary>关闭原因描述（日志 / 提示用）。</summary>
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
    /// <para><b>wire 协议</b>：JSON envelope <c>{"type":"xxx","payload":"&lt;载荷的 JSON 文本&gt;"}</c>——
    /// payload 二次编码是默认 JsonUtility 无法提取嵌套原始 JSON 的最省事解；换强序列化器后的嵌套形态等真实后端再议。</para>
    /// </summary>
    /// <remarks>
    /// <b>注册：</b><c>builder.RegisterOwned(new WebSocketUtility(), typeof(IWebSocketUtility))</c>——注册即注入
    /// （ADR-0019）回填 Context（<see cref="Send{T}"/> 转事件所需）；脱离容器 new 后未 Attach 就 <see cref="Connect"/> 抛。
    /// 战斗专用连接注册进 <c>FlowState</c> 子 Context，退出阶段整棵撤（含断开）。<br/>
    /// <b>线程：</b>公共 API 主线程调用；接收循环在后台收帧、每条消息切回主线程后再解析 + <c>SendEvent</c>
    /// （事件系统主线程独占的铁律，框架兜住）；<see cref="Send{T}"/> 内部 FIFO 串行（单 socket 不允许并发写）。<br/>
    /// <b>失败语义</b>（ADR-0028 §2）：<see cref="Connect"/> 失败/超时 → <see cref="NetworkException"/>；
    /// <see cref="Send{T}"/> 在未连接、或发送中途连接断掉时 → （ConnectionError，传输层原始异常不外泄）；
    /// 未注册的推送 type → Editor/Dev 一次性 warning + 丢弃；
    /// 坏消息（烂 JSON / 载荷不符）→ warning + 丢弃当条，不毒化接收循环。<br/>
    /// <b>推送事件类型约定：</b><c>[Serializable] struct XxxPushEvent : IEvent</c> + <b>公共字段</b>承载数据——
    /// 默认 JsonUtility 只认字段，<b>不能用 record 位置参数</b>（那是属性、反序列化不出来）。<br/>
    /// <b>刻意不做</b>：自动重连（订 <see cref="WebSocketClosedEvent"/> + 退避 Connect 样板见 guide §25）、
    /// WebGL（<see cref="ClientWebSocketProvider"/> 不支持）、RPC correlation id。<br/>
    /// <b>扩展点：</b>换传输 <see cref="IWebSocketProvider"/> / 换格式 <see cref="INetworkSerializer"/>（构造注入）。
    /// </remarks>
    public interface IWebSocketUtility : IUtility
    {
        /// <summary>连接状态（响应式，订阅即得当前值）。UI 直接 <c>Bag.Subscribe(ws.State, ...)</c>。</summary>
        ReadOnlyReactiveProperty<NetworkConnectionState> State { get; }

        /// <summary>
        /// 把服务器推送的 <paramref name="type"/> 映射为框架事件：收到该 type 时把 payload 反序列化为 TEvent 并 SendEvent。
        /// 连接前后均可注册；同 type 重复注册抛 <see cref="InvalidOperationException"/>（代码写错了）。
        /// TEvent 用 <c>[Serializable] struct</c> + 公共字段（见类型 remarks 的约定）。
        /// </summary>
        void RegisterPush<TEvent>(string type) where TEvent : struct, IEvent;

        /// <summary>建立连接（<c>ws://</c> / <c>wss://</c>）。已在 Connecting/Connected 时调用抛
        /// <see cref="InvalidOperationException"/>；失败/超时抛 <see cref="NetworkException"/> 且状态回 Disconnected。</summary>
        UniTask Connect(string url, CancellationToken ct = default);

        /// <summary>优雅关闭（发 Close 帧）。未连接 = no-op；Connecting 中 = 取消在途 <see cref="Connect"/>
        /// （其 await 收到 OCE）、不发事件；已连接则完成后 State→Disconnected + <see cref="WebSocketClosedEvent"/>(ByUser:true)。</summary>
        UniTask Disconnect(CancellationToken ct = default);

        /// <summary>发送一条 envelope 消息（type + 序列化后的 payload）。多次调用保序（内部 FIFO）。
        /// 未连接、或发送中途连接断掉，均抛（ConnectionError）。</summary>
        UniTask Send<T>(string type, T payload, CancellationToken ct = default) where T : class;

        /// <summary>发送无载荷消息（如心跳 ping）。</summary>
        UniTask Send(string type, CancellationToken ct = default);
    }
}
