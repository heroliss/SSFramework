using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace Game.Framework.Network
{
    /// <summary>
    /// 默认 HTTP 传输：UnityWebRequest 包装。走引擎异步操作、全程主线程（不下线程池），
    /// 因此是唯一全平台（含 WebGL）可用的传输——换 BestHTTP / HttpClient 时本类是接口契约的参照实现。
    /// </summary>
    /// <remarks>
    /// UnityWebRequest 结果到接口契约的映射（UniTask 的 <c>ToUniTask</c> 对非 Success 结果抛
    /// <see cref="UnityWebRequestException"/>，这里按类型分流）：
    /// <list type="bullet">
    ///   <item><c>ProtocolError</c>（4xx/5xx，HTTP 交换已完成）→ 正常返回 <see cref="HttpResponse"/>——状态码语义归上层。</item>
    ///   <item><c>ConnectionError</c> / <c>DataProcessingError</c> → <see cref="NetworkException"/>（ConnectionError）。</item>
    ///   <item>ct 取消 → ToUniTask 内部 Abort 请求并抛 OCE（符合接口「中止在途请求」要求）。</item>
    /// </list>
    /// 每请求 new 一个 UnityWebRequest 并 using 释放（UWR 不可复用）；本类无状态，Dispose 为 no-op。
    /// </remarks>
    public sealed class UnityWebRequestHttpProvider : IHttpProvider
    {
        public async UniTask<HttpResponse> SendAsync(string url, string method, byte[] body, string contentType,
            IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken ct)
        {
            using var req = new UnityWebRequest(url, method);
            req.downloadHandler = new DownloadHandlerBuffer();
            if (body != null && body.Length > 0)
            {
                req.uploadHandler = new UploadHandlerRaw(body);
                if (!string.IsNullOrEmpty(contentType))
                    req.uploadHandler.contentType = contentType;
            }
            if (headers != null)
                foreach (var h in headers)
                    req.SetRequestHeader(h.Key, h.Value); // 列表已由编排层合并去重，无同名项

            try
            {
                await req.SendWebRequest().ToUniTask(cancellationToken: ct);
            }
            catch (UnityWebRequestException e)
            {
                if (req.result == UnityWebRequest.Result.ProtocolError)
                    return BuildResponse(req); // 4xx/5xx：交换已完成，按契约返回不抛
                throw new NetworkException(NetworkErrorKind.ConnectionError,
                    $"连接失败：{method} {url}（{req.error}）", inner: e);
            }

            return BuildResponse(req);
        }

        public void Dispose() { } // 无持久连接 / 无缓存，无需清理

        private static HttpResponse BuildResponse(UnityWebRequest req)
        {
            // downloadHandler.data 在无响应体时可能为 null——契约要求 Body 永不为 null
            byte[] data = req.downloadHandler.data;
            return new HttpResponse
            {
                StatusCode = (int)req.responseCode,
                Body = data ?? Array.Empty<byte>(),
                Headers = (IReadOnlyDictionary<string, string>)req.GetResponseHeaders()
                          ?? new Dictionary<string, string>(),
            };
        }
    }
}
