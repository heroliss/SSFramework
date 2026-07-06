using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Network
{
    /// <summary>
    /// HTTP 传输接缝：发送一次已完全解析的请求（绝对 URL、已合并头），返回原始响应。
    /// 默认 <see cref="UnityWebRequestHttpProvider"/>（全平台含 WebGL）；换 BestHTTP / HttpClient 等
    /// 只需实现本接口经 <see cref="HttpUtility"/> 构造注入。适配层保留 Async 后缀（同 IAssetProvider 惯例）。ADR-0028 §5。
    /// </summary>
    /// <remarks>
    /// 实现契约：
    /// <list type="bullet">
    ///   <item>HTTP 交换完成（<b>任何</b>状态码，含 4xx/5xx）→ 返回 <see cref="HttpResponse"/> 不抛——
    ///         状态码语义归上层，传输层不做判断。</item>
    ///   <item>传输失败（DNS / 拒连 / 网络断）→ 抛 <see cref="NetworkException"/>（ConnectionError）。</item>
    ///   <item>ct 取消 → 中止在途请求并抛 <see cref="OperationCanceledException"/>。
    ///         超时不归 provider——utility 已把超时计时链进 ct，实现只需尊重取消。</item>
    ///   <item>headers 已由编排层合并去重（无同名项，默认头与每请求头的覆盖在上游完成），实现照列表逐个设置即可。</item>
    ///   <item>主线程调用、主线程回返（回调后要触碰框架的调用链依赖这一点）。</item>
    /// </list>
    /// </remarks>
    public interface IHttpProvider : IDisposable
    {
        UniTask<HttpResponse> SendAsync(string url, string method, byte[] body, string contentType,
            IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken ct);
    }
}
