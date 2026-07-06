using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Network
{
    /// <summary>
    /// <see cref="IHttpUtility"/> 的默认实现：URL 拼接 + 头合并 + 超时计时 + 异常折叠的编排层，
    /// 传输与格式分别委托给 <see cref="IHttpProvider"/> / <see cref="INetworkSerializer"/>（构造注入，默认 UnityWebRequest + JSON）。
    /// </summary>
    /// <remarks>
    /// <b>注册：</b><c>builder.RegisterOwned(new HttpUtility(baseUrl), typeof(IHttpUtility))</c>（推荐，随 Context
    /// Dispose 取消在途请求）；全局唯一、不关心释放用 <c>RegisterValue</c>。不依赖 Context，可被父子 Context 共享。<br/>
    /// <b>超时实现</b>：每请求 linked CTS = 外部 ct + 生命周期 ct + <c>CancelAfter</c> 计时，provider 只需尊重取消；
    /// 触发后按「外部 ct 是否已取消」区分——是 → 原样抛 OCE（调用方意图），否 → 折叠为 Timeout（网络环境问题）。<br/>
    /// <b>Dispose 后不可再用</b>（抛 <see cref="ObjectDisposedException"/>）；Dispose 取消所有在途请求（在 await 处收到 OCE）。
    /// </remarks>
    public sealed class HttpUtility : IHttpUtility, IDisposable
    {
        private const int ErrorBodyMaxChars = 4096; // 异常携带的响应体上限——排查够用，防服务器错误页把日志撑爆

        private readonly IHttpProvider _provider;
        private readonly INetworkSerializer _serializer;
        private readonly float _defaultTimeoutSeconds;
        private readonly Dictionary<string, string> _defaultHeaders = new(StringComparer.OrdinalIgnoreCase); // HTTP 头名不区分大小写
        private readonly CancellationTokenSource _lifetimeCts = new();
        private bool _disposed;

        public string BaseUrl { get; }

        /// <param name="baseUrl">基地址（尾部 / 自动去除）；null = 所有 path 必须是绝对 URL。</param>
        /// <param name="provider">传输实现；null = 默认 <see cref="UnityWebRequestHttpProvider"/>。</param>
        /// <param name="serializer">序列化格式；null = 默认 <see cref="JsonUtilityNetworkSerializer"/>。</param>
        /// <param name="defaultTimeoutSeconds">默认超时秒数（&lt;=0 = 不限时，一般只在调试用）。单次覆盖走 <see cref="Send"/>。</param>
        public HttpUtility(string baseUrl = null, IHttpProvider provider = null,
            INetworkSerializer serializer = null, float defaultTimeoutSeconds = 10f)
        {
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl.TrimEnd('/');
            _provider = provider ?? new UnityWebRequestHttpProvider();
            _serializer = serializer ?? new JsonUtilityNetworkSerializer();
            _defaultTimeoutSeconds = defaultTimeoutSeconds;
        }

        public void SetHeader(string name, string value)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("header 名不能为空。", nameof(name));
            if (value == null) _defaultHeaders.Remove(name);
            else _defaultHeaders[name] = value;
        }

        public async UniTask<TResp> Get<TResp>(string path, CancellationToken ct = default) where TResp : class
        {
            var resp = await SendChecked("GET", path, body: null, ct);
            return DeserializeBody<TResp>(resp, "GET", path);
        }

        public async UniTask<TResp> Post<TReq, TResp>(string path, TReq body, CancellationToken ct = default)
            where TReq : class where TResp : class
        {
            var resp = await SendChecked("POST", path, SerializeBody(path, body), ct);
            return DeserializeBody<TResp>(resp, "POST", path);
        }

        public async UniTask Post<TReq>(string path, TReq body, CancellationToken ct = default) where TReq : class
        {
            await SendChecked("POST", path, SerializeBody(path, body), ct);
        }

        public async UniTask<HttpResponse> Send(HttpRequest request, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrEmpty(request.Method)) throw new ArgumentException("HttpRequest.Method 不能为空。", nameof(request));

            string contentType = request.ContentType ?? (request.Body != null ? _serializer.ContentType : null);
            return await SendCore(request.Method, request.Path, request.Body, contentType,
                request.Headers, request.TimeoutSeconds, ct);
        }

        /// <summary>释放并取消所有在途请求（各自的 await 处收到 OCE）。Dispose 后调用任何 API 抛。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
            _provider.Dispose();
        }

        // ── 编排核心 ─────────────────────────────────────────────────────────

        // 动词门面的严格路径：默认头 + 默认超时 + 非 2xx 抛 HttpError。
        private async UniTask<HttpResponse> SendChecked(string method, string path, byte[] body, CancellationToken ct)
        {
            ThrowIfDisposed();
            string contentType = body != null ? _serializer.ContentType : null;
            var resp = await SendCore(method, path, body, contentType, extraHeaders: null, timeoutOverride: null, ct);
            if (!resp.IsSuccess)
                throw new NetworkException(NetworkErrorKind.HttpError,
                    $"HTTP {resp.StatusCode}：{method} {path}",
                    resp.StatusCode, Truncate(resp.BodyText));
            return resp;
        }

        private async UniTask<HttpResponse> SendCore(string method, string path, byte[] body, string contentType,
            Dictionary<string, string> extraHeaders, float? timeoutOverride, CancellationToken ct)
        {
            string url = ResolveUrl(path);
            var headers = MergeHeaders(extraHeaders);

            float timeout = timeoutOverride ?? _defaultTimeoutSeconds;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            if (timeout > 0) linked.CancelAfter(TimeSpan.FromSeconds(timeout));

            try
            {
                return await _provider.SendAsync(url, method, body, contentType, headers, linked.Token);
            }
            catch (OperationCanceledException e)
            {
                // 三个取消源同 token，靠源头回溯区分：外部取消 / Dispose → 是调用方（或宿主销毁）意图，原样抛；
                // 都不是 → 只剩 CancelAfter 计时，折叠为 Timeout（网络环境问题，业务可提示重试）。
                if (ct.IsCancellationRequested || _lifetimeCts.IsCancellationRequested) throw;
                throw new NetworkException(NetworkErrorKind.Timeout,
                    $"请求超时（{timeout:0.#}s）：{method} {url}", inner: e);
            }
        }

        private string ResolveUrl(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path 不能为空。", nameof(path));
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return path;
            if (BaseUrl == null)
                throw new ArgumentException($"path '{path}' 是相对路径但未配置 BaseUrl——注册时传 baseUrl，或使用绝对 URL。", nameof(path));
            return path[0] == '/' ? BaseUrl + path : BaseUrl + "/" + path;
        }

        // 默认头与每请求头合并，同名（不区分大小写）后者覆盖。去重在编排层完成、provider 拿到的列表无重复名——
        // 覆盖语义不依赖具体传输对重复设置头的行为（RFC 允许逗号拼接，各库实现不一）。无任何头返回 null。
        private List<KeyValuePair<string, string>> MergeHeaders(Dictionary<string, string> extra)
        {
            if (_defaultHeaders.Count == 0 && (extra == null || extra.Count == 0)) return null;

            Dictionary<string, string> source;
            if (extra == null || extra.Count == 0)
            {
                source = _defaultHeaders;
            }
            else
            {
                source = new Dictionary<string, string>(_defaultHeaders, StringComparer.OrdinalIgnoreCase);
                foreach (var h in extra) source[h.Key] = h.Value;
            }

            var merged = new List<KeyValuePair<string, string>>(source.Count);
            foreach (var h in source) merged.Add(h);
            return merged;
        }

        private byte[] SerializeBody<TReq>(string path, TReq body) where TReq : class
        {
            ThrowIfDisposed();
            if (body == null) throw new ArgumentNullException(nameof(body), $"Post('{path}') 的 body 不能为 null——无体请求走 Send 逃生舱。");
            return _serializer.Serialize(body); // 序列化失败在发送前就抛给调用方（参数问题，不折叠）
        }

        private TResp DeserializeBody<TResp>(HttpResponse resp, string method, string path) where TResp : class
        {
            if (resp.Body.Length == 0) return null; // 2xx 空体：唯一的 null 语义
            try
            {
                return _serializer.Deserialize<TResp>(resp.Body);
            }
            catch (Exception e)
            {
                throw new NetworkException(NetworkErrorKind.DeserializeError,
                    $"响应体无法反序列化为 {typeof(TResp).Name}：{method} {path}（{e.GetType().Name}: {e.Message}）",
                    responseBody: Truncate(resp.BodyText), inner: e);
            }
        }

        private static string Truncate(string s) =>
            string.IsNullOrEmpty(s) ? null : (s.Length <= ErrorBodyMaxChars ? s : s.Substring(0, ErrorBodyMaxChars));

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(HttpUtility), "HTTP 工具已随 Context 释放——检查是否持有了过期引用。");
        }
    }
}
