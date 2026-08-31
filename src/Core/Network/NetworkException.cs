using System;

namespace Game.Framework.Network
{
    /// <summary>
    /// 网络失败的分类。业务 catch <see cref="NetworkException"/> 后按 Kind 决定反应——
    /// 环境问题（ConnectionError / Timeout）可提示玩家重试；HttpError 查 StatusCode 走业务分支；
    /// DeserializeError 是双端契约不符（该报警不该重试）。
    /// </summary>
    public enum NetworkErrorKind
    {
        /// <summary>DNS 失败 / 拒绝连接 / 网络断开 / 长连接通道不可用——请求根本没完成 HTTP 交换。</summary>
        ConnectionError,

        /// <summary>超出默认或指定超时（utility 内部计时触发）。外部 CancellationToken 取消不走这里——
        /// 那是调用方意图，抛 <see cref="OperationCanceledException"/> 不包装。</summary>
        Timeout,

        /// <summary>请求送达服务器但响应非 2xx。<see cref="NetworkException.StatusCode"/> /
        /// <see cref="NetworkException.ResponseBody"/> 已填。</summary>
        HttpError,

        /// <summary>响应体 / 推送载荷无法反序列化为目标类型——服务器返回与客户端类型契约不符。</summary>
        DeserializeError,
    }

    /// <summary>
    /// 网络模块唯一异常类型（刻意不复用 <c>System.Net.Http.HttpRequestException</c>——名字撞 BCL 且缺分级信息）。
    /// 预期内的业务失败（如查询不到 → 404）也走这里：REST 状态码语义因服务器而异，框架不替业务折叠成 null，
    /// 用 <c>catch ... when (e.Kind == HttpError &amp;&amp; e.StatusCode == 404)</c> 过滤。ADR-0028 §2。
    /// </summary>
    public sealed class NetworkException : Exception
    {
        /// <summary>框架归一化后的失败类别。</summary>
        public NetworkErrorKind Kind { get; }

        /// <summary>HTTP 状态码。仅 <see cref="NetworkErrorKind.HttpError"/> 有意义，其余 Kind 为 0。</summary>
        public int StatusCode { get; }

        /// <summary>服务器错误响应体（UTF-8 解码、截断至 4KB），排查用；无响应体时为 null。</summary>
        public string ResponseBody { get; }

        /// <summary>创建一条保留网络失败分类与可选 HTTP 上下文的异常。</summary>
        /// <param name="kind">归一化失败类别。</param>
        /// <param name="message">可读错误消息。</param>
        /// <param name="statusCode">HTTP 状态码；非 HTTP 失败传 0。</param>
        /// <param name="responseBody">可选且已截断的响应体。</param>
        /// <param name="inner">底层异常。</param>
        public NetworkException(NetworkErrorKind kind, string message,
            int statusCode = 0, string responseBody = null, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
