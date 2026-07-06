using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Game.Framework.Test
{
    /// <summary>
    /// 网络集成测试用的回环 HTTP 服务器（HttpListener，127.0.0.1 免管理员权限）。
    /// 端点：GET /hello（固定 JSON）、POST /echo（原样回显请求体）、GET /fail?code=N（指定状态码）、
    /// GET /headers（把 Authorization / X-Custom 请求头回显进 JSON）、GET /slow?ms=N（延迟响应，测超时/取消）。
    /// </summary>
    /// <remarks>
    /// 请求在线程池处理（不碰任何 Unity API）；端口从 18200 顺延扫描避免占用冲突。
    /// Dispose 停止监听并使在途 GetContextAsync 抛出（接受循环借此退出）。
    /// </remarks>
    internal sealed class NetworkTestServer : IDisposable
    {
        private readonly HttpListener _listener;

        public int Port { get; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public NetworkTestServer()
        {
            for (int port = 18200; port < 18260; port++)
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.Start();
                    _listener = candidate;
                    Port = port;
                    break;
                }
                catch (HttpListenerException)
                {
                    ((IDisposable)candidate).Dispose(); // 端口被占，换下一个
                }
            }
            if (_listener == null) throw new InvalidOperationException("18200-18259 无可用端口，无法启动测试服务器。");
            _ = AcceptLoop();
        }

        public void Dispose()
        {
            if (_listener.IsListening) _listener.Stop();
            _listener.Close();
        }

        private async Task AcceptLoop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; } // Stop/Close 后 GetContextAsync 抛出 = 正常退出
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                switch (req.Url.AbsolutePath)
                {
                    case "/hello":
                        Write(ctx.Response, 200, "{\"message\":\"hello\",\"value\":42}");
                        break;

                    case "/echo":
                    {
                        byte[] body = ReadAll(req);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = req.ContentType;
                        ctx.Response.ContentLength64 = body.Length;
                        ctx.Response.OutputStream.Write(body, 0, body.Length);
                        ctx.Response.Close();
                        break;
                    }

                    case "/fail":
                    {
                        int code = int.TryParse(req.QueryString["code"], out int c) ? c : 500;
                        Write(ctx.Response, code, "{\"error\":\"server says no\"}");
                        break;
                    }

                    case "/headers":
                    {
                        string auth = req.Headers["Authorization"] ?? "";
                        string custom = req.Headers["X-Custom"] ?? "";
                        Write(ctx.Response, 200, $"{{\"auth\":\"{auth}\",\"custom\":\"{custom}\"}}");
                        break;
                    }

                    case "/slow":
                    {
                        int ms = int.TryParse(req.QueryString["ms"], out int m) ? m : 3000;
                        System.Threading.Thread.Sleep(ms); // 线程池线程，睡它没关系
                        Write(ctx.Response, 200, "{\"message\":\"finally\",\"value\":1}");
                        break;
                    }

                    default:
                        Write(ctx.Response, 404, "{\"error\":\"no such route\"}");
                        break;
                }
            }
            catch
            {
                // 客户端提前断开（取消/超时测试的常态）——服务器侧静默即可
                try { ctx.Response.Abort(); } catch { /* 已关闭 */ }
            }
        }

        private static byte[] ReadAll(HttpListenerRequest req)
        {
            using var ms = new System.IO.MemoryStream();
            req.InputStream.CopyTo(ms);
            return ms.ToArray();
        }

        private static void Write(HttpListenerResponse resp, int status, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            resp.StatusCode = status;
            resp.ContentType = "application/json";
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.Close();
        }
    }
}
