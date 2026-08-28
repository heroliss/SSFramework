using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Game.Framework.Logging;
using Game.Framework.Utility;

namespace Game.Framework.Demo.Modules.Services
{
    /// <summary>
    /// 网络 demo 章的内嵌离线服务器：HTTP 请求-响应（若干演示端点）+ WebSocket（echo + 每 2s 推 tick）。
    /// 让网络章无需外部后端即可整段跑通，所有按钮保持原子（无「先启服务器」前置条件）。
    /// </summary>
    public interface IDemoGameServer : IUtility
    {
        /// <summary>HTTP 基地址（已含实际端口），注册 IHttpUtility 时用。</summary>
        string HttpBaseUrl { get; }

        /// <summary>WebSocket 地址（ws://，已含实际端口），Connect 时用。</summary>
        string WsUrl { get; }

        /// <summary>服务器是否在运行（demo 的「停止服务器」按钮之后为 false，用来演示 ConnectionError）。</summary>
        bool IsRunning { get; }

        /// <summary>停止服务器（演示连接失败），同时关闭已建立的 WS 连接（客户端立刻收到意外断开事件）。Dispose 也会停。</summary>
        void Stop();
    }

    /// <summary>
    /// 内嵌服务器实现。<b>HTTP 用 <see cref="HttpListener"/></b>（回环免管理员权限；UnityWebRequest 经系统代理也能到 localhost）；
    /// <b>WebSocket 用 <see cref="TcpListener"/> + 手写 RFC6455</b>——Unity Editor 的 Mono 运行时
    /// <c>HttpListener</c> 的 WS 升级不可用（<c>IsWebSocketRequest</c> 恒 false，ADR-0028 §8），只能自己握手 + 编解帧。
    /// </summary>
    /// <remarks>
    /// 全部 IO 在后台线程 / 线程池（不碰任何 Unity API）；构造即启动、<see cref="Dispose"/> / <see cref="Stop"/> 停。
    /// 每个 WS 连接一个读循环 + 一个 2s tick 推送循环，两者写同一 socket 用锁串行（NetworkStream 不允许并发写）。
    /// </remarks>
    public sealed class DemoGameServer : IDemoGameServer, IDisposable
    {
        // WebSocket 握手魔术 GUID（RFC6455 固定常量）：Sec-WebSocket-Accept = Base64(SHA1(key + 此常量))。
        private const string WsMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly HttpListener _http;
        private readonly TcpListener _wsListener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _wsClients = new(); // 活跃 WS 连接（lock 保护）：Stop 时主动关闭，让客户端立刻收到断开
        private volatile bool _running;
        private bool _disposed;

        public string HttpBaseUrl { get; }
        public string WsUrl { get; }
        public bool IsRunning => _running;

        public DemoGameServer()
        {
            HttpListener http = null;
            TcpListener wsListener = null;
            try
            {
                int httpPort = FindFreeHttpPort(out http); // HttpListener 要试着 Start 才知道端口能不能用

                wsListener = new TcpListener(IPAddress.Loopback, 0); // 端口 0 = 让 OS 分配空闲端口
                wsListener.Start();
                int wsPort = ((IPEndPoint)wsListener.LocalEndpoint).Port;

                // 两个监听器都成功后才提交字段并发布 Running，调用方不会看到半启动对象。
                _http = http;
                _wsListener = wsListener;
                HttpBaseUrl = $"http://127.0.0.1:{httpPort}";
                WsUrl = $"ws://127.0.0.1:{wsPort}/";
                _running = true;
                _ = HttpAcceptLoop();
                _ = WsAcceptLoop();
            }
            catch
            {
                // 构造函数未返回时无人能调用 Dispose；按获取逆序回滚，且不能让清理异常覆盖启动根因。
                try { wsListener?.Stop(); } catch { }
                try { http?.Close(); } catch { }
                try { _cts.Cancel(); } catch { }
                try { _cts.Dispose(); } catch { }
                throw;
            }
        }

        public void Stop()
        {
            bool wasRunning = _running;
            _running = false;
            if (wasRunning)
            {
                try { _cts.Cancel(); }
                catch (Exception e) { Log.Error("停止 Demo 服务器时取消回调抛出异常；仍将继续关闭监听器。", e, "DemoServer"); }
            }
            try { if (_http.IsListening) _http.Stop(); }
            catch (Exception e) { Log.Error("停止 HTTP 监听器时发生异常；仍将继续清理 WebSocket。", e, "DemoServer"); }
            try { _wsListener.Stop(); }
            catch (Exception e) { Log.Error("停止 WebSocket 监听器时发生异常；仍将继续关闭活动客户端。", e, "DemoServer"); }

            // 停监听只挡新连接；已建立的 WS 连接的读循环阻塞在 ReadAsync 上（Mono 的 NetworkStream 不响应 ct），
            // 主动关 socket 才能让它退出——客户端侧立刻收到意外断开（WebSocketClosedEvent ByUser:false），也是演示的一部分。
            lock (_wsClients)
            {
                foreach (var client in _wsClients)
                    try { client.Close(); } catch { /* 已断 */ }
                _wsClients.Clear();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            try { _http.Close(); } catch { }
            try { _cts.Dispose(); } catch { }
        }

        // ── HTTP ────────────────────────────────────────────────────────────

        private static int FindFreeHttpPort(out HttpListener listener)
        {
            Exception lastError = null;
            for (int port = 18400; port < 18460; port++)
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.Start();
                    listener = candidate;
                    return port;
                }
                catch (HttpListenerException e)
                {
                    lastError = e;
                    candidate.Close(); // Windows/部分 Mono 运行时以 HttpListenerException 报端口冲突
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    lastError = e;
                    candidate.Close(); // Unity Mono 可能直接透出 SocketException；同样应尝试下一端口
                }
                catch
                {
                    candidate.Close();
                    throw;
                }
            }
            listener = null;
            throw new InvalidOperationException(
                "18400-18459 无可用端口，无法启动 demo HTTP 服务器。请检查残留 Unity/服务进程或调整端口范围。",
                lastError);
        }

        private async Task HttpAcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); }
                catch { break; } // Stop/Close 后 GetContextAsync 抛出 = 正常退出
                _ = Task.Run(() => HandleHttp(ctx));
            }
        }

        private static void HandleHttp(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                switch (req.Url.AbsolutePath)
                {
                    case "/api/login":
                        // 不解析请求体（避免后台线程用 JsonUtility）——固定发一个 token，演示「拿 token 后 SetHeader」
                        WriteJson(ctx.Response, 200, "{\"Token\":\"demo-token-abc123\",\"PlayerId\":1001}");
                        break;

                    case "/api/leaderboard":
                        if (string.IsNullOrEmpty(req.Headers["Authorization"]))
                        {
                            WriteJson(ctx.Response, 401, "{\"error\":\"缺少 Authorization 头——先点登录拿 token 再 SetHeader\"}");
                            break;
                        }
                        WriteJson(ctx.Response, 200, BuildLeaderboard(req.QueryString["count"]));
                        break;

                    case "/api/slow":
                    {
                        int ms = int.TryParse(req.QueryString["ms"], out int m) ? Math.Min(m, 20000) : 3000;
                        Thread.Sleep(ms); // 线程池线程，睡它演示超时 / 取消
                        WriteJson(ctx.Response, 200, "{\"message\":\"慢响应终于回来了\"}");
                        break;
                    }

                    case "/api/fail":
                    {
                        int code = int.TryParse(req.QueryString["code"], out int c) ? c : 500;
                        WriteJson(ctx.Response, code, $"{{\"error\":\"服务器按你的要求返回了 {code}\"}}");
                        break;
                    }

                    default:
                        WriteJson(ctx.Response, 404, "{\"error\":\"没有这个路由\"}");
                        break;
                }
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* 客户端已断（取消 / 超时的常态） */ }
            }
        }

        private static string BuildLeaderboard(string countParam)
        {
            string[] all = { "Alice - 9800", "Bob - 8600", "Carol - 7100", "Dave - 6400", "Erin - 5200" };
            int count = int.TryParse(countParam, out int n) ? Math.Clamp(n, 1, all.Length) : 3;
            var sb = new StringBuilder("{\"entries\":[");
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(all[i]).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void WriteJson(HttpListenerResponse resp, int status, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            resp.StatusCode = status;
            resp.ContentType = "application/json";
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.Close();
        }

        // ── WebSocket（TcpListener + 手写 RFC6455）───────────────────────────

        private async Task WsAcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = await _wsListener.AcceptTcpClientAsync(); }
                catch { break; } // Stop 后 AcceptTcpClientAsync 抛出 = 正常退出
                _ = Task.Run(() => HandleWsClient(client));
            }
        }

        private async Task HandleWsClient(TcpClient client)
        {
            lock (_wsClients)
            {
                if (!_running) { client.Close(); return; } // Stop 与 Accept 竞态：晚到的连接直接关
                _wsClients.Add(client);
            }
            var connCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var writeLock = new object();
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    if (!await DoHandshake(stream)) return;

                    // 推送循环：每 2s 发一条 tick（count++）。与读循环的 echo 写同一 socket，用 writeLock 串行。
                    int tick = 0;
                    _ = Task.Run(async () =>
                    {
                        while (!connCts.IsCancellationRequested)
                        {
                            try { await Task.Delay(2000, connCts.Token); }
                            catch { return; }
                            int n = Interlocked.Increment(ref tick);
                            string frame = $"{{\"type\":\"tick\",\"payload\":\"{{\\\"count\\\":{n}}}\"}}";
                            TryWriteText(stream, writeLock, frame);
                        }
                    });

                    // 读循环：收到文本帧原样回显（客户端注册的 "chat" 推送即收到回显）；收到 close 帧退出。
                    while (!connCts.IsCancellationRequested)
                    {
                        (int opcode, byte[] payload) = await ReadFrame(stream, connCts.Token);
                        if (opcode == -1) break; // 连接断开
                        if (opcode == 0x8) // close 帧：回一个 close 帧完成握手（否则客户端优雅关闭会报错），再退出
                        {
                            TryWriteFrame(stream, writeLock, new byte[] { 0x88, 0x00 });
                            break;
                        }
                        if (opcode == 0x1) // 文本帧：回显
                            TryWriteText(stream, writeLock, Encoding.UTF8.GetString(payload));
                    }
                }
            }
            catch { /* 连接异常断开：静默收尾 */ }
            finally
            {
                connCts.Cancel();
                connCts.Dispose();
                lock (_wsClients) _wsClients.Remove(client);
            }
        }

        // RFC6455 握手：读 HTTP 升级请求头 → 算 Sec-WebSocket-Accept → 回 101。
        private static async Task<bool> DoHandshake(NetworkStream stream)
        {
            string headers = await ReadHttpHeaders(stream);
            string key = ExtractHeader(headers, "Sec-WebSocket-Key");
            if (string.IsNullOrEmpty(key)) return false;

            string accept;
            using (var sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.UTF8.GetBytes(key + WsMagicGuid)));

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            return true;
        }

        private static async Task<string> ReadHttpHeaders(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            // 逐字节读到空行（\r\n\r\n）为止——请求头很短；结尾判断只看尾部 4 个字符，别用 ToString().EndsWith（每字节整串重建）
            while (sb.Length < 4096)
            {
                int read = await stream.ReadAsync(buf, 0, 1);
                if (read == 0) break;
                sb.Append((char)buf[0]);
                if (sb.Length >= 4 &&
                    sb[sb.Length - 4] == '\r' && sb[sb.Length - 3] == '\n' &&
                    sb[sb.Length - 2] == '\r' && sb[sb.Length - 1] == '\n')
                    break;
            }
            return sb.ToString();
        }

        private static string ExtractHeader(string headers, string name)
        {
            foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(colon + 1).Trim();
            }
            return null;
        }

        // 读一个客户端帧（客户端帧必然带掩码）。返回 (opcode, 解掩码后的 payload)；断开返回 (-1, null)。
        private static async Task<(int opcode, byte[] payload)> ReadFrame(NetworkStream stream, CancellationToken ct)
        {
            byte[] head = await ReadExact(stream, 2, ct);
            if (head == null) return (-1, null);

            int opcode = head[0] & 0x0F;
            bool masked = (head[1] & 0x80) != 0;
            long len = head[1] & 0x7F;
            if (len == 126)
            {
                byte[] ext = await ReadExact(stream, 2, ct);
                if (ext == null) return (-1, null);
                len = (ext[0] << 8) | ext[1];
            }
            else if (len == 127)
            {
                byte[] ext = await ReadExact(stream, 8, ct);
                if (ext == null) return (-1, null);
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
            }

            byte[] mask = masked ? await ReadExact(stream, 4, ct) : null;
            byte[] payload = await ReadExact(stream, (int)len, ct);
            if (payload == null) return (-1, null);
            if (masked)
                for (int i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i % 4];
            return (opcode, payload);
        }

        // 从流读满 count 字节；流关闭（读到 0）返回 null。
        private static async Task<byte[]> ReadExact(NetworkStream stream, int count, CancellationToken ct)
        {
            var buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int read = await stream.ReadAsync(buf, off, count - off, ct);
                if (read == 0) return null;
                off += read;
            }
            return buf;
        }

        // 写一个服务器文本帧（不带掩码，服务器→客户端）。writeLock 串行化 echo 与 tick 推送对同一 socket 的写。
        private static void TryWriteText(NetworkStream stream, object writeLock, string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            using var ms = new MemoryStream();
            ms.WriteByte(0x81); // FIN + 文本 opcode
            if (payload.Length < 126)
            {
                ms.WriteByte((byte)payload.Length);
            }
            else // demo 消息不会超过 64KB，只需处理 126（2 字节长度）分支
            {
                ms.WriteByte(126);
                ms.WriteByte((byte)((payload.Length >> 8) & 0xFF));
                ms.WriteByte((byte)(payload.Length & 0xFF));
            }
            ms.Write(payload, 0, payload.Length);
            TryWriteFrame(stream, writeLock, ms.ToArray());
        }

        // 把一整个已编码好的帧写进 socket，writeLock 串行化并发写（echo / tick / close 都走这里）。
        private static void TryWriteFrame(NetworkStream stream, object writeLock, byte[] frame)
        {
            try
            {
                lock (writeLock)
                    stream.Write(frame, 0, frame.Length);
            }
            catch { /* 连接已断，忽略 */ }
        }
    }
}
