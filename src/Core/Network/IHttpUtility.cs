using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Utility;

namespace Game.Framework.Network
{
    /// <summary>
    /// HTTP 请求-响应门面。框架统一的短连接网络入口，业务层通过 <c>GetUtility&lt;IHttpUtility&gt;</c> 访问。
    ///
    /// <para><b>消息建模双轨之一</b>（ADR-0028）：请求-响应 = UniTask 返回值，不进事件系统——
    /// 「发起方等结果」的因果关系用返回值表达最直白；服务器主动推送才走框架 Event（见 <see cref="IWebSocketUtility"/>）。</para>
    ///
    /// <para><b>形态：REST 动词 + 强类型消息</b>——请求 / 响应各定义一个 <c>[Serializable]</c> 类，
    /// <c>await http.Post&lt;LoginReq, LoginResp&gt;("api/login", req, ct)</c> 一行完成序列化、发送、校验、反序列化。
    /// query 直接写在 path 里（动态值用 <c>Uri.EscapeDataString</c>），刻意不做 query builder。</para>
    ///
    /// <para><b>严格与宽容两种模式</b>：动词方法（Get / Post）非 2xx 抛异常（游戏 API 常态预期成功）；
    /// <see cref="Send"/> 逃生舱只要 HTTP 交换完成就返回响应不抛（自己查状态码），并承载
    /// PUT / DELETE / raw 字节 / 每请求头等低频形态。</para>
    /// </summary>
    /// <remarks>
    /// <b>注册：</b><c>builder.RegisterOwned(new HttpUtility("https://api.xxx.com"), typeof(IHttpUtility))</c>——
    /// 随 Context Dispose 取消所有在途请求。环境切换（dev / prod）= 注册时传不同 baseUrl（构造定死，运行期不可变）。<br/>
    /// <b>线程：</b>公共 API 主线程调用（框架统一契约）；默认传输走 UnityWebRequest 引擎异步、全程不下线程池
    /// （WebGL 兼容的来源），完成后回主线程返回。请求之间天然并行（无共享介质，不像存储需要 FIFO）。<br/>
    /// <b>失败语义</b>（ADR-0028 §2）：
    /// <list type="bullet">
    ///   <item>DNS / 拒连 / 网络断 → <see cref="NetworkException"/>（ConnectionError）；超时 →（Timeout）。</item>
    ///   <item>外部 ct 取消 → <see cref="OperationCanceledException"/>（不包装，框架统一约定）。</item>
    ///   <item>动词方法非 2xx → <see cref="NetworkException"/>（HttpError，带 StatusCode + ResponseBody）；
    ///         预期 404 用 <c>catch ... when</c> 过滤，框架不折叠成 null。</item>
    ///   <item>响应体反序列化失败 → （DeserializeError）；2xx 空响应体 → 返回 null（唯一的 null 语义）。</item>
    ///   <item>参数非法 / 相对 path 但未配 BaseUrl → 抛参数异常；Dispose 后调用 → 抛 <see cref="ObjectDisposedException"/>。</item>
    /// </list>
    /// <b>刻意不做</b>：自动重试（幂等性只有业务知道，退避循环样板见 guide §25）、大文件下载（归资源系统）、
    /// 请求队列 / 限流 / 缓存（现有原语可组合）。<br/>
    /// <b>扩展点：</b>换传输（BestHTTP / HttpClient）实现 <see cref="IHttpProvider"/>；换格式（Protobuf / MemoryPack）
    /// 实现 <see cref="INetworkSerializer"/>——都经 <see cref="HttpUtility"/> 构造注入，业务代码零改动。
    /// </remarks>
    public interface IHttpUtility : IUtility
    {
        /// <summary>基地址（构造传入、已去尾部 <c>/</c>）；null = 未配置，此时所有 path 必须是绝对 URL。</summary>
        string BaseUrl { get; }

        /// <summary>
        /// 设置随后每个请求都携带的默认头（同名覆盖、头名不区分大小写；value 传 null 移除）。
        /// 典型用法：登录拿到 token 后 <c>SetHeader("Authorization", $"Bearer {token}")</c>，之后所有请求自动带上。
        /// </summary>
        void SetHeader(string name, string value);

        /// <summary>
        /// GET 并把 2xx 响应体反序列化为 TResp（空体 → null）。
        /// query 直接写在 path：<c>Get&lt;Board&gt;($"api/rank?count={n}", ct)</c>。非 2xx 抛（HttpError）。
        /// </summary>
        UniTask<TResp> Get<TResp>(string path, CancellationToken ct = default) where TResp : class;

        /// <summary>POST：body 经 serializer 序列化（Content-Type 随格式自动带上），2xx 响应体反序列化为 TResp（空体 → null）。</summary>
        UniTask<TResp> Post<TReq, TResp>(string path, TReq body, CancellationToken ct = default)
            where TReq : class where TResp : class;

        /// <summary>POST 不关心响应体（打点 / ack 型接口）。仍校验状态码，非 2xx 抛（HttpError）。</summary>
        UniTask Post<TReq>(string path, TReq body, CancellationToken ct = default) where TReq : class;

        /// <summary>
        /// 逃生舱：任意动词 / raw 字节体 / 每请求头与超时 / 读状态码与响应头。
        /// 与动词方法的关键差异——只要 HTTP 交换完成（含 4xx/5xx）就<b>返回响应不抛</b>（查 <see cref="HttpResponse.IsSuccess"/>）；
        /// 只有传输层失败（连不上 / 超时 / 取消）才抛。
        /// </summary>
        UniTask<HttpResponse> Send(HttpRequest request, CancellationToken ct = default);
    }
}
