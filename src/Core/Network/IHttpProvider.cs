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
    ///         状态码语义归上层，传输层不做判断。返回对象、<see cref="HttpResponse.Body"/> 与
    ///         <see cref="HttpResponse.Headers"/> 均不得为 null；空体 / 无响应头分别返回空数组 / 空字典。</item>
    ///   <item>传输失败（DNS / 拒连 / 网络断）→ 抛 <see cref="NetworkException"/>（ConnectionError）。</item>
    ///   <item>ct 取消 → 中止在途请求并抛 <see cref="OperationCanceledException"/>。
    ///         超时不归 provider——utility 的 request owner 会取消该 token；实现不得在取消回调中抛异常。
    ///         Utility 会隔离违规回调，避免其逃逸到调用方 CTS / timer 线程，但 Adapter 仍须让物理请求尽快到终态。</item>
    ///   <item>headers 已由编排层合并去重（无同名项，默认头与每请求头的覆盖在上游完成），实现照列表逐个设置即可。</item>
    ///   <item>通常从主线程调用；实现允许在任意线程完成。Utility 会在完成公共调用前恢复 Unity 主线程，
    ///         自定义 HttpClient / BestHTTP Adapter 不需要伪造主线程 continuation。</item>
    /// </list>
    /// </remarks>
    public interface IHttpProvider : IDisposable
    {
        /// <summary>
        /// 发送一份已完成 URL 解析、请求头合并与内容编码的请求快照。
        /// 输入集合和字节数组在返回任务到达终态前只借给 Provider，Provider 不得在终态后继续持有或修改；
        /// HTTP 状态码均通过非 null <see cref="HttpResponse"/> 返回，只有传输失败与取消通过异常表达。
        /// </summary>
        UniTask<HttpResponse> SendAsync(string url, string method, byte[] body, string contentType,
            IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken ct);
    }
}
