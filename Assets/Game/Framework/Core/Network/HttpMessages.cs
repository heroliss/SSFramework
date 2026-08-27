using System;
using System.Collections.Generic;
using System.Text;

namespace Game.Framework.Network
{
    /// <summary>
    /// <see cref="IHttpUtility.Send"/> 逃生舱的请求描述：任意动词 / raw 字节体 / 每请求头 / 每请求超时。
    /// 动词门面（Get / Post）覆盖不了的形态都从这里走——PUT、DELETE、非 JSON 体、自定义 Content-Type 等。
    /// </summary>
    public sealed class HttpRequest
    {
        /// <summary>HTTP 动词（大写惯例："GET" / "POST" / "PUT" / "DELETE"…）。</summary>
        public string Method = "GET";

        /// <summary>相对 BaseUrl 的路径（query 直接写在里面），或 <c>http(s)://</c> 开头的绝对地址。</summary>
        public string Path;

        /// <summary>请求体原始字节；null = 无请求体。已序列化对象体自己先过 serializer。</summary>
        public byte[] Body;

        /// <summary>请求体的 Content-Type；null 且有 Body 时取 serializer 的 <see cref="INetworkSerializer.ContentType"/>。</summary>
        public string ContentType;

        /// <summary>附加请求头，叠加在 <see cref="IHttpUtility.SetHeader"/> 默认头之上（同名覆盖）；null = 无附加。</summary>
        public Dictionary<string, string> Headers;

        /// <summary>本次请求的有限超时秒数；null = 用 utility 默认值，&lt;=0 = 不限时；NaN / Infinity / 超出 TimeSpan 范围会在发送前 fail-fast。</summary>
        public float? TimeoutSeconds;
    }

    /// <summary>
    /// <see cref="IHttpUtility.Send"/> 的响应。逃生舱语义：只要 HTTP 交换完成（含 4xx/5xx）就返回本对象不抛，
    /// 调用方查 <see cref="IsSuccess"/> / <see cref="StatusCode"/> 自行分支——与动词门面「非 2xx 抛」形成分工。
    /// </summary>
    public sealed class HttpResponse
    {
        public int StatusCode;

        /// <summary>响应体原始字节。永不为 null（空体 = 空数组），文本内容用 <see cref="BodyText"/>。</summary>
        public byte[] Body = Array.Empty<byte>();

        /// <summary>响应头（key 大小写保留传输层原样）；可能为空字典，不为 null。</summary>
        public IReadOnlyDictionary<string, string> Headers = EmptyHeaders;

        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

        /// <summary>响应体按 UTF-8 解码的文本（惰性、缓存）。空体返回空串。</summary>
        public string BodyText => _bodyText ??= Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Body);

        private string _bodyText;

        private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();
    }
}
