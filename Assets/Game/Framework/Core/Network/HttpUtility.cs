using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;

namespace Game.Framework.Network
{
    /// <summary>
    /// <see cref="IHttpUtility"/> 的默认实现：URL 拼接 + 头合并 + 超时计时 + 异常折叠的编排层，
    /// 传输与格式分别委托给 <see cref="IHttpProvider"/> / <see cref="INetworkSerializer"/>（构造注入，默认 UnityWebRequest + JSON）。
    /// </summary>
    /// <remarks>
    /// <b>注册：</b><c>builder.RegisterOwned(new HttpUtility(baseUrl), typeof(IHttpUtility))</c>（推荐，随 Context
    /// Dispose 取消在途请求）；全局唯一、不关心释放用 <c>RegisterValue</c>。不依赖 Context，可被父子 Context 共享。<br/>
    /// <b>超时实现</b>：每个请求有独立 owner；外部 ct、生命周期与 deadline 只向该 owner 发出取消意图，
    /// provider 只需尊重 owner token。deadline 使用显式 Send-vs-Delay 竞速后安全取消，不让
    /// <c>CancelAfter</c> 的 timer 线程直接承接第三方取消回调异常。<br/>
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

        /// <summary>
        /// 一次 HTTP 交换的私有 owner。调用方 / Utility 生命周期 / deadline 都只取消本 owner，
        /// 取消回调异常在这里隔离，不能反向逃逸到外部 CTS 或 timer 线程并截断清理。
        /// </summary>
        private sealed class RequestOwner : IDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly string _label;
            private readonly object _gate = new();
            private CancellationTokenRegistration _callerRegistration;
            private CancellationTokenRegistration _lifetimeRegistration;
            private int _timedOut;
            private int _cancelDepth;
            private bool _disposeRequested;
            private bool _cleanupClaimed;

            public CancellationToken Token { get; }
            public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

            public RequestOwner(CancellationToken callerToken, CancellationToken lifetimeToken, string label)
            {
                _label = label;
                Token = _cts.Token;
                _callerRegistration = callerToken.Register(CancelFromCaller);
                _lifetimeRegistration = lifetimeToken.Register(CancelFromLifetime);
            }

            public void RequestTimeout()
            {
                Interlocked.Exchange(ref _timedOut, 1);
                Cancel($"{_label} 超时 owner");
            }

            public void Dispose()
            {
                bool cleanup;
                lock (_gate)
                {
                    if (_disposeRequested) return;
                    _disposeRequested = true;
                    cleanup = TryClaimCleanupLocked();
                }
                if (cleanup) Cleanup();
            }

            private void CancelFromCaller() => Cancel($"{_label} 调用方 owner");
            private void CancelFromLifetime() => Cancel($"{_label} 生命周期 owner");

            private void Cancel(string label)
            {
                lock (_gate)
                {
                    if (_cleanupClaimed) return;
                    _cancelDepth++;
                }

                try
                {
                    CancelOwnerSafely(_cts, label);
                }
                finally
                {
                    bool cleanup;
                    lock (_gate)
                    {
                        _cancelDepth--;
                        cleanup = TryClaimCleanupLocked();
                    }
                    if (cleanup) Cleanup();
                }
            }

            private bool TryClaimCleanupLocked()
            {
                if (_cleanupClaimed || !_disposeRequested || _cancelDepth != 0) return false;
                _cleanupClaimed = true;
                return true;
            }

            private void Cleanup()
            {
                // Provider 取消可能同步内联完成整个 await 链；等 Cancel 完整退栈后再 Dispose CTS，
                // 避免在 CancellationTokenSource.Cancel 正遍历回调时释放同一个 owner。
                _callerRegistration.Dispose();
                _lifetimeRegistration.Dispose();
                _cts.Dispose();
            }
        }

        public string BaseUrl { get; }

        /// <param name="baseUrl">基地址（尾部 / 自动去除）；null = 所有 path 必须是绝对 URL。</param>
        /// <param name="provider">传输实现；null = 默认 <see cref="UnityWebRequestHttpProvider"/>。</param>
        /// <param name="serializer">序列化格式；null = 默认 <see cref="JsonUtilityNetworkSerializer"/>。</param>
        /// <param name="defaultTimeoutSeconds">默认超时秒数（有限值；&lt;=0 = 不限时，一般只在调试用）。单次覆盖走 <see cref="Send"/>。</param>
        public HttpUtility(string baseUrl = null, IHttpProvider provider = null,
            INetworkSerializer serializer = null, float defaultTimeoutSeconds = 10f)
        {
            ValidateTimeout(defaultTimeoutSeconds, nameof(defaultTimeoutSeconds));
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
            CancelOwnerSafely(_lifetimeCts, "HTTP 工具生命周期 owner");
            try
            {
                _provider.Dispose();
            }
            finally
            {
                _lifetimeCts.Dispose();
            }
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
            TimeSpan? deadline = CreateDeadline(timeout,
                timeoutOverride.HasValue ? nameof(HttpRequest.TimeoutSeconds) : "defaultTimeoutSeconds");
            using var owner = new RequestOwner(ct, _lifetimeCts.Token, $"{method} {url}");

            try
            {
                UniTask<HttpResponse> providerTask = _provider
                    .SendAsync(url, method, body, contentType, headers, owner.Token);
                var outcome = new UniTaskCompletionSource<HttpResponse>();
                var physicalCompletion = new UniTaskCompletionSource();
                ObserveProviderSend(providerTask, outcome, physicalCompletion).Forget(e =>
                    Log.Error("HTTP Provider 结果观察器异常，请求终态可能未正常传播。", e, nameof(HttpUtility)));

                CancellationTokenSource deadlineCts = null;
                HttpResponse response;
                try
                {
                    if (deadline.HasValue)
                    {
                        deadlineCts = new CancellationTokenSource();
                        UniTask deadlineTask = UniTask.Delay(
                            deadline.Value,
                            ignoreTimeScale: true,
                            cancellationToken: deadlineCts.Token);
                        int winner = await UniTask.WhenAny(physicalCompletion.Task, deadlineTask);
                        if (winner == 0)
                        {
                            // 及时撤掉 loser：否则每个快速响应都会让 deadline continuation 与 Body 多活到完整超时。
                            CancelOwnerSafely(deadlineCts, $"{method} {url} 超时计时器 owner");
                        }
                        else
                        {
                            owner.RequestTimeout();
                        }
                    }

                    // Provider 任务只有 observer 一个 awaiter；race signal 与 outcome 是两个独立 TCS，
                    // deadline 先赢后也不会在 pending UniTask 上注册第二个 continuation。
                    response = await outcome.Task;
                }
                finally
                {
                    if (deadlineCts != null)
                    {
                        CancelOwnerSafely(deadlineCts, $"{method} {url} 超时计时器 owner");
                        deadlineCts.Dispose();
                    }
                }

                // Adapter 可以在任意线程完成；主线程公共 API 的 continuation 不应继承 Adapter 的完成线程。
                await UniTask.SwitchToMainThread();
                if (owner.TimedOut)
                    throw CreateTimeoutException(timeout, method, url);
                return response ?? throw new NetworkException(NetworkErrorKind.ConnectionError,
                    $"HTTP provider 返回了 null response：{method} {url}");
            }
            catch (Exception e)
            {
                await UniTask.SwitchToMainThread();

                // 意图优先于 Adapter 的具体异常形态：有些传输在被取消/Dispose 时会抛 ODE 或 socket error。
                if (ct.IsCancellationRequested || _disposed)
                {
                    if (e is OperationCanceledException) throw;
                    throw new OperationCanceledException(
                        $"HTTP 请求已取消：{method} {url}", e,
                        ct.IsCancellationRequested ? ct : owner.Token);
                }

                if (owner.TimedOut)
                {
                    if (e is NetworkException { Kind: NetworkErrorKind.Timeout }) throw;
                    throw CreateTimeoutException(timeout, method, url, e);
                }

                // Provider 在 owner token 未取消时自发 OCE 不是外部取消，也绝不能冒充 timeout。
                if (e is OperationCanceledException)
                    throw new NetworkException(NetworkErrorKind.ConnectionError,
                        $"HTTP provider 在 token 未取消时终止了请求：{method} {url}", inner: e);
                if (e is NetworkException) throw;
                throw new NetworkException(NetworkErrorKind.ConnectionError,
                    $"HTTP provider 发送失败：{method} {url}（{e.GetType().Name}: {e.Message}）", inner: e);
            }
        }

        private static async UniTask ObserveProviderSend(
            UniTask<HttpResponse> providerTask,
            UniTaskCompletionSource<HttpResponse> outcome,
            UniTaskCompletionSource physicalCompletion)
        {
            try
            {
                outcome.TrySetResult(await providerTask);
            }
            catch (Exception e)
            {
                // 结果由 SendCore 在主线程统一分类；observer 自身永不把 Provider 异常变成 fire-and-forget。
                outcome.TrySetException(e);
            }
            finally
            {
                physicalCompletion.TrySetResult();
            }
        }

        private static void ValidateTimeout(float timeout, string paramName) =>
            _ = CreateDeadline(timeout, paramName);

        private static TimeSpan? CreateDeadline(float timeout, string paramName)
        {
            if (float.IsNaN(timeout) || float.IsInfinity(timeout))
                throw new ArgumentOutOfRangeException(paramName, timeout, "HTTP timeout 必须是有限秒数；<= 0 表示不限时。");
            if (timeout <= 0) return null;

            try
            {
                return TimeSpan.FromSeconds(timeout);
            }
            catch (OverflowException e)
            {
                throw new ArgumentOutOfRangeException(paramName, timeout,
                    $"HTTP timeout 超出 TimeSpan 可表示范围：{e.Message}");
            }
        }

        private static NetworkException CreateTimeoutException(
            float timeout, string method, string url, Exception inner = null) =>
            new(NetworkErrorKind.Timeout,
                $"请求超时（{timeout:0.#}s）：{method} {url}", inner: inner);

        private static void CancelOwnerSafely(CancellationTokenSource owner, string label)
        {
            try
            {
                owner.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Dispose 与迟到的外部取消并发时，owner 已经完成；无需重复清理。
            }
            catch (Exception e)
            {
                // CancellationTokenSource 已完成取消，只是某个注册回调抛了异常；记录后继续释放其它 owner。
                Log.Write(
                    LogLevel.Warning,
                    $"{label} 的取消回调抛出异常，已隔离；HTTP 清理将继续。",
                    category: nameof(HttpUtility),
                    exception: e);
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
