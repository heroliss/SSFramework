using System;
using System.Collections.Generic;
using System.Threading;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Demo.Modules.Services;
using Game.Framework.Event;
using Game.Framework.Network;
using UnityEngine.UIElements;
using HttpUtility = Game.Framework.Network.HttpUtility;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·网络：请求-响应（<see cref="IHttpUtility"/>，UniTask 返回值）+ 长连接推送（<see cref="IWebSocketUtility"/>，
    /// 推送转框架 Event）双轨。内嵌离线服务器（<see cref="DemoGameServer"/>）让整章无需外部后端即可跑通。ADR-0028。
    /// </summary>
    public sealed class NetworkDemoModule : DemoModuleBase
    {
        public override string Id => "network";
        public override string Title => "网络 · HTTP 与 WebSocket";
        public override string Category => "能力";
        public override int Order => 95;
        public override string Summary =>
            "消息建模双轨：请求-响应 = UniTask 返回值（Get/Post，非 2xx 抛 NetworkException）；服务器推送 = 转框架 Event" +
            "（RegisterPush 映射，Bag.Subscribe 消费）。传输/序列化双接缝可插拔，默认 UnityWebRequest + ClientWebSocket + JSON。ADR-0028。";

        // ── 演示用消息类型（请求/响应是 class；推送事件是 [Serializable] struct + 公共字段）──

        [Serializable] private class LoginReq { public string User; public string Password; }
        [Serializable] private class LoginResp { public string Token; public int PlayerId; }
        [Serializable] private class Leaderboard { public List<string> entries; }
        [Serializable] private class SlowResp { public string message; }
        [Serializable] private class ChatOutbound { public string Value; } // Send<T> 的 payload 需为 class

        /// <summary>服务器每 2s 推送的 tick（type="tick"）。⚠ 推送事件用公共字段、不用 record 位置参数（JsonUtility 只认字段）。</summary>
        [Serializable] private struct ServerTickEvent : IEvent { public int count; }

        /// <summary>客户端发出的 chat 被服务器原样回显（type="chat"）→ 转成此事件。</summary>
        [Serializable] private struct ChatEchoEvent : IEvent { public string Value; }

        /// <summary>
        /// 注册三个网络服务 + 推送映射（都在 InstallBindings 做一次）：
        /// 服务器构造即启动、拿到实际端口 → 用它构造 HttpUtility（3s 超时便于演示超时）；
        /// WebSocketUtility 的 RegisterPush 在这里配好（连接前后均可注册，放这里避免 Build 重入时重复注册抛异常）。
        /// </summary>
        public override void InstallBindings(ContainerBuilder builder)
        {
            var server = new DemoGameServer();
            builder.RegisterOwned(server, typeof(IDemoGameServer));
            builder.RegisterOwned(new HttpUtility(server.HttpBaseUrl, defaultTimeoutSeconds: 3f), typeof(IHttpUtility));

            var ws = new WebSocketUtility();
            ws.RegisterPush<ServerTickEvent>("tick"); // 服务器周期推送
            ws.RegisterPush<ChatEchoEvent>("chat");   // 服务器回显客户端消息
            builder.RegisterOwned(ws, typeof(IWebSocketUtility));
        }

        public override void Build(DemoModuleHost host)
        {
            var server = this.GetUtility<IDemoGameServer>();
            var http = this.GetUtility<IHttpUtility>();
            var ws = this.GetUtility<IWebSocketUtility>();

            // ── 定位 ──
            host.AddSectionTitle("定位：消息建模双轨");
            host.AddNote("网络消息分两类，用两种最贴合因果的形态建模：**请求-响应**（发起方等结果）= `await http.Post&lt;Req,Resp&gt;(...)` **UniTask 返回值**，不硬塞进事件；**服务器推送/广播**（谁都可能收到）= 转框架 **Event**，`Bag.Subscribe&lt;T&gt;` 消费，与订 Model 事件同一套心智。",
                new CodeRef("Assets/Game/Framework/Core/Network/IHttpUtility.cs", "public interface IHttpUtility", "HTTP 门面契约"));
            host.AddSubNote("传输与序列化是两个接缝：默认 HTTP=UnityWebRequest（全平台含 WebGL）、WS=ClientWebSocket、格式=JSON；换 BestHTTP / Protobuf / MemoryPack 只换 provider / serializer，业务零改动。本章服务器是内嵌离线的（HTTP 用 HttpListener、WS 用 TcpListener + 手写 RFC6455——Mono 的 HttpListener 做不了 WS 服务端），点按钮即可跑通、无需外部后端。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Services/DemoGameServer.cs", "class DemoGameServer", "内嵌演示服务器"));

            // ── 服务器状态 ──
            host.AddSectionTitle("内嵌服务器");
            var serverLabel = host.AddValueDisplay();
            serverLabel.style.whiteSpace = WhiteSpace.Normal;
            void RefreshServer() => serverLabel.text = server.IsRunning
                ? $"运行中 ✓  HTTP={server.HttpBaseUrl}  WS={server.WsUrl}"
                : "已停止 ✗（下面任何请求 / 连接都会得到 ConnectionError）";
            RefreshServer();
            host.AddActionRow("停止服务器（演示连接失败）", () =>
            {
                server.Stop();
                RefreshServer();
            }, CodeRef.Here("server.Stop()", "停止"));
            host.AddSubNote("停掉服务器后，再点下面的 HTTP / WS 按钮，会看到失败按 Kind 分级：连不上 = ConnectionError。");

            BuildHttpSection(host, http);
            BuildWebSocketSection(host, server, ws);

            // ── 扩展点与刻意不做 ──
            host.AddSectionTitle("扩展点与刻意不做");
            host.AddConcept("换传输 = IHttpProvider / IWebSocketProvider", "BestHTTP（WebGL 的 WS / HTTP2 / SignalR）、HttpClient 等实现它，经 utility 构造注入；付费插件做「适配器」不内置。");
            host.AddConcept("换格式 = INetworkSerializer", "Protobuf（跨语言后端 / 既有 proto）、MemoryPack（双端 C#）实现它；等真实后端契约驱动，工具链成本不凭空预付。");
            host.AddConcept("不做自动重试 / 自动重连", "幂等性、重新认证、状态恢复只有业务知道——框架给退避样板（guide §25）、不做黑盒。重连 = 订 WebSocketClosedEvent(!ByUser) + 循环 Connect。");
            host.AddConcept("不做 WebGL 的 WS / RPC correlation id / 大文件下载", "WebGL 的 WS 写 JS-bridge provider（接缝已留）；带请求-响应关联的 RPC 是 MagicOnion 领域；大文件下载归资源系统。");

            host.AddTip("速记：请求-响应用 await Get/Post（非 2xx 抛 NetworkException，查 Kind/StatusCode）；推送用 RegisterPush 映射 + Bag.Subscribe 消费；连接状态订 ws.State；断开订 WebSocketClosedEvent。深度见 framework-guide 网络章 / ADR-0028。");
        }

        private void BuildHttpSection(DemoModuleHost host, IHttpUtility http)
        {
            host.AddSectionTitle("HTTP 请求-响应（原子按钮）");
            var httpLabel = host.AddValueDisplay("点下面的按钮发请求，结果显示在这里。");
            httpLabel.style.whiteSpace = WhiteSpace.Normal;

            string token = null; // 登录拿到的 token，被下面几个闭包共享

            host.AddActionRow("POST /api/login（登录拿 token）", async () =>
            {
                try
                {
                    var resp = await http.Post<LoginReq, LoginResp>("api/login", new LoginReq { User = "hero", Password = "pw" });
                    token = resp.Token;
                    httpLabel.text = $"登录成功 ✓ token={resp.Token}，playerId={resp.PlayerId}。现在可点「设置 token 头」。";
                }
                catch (NetworkException e) { httpLabel.text = $"登录失败：{e.Kind} — {e.Message}"; }
            }, CodeRef.Here("http.Post<LoginReq, LoginResp>", "Post 请求-响应"));

            host.AddActionRow("SetHeader Authorization（用登录 token）", () =>
            {
                if (token == null) { httpLabel.text = "先点登录拿 token。"; return; }
                http.SetHeader("Authorization", $"Bearer {token}");
                httpLabel.text = "已设置 Authorization 默认头 ✓ 之后每个请求都自动带上（典型 auth 姿势）。";
            }, CodeRef.Here("http.SetHeader(\"Authorization\"", "默认头"));

            host.AddActionRow("GET /api/leaderboard?count=3（需 token，缺则 401）", async () =>
            {
                try
                {
                    var board = await http.Get<Leaderboard>("api/leaderboard?count=3");
                    httpLabel.text = "排行榜 ✓：" + string.Join("、", board.entries);
                }
                catch (NetworkException e) when (e.Kind == NetworkErrorKind.HttpError && e.StatusCode == 401)
                {
                    httpLabel.text = "401 未授权：还没设 Authorization 头。这演示了「预期内的业务错误用 catch...when 过滤 StatusCode」，框架不把状态码折叠成 null。";
                }
                catch (NetworkException e) { httpLabel.text = $"失败：{e.Kind} {e.StatusCode}"; }
            }, CodeRef.Here("http.Get<Leaderboard>", "Get + query"));

            host.AddActionRow("GET /api/fail?code=500（非 2xx 抛 HttpError）", async () =>
            {
                try
                {
                    await http.Get<Leaderboard>("api/fail?code=500");
                    httpLabel.text = "不该到这（500 应抛）。";
                }
                catch (NetworkException e)
                {
                    httpLabel.text = $"HttpError ✓ Kind={e.Kind}，StatusCode={e.StatusCode}，Body={e.ResponseBody}。";
                }
            }, CodeRef.Here("api/fail?code=500", "非 2xx"));

            host.AddActionRow("GET /api/slow?ms=6000（3s 默认超时触发）", async () =>
            {
                httpLabel.text = "请求中……（服务器要 6s，客户端 3s 超时会先触发）";
                try
                {
                    await http.Get<SlowResp>("api/slow?ms=6000");
                    httpLabel.text = "返回了（没超时？）。";
                }
                catch (NetworkException e) when (e.Kind == NetworkErrorKind.Timeout)
                {
                    httpLabel.text = "Timeout ✓ 内部超时（网络环境问题，可提示玩家重试）——与外部取消严格区分。";
                }
                catch (NetworkException e) { httpLabel.text = $"失败：{e.Kind}"; }
            }, CodeRef.Here("api/slow?ms=6000", "超时"));

            CancellationTokenSource slowCts = null;
            host.AddActionRow("GET /api/slow + 1.5s 后手动取消（→ OCE，不是 Timeout）", async () =>
            {
                slowCts?.Cancel();
                slowCts = new CancellationTokenSource();
                var localCts = slowCts;
                localCts.CancelAfter(1500);
                httpLabel.text = "请求中……1.5s 后自动取消";
                try
                {
                    await http.Get<SlowResp>("api/slow?ms=6000", localCts.Token);
                    httpLabel.text = "返回了（没被取消？）。";
                }
                catch (OperationCanceledException)
                {
                    httpLabel.text = "已取消 ✓ 外部 ct 取消 = OperationCanceledException（调用方意图），不包装成 NetworkException——与超时区分开。";
                }
                catch (NetworkException e) { httpLabel.text = $"失败：{e.Kind}"; }
            }, CodeRef.Here("localCts.Token", "外部取消"));
        }

        private void BuildWebSocketSection(DemoModuleHost host, IDemoGameServer server, IWebSocketUtility ws)
        {
            host.AddSectionTitle("WebSocket 长连接：推送转事件");
            var stateLabel = host.AddValueDisplay();
            stateLabel.style.whiteSpace = WhiteSpace.Normal;
            var wsLabel = host.AddValueDisplay("连接后：服务器每 2s 推 tick（→ 事件）；发 chat 服务器回显（→ 事件）。");
            wsLabel.style.whiteSpace = WhiteSpace.Normal;

            // 连接状态：订阅 RP（订阅即得当前值）。State 随宿主 Bag 退订。
            Bag.Subscribe(ws.State, state => stateLabel.text = $"连接状态：{state}");

            // 推送 → 框架 Event（映射在 InstallBindings 里配好）。这里像订 Model 事件一样消费。
            int tickCount = 0;
            Bag.Subscribe<ServerTickEvent>(e =>
            {
                tickCount++;
                wsLabel.text = $"收到服务器推送 tick #{e.count}（本章会话内第 {tickCount} 条）——这是「推送→框架 Event」。";
            });
            Bag.Subscribe<ChatEchoEvent>(e => wsLabel.text = $"收到服务器回显：\"{e.Value}\"——客户端 Send 的 chat 被服务器原样推回、映射成事件。");
            Bag.Subscribe<WebSocketClosedEvent>(e => wsLabel.text = $"连接关闭：ByUser={e.ByUser}，原因「{e.Reason}」。ByUser=false 时业务可据此触发重连。");

            host.AddActionRow("Connect（连接内嵌 WS 服务器）", async () =>
            {
                try
                {
                    await ws.Connect(server.WsUrl);
                    wsLabel.text = "已连接 ✓ 稍等 2s 就能看到第一条 tick 推送。默认 ClientWebSocket 直连（Proxy=null，绕系统代理）。";
                }
                catch (InvalidOperationException) { wsLabel.text = "已在连接中或已连接——先 Disconnect。"; }
                catch (NetworkException e) { wsLabel.text = $"连接失败：{e.Kind} — {e.Message}（服务器停了？）"; }
            }, CodeRef.Here("ws.Connect(server.WsUrl)", "建连"));

            int sendSeq = 0;
            host.AddActionRow("Send chat（服务器回显 → 事件）", async () =>
            {
                try
                {
                    await ws.Send("chat", new ChatOutbound { Value = $"hello #{++sendSeq}" });
                    wsLabel.text = $"已发送 chat「hello #{sendSeq}」，等服务器回显……";
                }
                catch (NetworkException e) { wsLabel.text = $"发送失败：{e.Kind}（未连接？先 Connect）。"; }
            }, CodeRef.Here("ws.Send(\"chat\"", "发送"));

            host.AddActionRow("Disconnect（主动断开 → ClosedEvent ByUser:true）", async () =>
            {
                await ws.Disconnect();
            }, CodeRef.Here("ws.Disconnect()", "断开"));

            host.AddSubNote("推送事件类型是 `[Serializable] struct + 公共字段`（`ServerTickEvent { public int count; }`）——JsonUtility 只认字段，别用 record 位置参数（那是属性、反序列化不出来）。映射用 `RegisterPush&lt;TEvent&gt;(\"type\")`，本章在 InstallBindings 里配好。",
                CodeRef.Here("ws.RegisterPush<ServerTickEvent>", "推送映射"));
        }
    }
}
