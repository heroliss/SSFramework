using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Network;
using NUnit.Framework;
using UnityEngine.TestTools;
using HttpUtility = Game.Framework.Network.HttpUtility;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证 HTTP 门面（ADR-0028）单元路径：经 FakeHttpProvider（接缝第二实现兼测试桩）覆盖
    /// URL 拼接 / 头合并 / 失败分级（HttpError / Timeout / DeserializeError vs 外部 OCE）/ 逃生舱宽容语义 / Dispose。
    /// </summary>
    public class HttpTests
    {
        [Serializable]
        private class LoginReq
        {
            public string User;
            public string Password;
        }

        [Serializable]
        private class LoginResp
        {
            public string Token;
            public int PlayerId;
        }

        /// <summary>可编程传输桩：记录最后一次请求的全部参数，响应/异常由 Handler 决定（默认 200 空体）。</summary>
        private sealed class FakeHttpProvider : IHttpProvider
        {
            public string LastUrl, LastMethod, LastContentType;
            public byte[] LastBody;
            public List<KeyValuePair<string, string>> LastHeaders;
            public Func<CancellationToken, UniTask<HttpResponse>> Handler;
            public bool Disposed;

            public UniTask<HttpResponse> SendAsync(string url, string method, byte[] body, string contentType,
                IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken ct)
            {
                LastUrl = url;
                LastMethod = method;
                LastBody = body;
                LastContentType = contentType;
                LastHeaders = headers == null ? null : new List<KeyValuePair<string, string>>(headers);
                return Handler != null ? Handler(ct) : UniTask.FromResult(Json(200, null));
            }

            public void Dispose() => Disposed = true;

            public static HttpResponse Json(int status, string json) => new HttpResponse
            {
                StatusCode = status,
                Body = json == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json),
            };
        }

        private FakeHttpProvider _fake;
        private HttpUtility _http;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeHttpProvider();
            _http = new HttpUtility("https://api.test/", _fake); // 尾部 / 应被规整掉
        }

        [TearDown]
        public void TearDown() => _http.Dispose();

        [UnityTest]
        public IEnumerator Post_Roundtrip_SerializesRequest_DeserializesResponse() => UniTask.ToCoroutine(async () =>
        {
            _fake.Handler = _ => UniTask.FromResult(FakeHttpProvider.Json(200, "{\"Token\":\"abc\",\"PlayerId\":7}"));

            var resp = await _http.Post<LoginReq, LoginResp>("api/login", new LoginReq { User = "hero", Password = "pw" });

            Assert.AreEqual("https://api.test/api/login", _fake.LastUrl); // BaseUrl 去尾 / + 相对 path 拼接
            Assert.AreEqual("POST", _fake.LastMethod);
            Assert.AreEqual("application/json", _fake.LastContentType); // Content-Type 随 serializer 走
            StringAssert.Contains("\"User\":\"hero\"", Encoding.UTF8.GetString(_fake.LastBody));
            Assert.AreEqual("abc", resp.Token);
            Assert.AreEqual(7, resp.PlayerId);
        });

        [UnityTest]
        public IEnumerator Get_UrlResolution_SlashPathAndAbsoluteUrl() => UniTask.ToCoroutine(async () =>
        {
            await _http.Get<LoginResp>("/api/rank"); // 带头斜杠的相对 path：不应双斜杠
            Assert.AreEqual("https://api.test/api/rank", _fake.LastUrl);

            await _http.Get<LoginResp>("http://other.host/x"); // 绝对 URL 原样直通，无视 BaseUrl
            Assert.AreEqual("http://other.host/x", _fake.LastUrl);
        });

        [UnityTest]
        public IEnumerator Get_RelativePath_WithoutBaseUrl_ThrowsArgument() => UniTask.ToCoroutine(async () =>
        {
            // 异步门面的校验异常被捕获进 UniTask（同 StorageUtility 惯例），await + try/catch 断言、不用 Assert.Throws。
            using var noBase = new HttpUtility(provider: new FakeHttpProvider());
            try
            {
                await noBase.Get<LoginResp>("api/x");
                Assert.Fail("相对 path 无 BaseUrl 应抛 ArgumentException");
            }
            catch (ArgumentException) { /* 预期：代码写错了，fail-fast */ }
        });

        [UnityTest]
        public IEnumerator SetHeader_DefaultsMerge_PerRequestOverrides_NullRemoves() => UniTask.ToCoroutine(async () =>
        {
            _http.SetHeader("Authorization", "Bearer t1");
            _http.SetHeader("X-Custom", "base");

            // 同名头由编排层合并去重（不区分大小写、每请求头胜出）——provider 收到的列表无重复项
            await _http.Send(new HttpRequest
            {
                Path = "api/x",
                Headers = new Dictionary<string, string> { ["x-custom"] = "override" },
            });
            Assert.AreEqual(2, _fake.LastHeaders.Count);
            Assert.AreEqual("Bearer t1", Find("Authorization"));
            Assert.AreEqual("override", Find("X-Custom")); // 每请求头覆盖默认头（大小写不同也算同名）

            // null 值移除默认头
            _http.SetHeader("Authorization", null);
            await _http.Get<LoginResp>("api/x");
            Assert.IsFalse(_fake.LastHeaders.Exists(h => h.Key == "Authorization"));

            string Find(string name) => _fake.LastHeaders.Find(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        });

        [UnityTest]
        public IEnumerator Get_Non2xx_ThrowsHttpError_WithStatusAndBody() => UniTask.ToCoroutine(async () =>
        {
            _fake.Handler = _ => UniTask.FromResult(FakeHttpProvider.Json(404, "{\"error\":\"nope\"}"));
            try
            {
                await _http.Get<LoginResp>("api/missing");
                Assert.Fail("非 2xx 应抛 NetworkException");
            }
            catch (NetworkException e)
            {
                Assert.AreEqual(NetworkErrorKind.HttpError, e.Kind);
                Assert.AreEqual(404, e.StatusCode);
                StringAssert.Contains("nope", e.ResponseBody);
            }
        });

        [UnityTest]
        public IEnumerator Get_Empty2xxBody_ReturnsNull() => UniTask.ToCoroutine(async () =>
        {
            var resp = await _http.Get<LoginResp>("api/nothing"); // 默认 Handler：200 空体
            Assert.IsNull(resp);
        });

        [UnityTest]
        public IEnumerator Get_MalformedBody_ThrowsDeserializeError() => UniTask.ToCoroutine(async () =>
        {
            _fake.Handler = _ => UniTask.FromResult(FakeHttpProvider.Json(200, "not-json###"));
            try
            {
                await _http.Get<LoginResp>("api/bad");
                Assert.Fail("坏响应体应抛 NetworkException");
            }
            catch (NetworkException e)
            {
                Assert.AreEqual(NetworkErrorKind.DeserializeError, e.Kind);
            }
        });

        [UnityTest]
        public IEnumerator InternalTimeout_FoldsToTimeout_ExternalCancel_StaysOCE() => UniTask.ToCoroutine(async () =>
        {
            // 挂起到取消为止的传输：让超时计时 / 外部取消成为唯一出路
            _fake.Handler = ct => UniTask.Never<HttpResponse>(ct);

            // 内部超时 → Timeout（网络环境问题，可提示重试）
            using (var shortTimeout = new HttpUtility("https://api.test", _fake, defaultTimeoutSeconds: 0.05f))
            {
                try
                {
                    await shortTimeout.Get<LoginResp>("api/slow");
                    Assert.Fail("应超时");
                }
                catch (NetworkException e)
                {
                    Assert.AreEqual(NetworkErrorKind.Timeout, e.Kind);
                }
            }

            // 外部取消 → OCE 原样抛（调用方意图，不包装）
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            try
            {
                await _http.Get<LoginResp>("api/slow", cts.Token);
                Assert.Fail("应被取消");
            }
            catch (OperationCanceledException) { /* 预期：不是 NetworkException */ }
        });

        [UnityTest]
        public IEnumerator ProviderConnectionError_PropagatesUnchanged() => UniTask.ToCoroutine(async () =>
        {
            // 传输层失败（DNS/拒连/断网）由 provider 判定并抛 NetworkException(ConnectionError)，门面原样上抛、不吞不改 Kind。
            // 刻意用 Fake 断言这条契约、不做真实网络集成：UnityWebRequest→ConnectionError 的映射依赖真实网络条件，
            // 且会被拦截式代理污染——代理把「连接拒绝」变成 502 错误页（→ HttpError），任何环境都测不稳（映射本身见 provider 注释）。
            _fake.Handler = _ => throw new NetworkException(NetworkErrorKind.ConnectionError, "no route to host");
            try
            {
                await _http.Get<LoginResp>("api/x");
                Assert.Fail("provider 的 ConnectionError 应原样上抛");
            }
            catch (NetworkException e)
            {
                Assert.AreEqual(NetworkErrorKind.ConnectionError, e.Kind);
            }
        });

        [UnityTest]
        public IEnumerator Send_Non2xx_ReturnsResponse_NoThrow() => UniTask.ToCoroutine(async () =>
        {
            _fake.Handler = _ => UniTask.FromResult(FakeHttpProvider.Json(500, "{\"error\":\"boom\"}"));

            var resp = await _http.Send(new HttpRequest { Method = "DELETE", Path = "api/thing" });

            Assert.AreEqual("DELETE", _fake.LastMethod);
            Assert.IsFalse(resp.IsSuccess); // 逃生舱：交换完成即返回，状态码自己看
            Assert.AreEqual(500, resp.StatusCode);
            StringAssert.Contains("boom", resp.BodyText);
        });

        [UnityTest]
        public IEnumerator Dispose_CancelsInFlight_AndRejectsNewCalls() => UniTask.ToCoroutine(async () =>
        {
            _fake.Handler = ct => UniTask.Never<HttpResponse>(ct);
            var inflight = _http.Get<LoginResp>("api/slow");

            _http.Dispose();
            try
            {
                await inflight;
                Assert.Fail("在途请求应被 Dispose 取消");
            }
            catch (OperationCanceledException) { /* 预期：宿主释放 = 取消语义 */ }

            try
            {
                await _http.Get<LoginResp>("api/x");
                Assert.Fail("Dispose 后调用应抛 ObjectDisposedException");
            }
            catch (ObjectDisposedException) { /* 预期 */ }
            Assert.IsTrue(_fake.Disposed, "utility Dispose 应级联释放 provider");
        });

        [UnityTest]
        public IEnumerator Post_NullBody_ThrowsArgumentNull() => UniTask.ToCoroutine(async () =>
        {
            try
            {
                await _http.Post<LoginReq, LoginResp>("api/login", null);
                Assert.Fail("Post(null) 应抛 ArgumentNullException");
            }
            catch (ArgumentNullException) { /* 预期 */ }
        });
    }

    /// <summary>
    /// HTTP 端到端集成：真 UnityWebRequest 传输打回环 HttpListener 服务器（NetworkTestServer），
    /// 验证「编排层 + 默认传输 + 真 HTTP 协议栈」整链——单元路径的 Fake 桩替代不了状态行 / 头 / 体的真实往返。
    /// </summary>
    public class HttpIntegrationTests
    {
        [Serializable]
        private class Hello
        {
            public string message;
            public int value;
        }

        [Serializable]
        private class HeaderEcho
        {
            public string auth;
            public string custom;
        }

        private NetworkTestServer _server;
        private HttpUtility _http;

        [SetUp]
        public void SetUp()
        {
            _server = new NetworkTestServer();
            _http = new HttpUtility(_server.BaseUrl);
        }

        [TearDown]
        public void TearDown()
        {
            _http.Dispose();
            _server.Dispose();
        }

        [UnityTest]
        public IEnumerator Get_And_PostEcho_RoundtripOverRealHttp() => UniTask.ToCoroutine(async () =>
        {
            var hello = await _http.Get<Hello>("hello");
            Assert.AreEqual("hello", hello.message);
            Assert.AreEqual(42, hello.value);

            var echoed = await _http.Post<Hello, Hello>("echo", new Hello { message = "ping", value = 1 });
            Assert.AreEqual("ping", echoed.message);
        });

        [UnityTest]
        public IEnumerator Get404_ThrowsHttpError_SendReturnsIt() => UniTask.ToCoroutine(async () =>
        {
            try
            {
                await _http.Get<Hello>("fail?code=404");
                Assert.Fail("404 应抛 HttpError");
            }
            catch (NetworkException e)
            {
                Assert.AreEqual(NetworkErrorKind.HttpError, e.Kind);
                Assert.AreEqual(404, e.StatusCode);
            }

            var resp = await _http.Send(new HttpRequest { Path = "fail?code=404" });
            Assert.AreEqual(404, resp.StatusCode); // 同一状况逃生舱不抛
        });

        [UnityTest]
        public IEnumerator DefaultHeader_ArrivesAtServer() => UniTask.ToCoroutine(async () =>
        {
            _http.SetHeader("Authorization", "Bearer tok-123");
            var echo = await _http.Send(new HttpRequest
            {
                Path = "headers",
                Headers = new Dictionary<string, string> { ["X-Custom"] = "hi" },
            });
            var parsed = UnityEngine.JsonUtility.FromJson<HeaderEcho>(echo.BodyText);
            Assert.AreEqual("Bearer tok-123", parsed.auth);
            Assert.AreEqual("hi", parsed.custom);
        });

        // 注：不在此做「连接失败 → ConnectionError」的真实网络集成测试——UnityWebRequest 的传输层错误映射
        // 依赖真实网络条件，且被拦截式系统代理污染（代理把「连接拒绝」应答成 502 → HttpError），任何环境都不稳。
        // 该映射的契约在 HttpTests.ProviderConnectionError_PropagatesUnchanged 用 Fake 确定性验证。
    }
}
