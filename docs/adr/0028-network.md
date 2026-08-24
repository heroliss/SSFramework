# ADR-0028：网络模块 —— IHttpUtility 请求-响应 + IWebSocketUtility 推送转事件，传输与序列化双接缝

**Status:** Accepted（2026-07-06）

## Context

roadmap 长期清单第一项：网络模块。「规划中的模块」表已给方向：服务抽象隔离传输层；请求/长连接/重试/取消接 UniTask + CancellationToken；**消息建模分两类**——请求-响应 = `UniTask<TResp>` 返回值，服务器推送/广播 = 转框架 Event；序列化随服务器技术栈定、藏在接缝后。

既有约束与先例：

- **ports & adapters**：零第三方依赖的能力整体留内核（Storage / Pool 先例）；重第三方依赖才开独立模块 asmdef（Asset.Yoo / Fonts 先例）。
- **双接缝先例**（ADR-0021）：`IStorageProvider`（介质）× `IStorageSerializer`（格式）正交注入，网络照搬为「传输 × 格式」。
- **主线程铁律**（ADR-0003）：事件系统与容器主线程独占；网络回调天然来自后台线程，回主线程是本模块必须兜住的边界。
- **失败语义先例**：预期内缺失给 null、系统级失败抛异常；取消永远抛 `OperationCanceledException` 不包装。
- 候选盘点（roadmap）：BestHTTP（付费）、UnityWebRequest 封装、gRPC（MagicOnion）、WebSocket。项目当前零网络原语。
- 范围拍板：HTTP 请求-响应 + WebSocket 长连接一起做（推送→Event 的框架特色建模需要真实落点）；序列化 JSON 零依赖起步。

## Decision

### 1. 两个门面，不做一个大 `INetworkUtility`

```csharp
public interface IHttpUtility : IUtility          // 无状态请求-响应
{
    string BaseUrl { get; }
    void SetHeader(string name, string value);    // 默认头（同名覆盖；null 值移除）；典型：登录后 Authorization
    UniTask<TResp> Get<TResp>(string path, CancellationToken ct = default) where TResp : class;
    UniTask<TResp> Post<TReq, TResp>(string path, TReq body, CancellationToken ct = default)
        where TReq : class where TResp : class;
    UniTask Post<TReq>(string path, TReq body, CancellationToken ct = default) where TReq : class;
    UniTask<HttpResponse> Send(HttpRequest request, CancellationToken ct = default);   // 逃生舱
}

public interface IWebSocketUtility : IUtility     // 有状态长连接（一个实例 = 一条逻辑连接）
{
    ReadOnlyReactiveProperty<NetworkConnectionState> State { get; }   // Disconnected/Connecting/Connected
    void RegisterPush<TEvent>(string type) where TEvent : IEvent;   // 2026-07 修订：struct→IEvent（见下）
    UniTask Connect(string url, CancellationToken ct = default);
    UniTask Disconnect(CancellationToken ct = default);
    UniTask Send<T>(string type, T payload, CancellationToken ct = default) where T : class;
    UniTask Send(string type, CancellationToken ct = default);        // 无载荷（心跳等）
}
```

- **为什么拆**：无状态请求与有状态连接的生命周期、失败模式、Dispose 语义完全不同；多数游戏只用 HTTP，不该被迫背上连接状态机 API。拆开后各自独立注册——HTTP 全局、战斗专用 WS 注册进 `FlowState` 子 Context 随阶段整棵撤（与 ADR-0023 天然协同）。roadmap 草案名 `INetworkUtility` 不保留。
- **HTTP 用 REST 动词风、不做「消息类型→路由」注册表**：`Get`/`Post` + path 显式直白、零注册表；「业务只见强类型消息」由泛型体现。query 直接写在 path（动态值 `Uri.EscapeDataString`）；PUT / DELETE / raw bytes / 每请求头 / 读响应头，全部收敛进 `Send(HttpRequest)` 一个逃生舱。
- **动词门面严格、逃生舱宽容**：动词方法非 2xx 抛异常（游戏 API 的常态预期是成功）；`Send` 只要 HTTP 交换完成（含 4xx/5xx）就返回 `HttpResponse` 不抛（查 `IsSuccess`），只有传输层失败才抛——两种模式分工清晰，不靠参数开关。

### 2. 失败语义：单一 `NetworkException` + `Kind` 分级

| 情形 | 行为 |
|---|---|
| path/body 参数非法、相对 path 但 BaseUrl 为 null | 抛 `ArgumentException`（代码写错） |
| Dispose 后调用 | 抛 `ObjectDisposedException` |
| DNS 失败 / 拒绝连接 / 网络断开 | `NetworkException(ConnectionError)` |
| 超时（内部 `CancelAfter` 触发） | `NetworkException(Timeout)` |
| 外部 ct 取消 | `OperationCanceledException`（不包装，框架统一约定） |
| 非 2xx（动词门面） | `NetworkException(HttpError)`，携带 `StatusCode` + `ResponseBody`（截断 ≤4KB） |
| 响应体 / 推送载荷反序列化失败 | `NetworkException(DeserializeError)`（服务器契约不符） |
| 2xx 空响应体 | 返回 `null`（唯一的 null 语义） |
| 未连接时 WS `Send` / 发送中途 socket 断掉 | `NetworkException(ConnectionError)`（传输层原始异常不外泄给业务） |
| Connecting 中调用 WS `Disconnect` | 取消在途 `Connect`（其 await 收到 OCE）、不发 ClosedEvent |
| 未注册的推送 type | Editor/Dev 一次性 warning + 丢弃（照 Localization 缺 key 先例），不毒化接收循环 |

- **非 2xx 不折叠成 null**：REST 状态码语义因服务器而异，隐藏状态码丢信息。预期 404 的业务 `catch (NetworkException e) when (e.Kind == NetworkErrorKind.HttpError && e.StatusCode == 404)`。
- 超时与外部取消**严格区分**：内部超时是网络环境问题（Timeout，可提示重试）；外部 ct 是调用方意图（OCE，静默尊重）。

### 3. 线程模型：公共 API 主线程，后台回调回主线程再触碰框架

- HTTP 默认传输走 UnityWebRequest 引擎异步操作，**全程不下线程池**（这也是 WebGL 兼容的来源）；请求之间天然并行（无共享介质，不需要 Storage 那样的 FIFO）。
- WS 接收循环：后台 `ReceiveAsync` → 每条消息 `await UniTask.SwitchToMainThread()` → 主线程解析 envelope + 查注册表 + `SendEvent`。事件系统是 R3 Subject + 字典（无锁、主线程独占），这一跳是铁律，写死在框架里而不是留给业务记住。
- WS 发送内部 FIFO 串行（尾任务链，照 `StorageUtility.Enqueue`）：保序 + 规避 `ClientWebSocket` 不允许并发写的限制。
- 坏消息（烂 JSON / 载荷反序列化失败）：warning + 丢弃当条，接收循环继续——单条脏数据不毒化整条连接。

### 4. WS wire 协议：JSON envelope `{type, payload}`，payload 二次编码

```json
{ "type": "chat", "payload": "{\"from\":\"a\",\"text\":\"hi\"}" }
```

- `RegisterPush<TEvent>("type")` 把服务器推送映射为框架事件：收到该 type → payload 反序列化为 `TEvent` → `SendEvent`。业务消费推送 = `Bag.Subscribe<TEvent>`，与 Model 事件同一套心智——**这正是「推送转事件」双轨建模的落点**。
- payload 是**字符串二次编码**而非嵌套对象：默认 `JsonUtility` 无法从泛型外层提取嵌套原始 JSON 片段，字符串载荷让零依赖序列化稳定工作。envelope 是 v1 的 wire 契约（demo 服务器同款）。
- **2026-07 修订（Outpost M4 驱动）**：字符串二次编码对二进制格式是破坏性的（Protobuf 字节过 `UTF8.GetString` 不保真）。新增可选接缝 `IWebSocketEnvelopeSerializer : INetworkSerializer`——序列化器实现它即整体接管 envelope 编解码与帧类型（payload 全程 `byte[]`、`UseBinaryFrames` 决定发二进制帧；`IWebSocketProvider.SendAsync` 相应加 `binary` 参数）。不实现的序列化器走原 JSON 兼容路径（wire 字节不变，零迁移）。envelope 的线上形态由格式自定（如 Protobuf 的 `{string type=1; bytes payload=2}`），框架不再规定嵌套编码方式。
- 推送事件类型约定：`[Serializable] struct + 公共字段`（`JsonUtility` 只认字段，**record 位置参数是属性、反序列化不出来**）。框架自产事件（如 `WebSocketClosedEvent`）不经反序列化、本无此约束，但内核程序集无 `IsExternalInit` polyfill、位置参数 record 的 init 访问器编译不过，故照 `FlowChangedEvent` 先例用 `readonly struct` + 显式字段。
- **2026-07 修订（Outpost proto 生产化驱动）**：`RegisterPush<TEvent>` 约束从 `struct, IEvent` 放宽为 `IEvent`——`struct` 是绑死默认 `JsonUtility` 的（它只反序列化 struct 字段），把二进制序列化器的 **class 消息挡在外**（Google.Protobuf 生成的 `IMessage` 是 class）。放宽后：struct 事件空 payload 仍取 `default(TEvent)`（零值合法），**引用类型事件空 payload 无默认实例可造 → 丢弃告警**（引用类型推送必须带 payload）。这是「换真 protobuf 库时才现形」的接缝内屈——JSON 单实现期约束设过紧、切二进制库时才暴露。
- 连接关闭统一发 `WebSocketClosedEvent(ByUser, Reason)`：用户主动 `Disconnect` 与意外断开都发，业务重连逻辑过滤 `!ByUser`。

### 5. 双接缝：传输 provider × 序列化 serializer，默认实现零依赖留内核

```
IHttpUtility ── HttpUtility（拼 URL / 合并头 / 超时 / 异常折叠）
IWebSocketUtility ── WebSocketUtility（状态机 / envelope / 推送注册表 / 收发循环）
     ├─ IHttpProvider ──── 默认 UnityWebRequestHttpProvider（全平台含 WebGL）
     ├─ IWebSocketProvider ─ 默认 ClientWebSocketProvider（除 WebGL 全平台）
     └─ INetworkSerializer ─ 默认 JsonUtilityNetworkSerializer（HTTP/WS 共用；ContentType 属性随格式走）
```

- 全部住内核 `Core/Network/`：UnityWebRequest 是引擎一等模块（与 `JsonUtility` / `AudioSource` 在 Core 同级），`ClientWebSocket` / `HttpListener` 是 BCL——零第三方依赖、asmdef 零改动。
- provider 接口保留 `Async` 后缀（适配层惯例）；门面 API 无后缀。
- `INetworkSerializer` **无 `class` 约束**（与 `IStorageSerializer` 不同）：WS 推送事件是 struct。
- 超时不归 provider：`HttpUtility` 用 `CancelAfter` 链接进 ct 统一实现，provider 只需尊重 ct。

### 6. 第三方定位（本模块与候选库的边界）

- **BestHTTP**：未来的 `IHttpProvider` / `IWebSocketProvider` 第二传输实现。值回票价的场景：WebGL 的 WS、HTTP/2 复用与连接调优、后端上 SignalR / Socket.IO / SSE。付费插件 license 不可随框架分发，形态永远是「~100 行适配器菜谱」而非内置依赖。接入后业务代码零改动——正是「第二实现验证抽象边界」的路径。
- **MagicOnion**：整套 RPC 范式（强类型服务接口 + MemoryPack + gRPC），**不是本接缝后的传输**。真用它时的正确姿势是「MagicOnion 直接用 + 框架管其余」，不要试图塞进 `IHttpProvider`。
- **Protobuf / MemoryPack**：`INetworkSerializer` 第二实现。**2026-07 修订（Outpost M4 驱动）**：内核新增轻量 `ProtobufNetworkSerializer`（`ProtoWriter`/`ProtoReader` 手写 wire 原语 + per-message 显式编解码注册，零依赖零反射、字节与标准 protobuf 互通）——覆盖「消息不多的自建后端 / dev server」段位，并让二进制 envelope 接缝有内置的第二实现验证。消息多到需要 `.proto` 契约共享 / map / oneof / 有符号 / 浮点时，仍换 Google.Protobuf 等真库（protoc 工具链），构造注入替换本类即可；MemoryPack 的 source generator 与 HybridCLR 热更兼容性仍需专门验证。**2026-07 追加（Outpost proto 生产化）**：换真库这条路已走通一次——用官方 protoc + Google.Protobuf 写了 `GoogleProtobufNetworkSerializer : IWebSocketEnvelopeSerializer`（全泛型，对任意 `IMessage` 生效，envelope 用官方 `CodedOutputStream` 手写、与内置 `ProtobufNetworkSerializer` 的 envelope 逐字节一致且可互解），构造注入替换手写实现、消费方零改动；生成 `IMessage` 与手写 `ProtoWire` wire 互通（反证 `ProtoWire` 是真 wire 格式）。踩到的接缝内屈（`RegisterPush` 的 struct 约束）见上「消息建模双轨」节修订。

**2026-07 再修订（提炼为框架增强模块）**：上条最初住 Outpost 业务侧 + 编辑器菜单硬编码单一 .proto 路径；现提炼为框架增强模块 **`Game.Framework.Network.Proto`**（同 `Asset.Yoo` 的 ports & adapters 收口姿势——Google.Protobuf 依赖收口于此、内核仍第三方零依赖，模块可整块删除 / 抽 UPM），成为「框架默认提供、可选启用」的网络序列化第三档（默认 JSON / 内核手写 ProtoWire / 官方 Google.Protobuf 三档，见 §5 与 `Assets/Game/AGENTS.md`「模块使用不变量」的网络条目）。三处强化：① **整文件注册** `RegisterFile(生成的 XxxReflection.Descriptor)` 递归登记一个 .proto 的全部消息（含嵌套类型、跳过 map entry 合成类型、**递归 `import` 的依赖文件**——只给顶层 file、传递闭包自动带上，多 file 共享依赖 / well-known types 幂等跳过），替代逐消息 `Register(T.Parser)`——加消息重新生成即自动纳入，无「加了忘注册」的缝（单消息 `Register` 仍保留，显式重复即抛以区分）。② **生成管线对齐其它模块**（Framework AGENTS「配置 Profile 约定」三件套）：`ProtoConfigProfile`（多套按目录配置：.proto 源目录 + 输出目录 + protoc 路径）+ `ProtoCodeGenerator`（封装 protoc CLI + **差量同步**：内容未变不落盘、陈旧 `*.g.cs` 连 .meta 清理）+ 菜单 `SSFramework/Protobuf/*` + `ProtoConfigOverviewWindow` 专用总览，并登记进框架配置总览 hub（`FrameworkConfigOverviewWindow`）。③ **envelope 编码零冗余分配**：`ComputeSize` 预算 + 单次精确分配 + `UnsafeByteOperations.UnsafeWrap` 免 payload 二次拷贝。IL2CPP 防裁剪 link.xml preserve `Google.Protobuf` 随模块走；HybridCLR 侧 Google.Protobuf 泛型实例的 AOT 元数据由构建期 SuperSet 补充。测试：`GoogleProtobufNetworkSerializerTests`（RegisterFile 递归嵌套 · repeated · map 往返 / 递归 import 依赖 / 共享依赖幂等 / envelope 与内核逐字节一致 + 双向互解 / 与 ProtoWire 手写编解码 wire 互通 / 失败语义）8 用例，PlayMode 333/333 全绿。

### 7. 注册与生命周期

- `builder.RegisterOwned(new HttpUtility("https://api.xxx.com"), typeof(IHttpUtility))`——环境切换（dev/prod）= 注册时传不同 baseUrl，构造定死、运行期不可变。
- `WebSocketUtility` 实现 `IHasGameContext`（`SendEvent` 所需），`RegisterOwned` 注册即注入时由 `AttachTo` 回填 Context（照 `GameFlow` 姿势）；脱离容器 new 后未 Attach 就 `Connect` 抛。
- Dispose：`HttpUtility` 取消所有在途请求；`WebSocketUtility` 停收发循环 + 关闭连接。随 Context 整棵撤。

### 8. 环境实测结论（落地时的两个坑，spike 已验证）

- **默认 WS 传输 `ClientWebSocketProvider` 直连、不走系统代理**（`Options.Proxy = null`）：游戏直连自己的服务器是常态，系统/公司代理对出海游戏罕见相关；且实测拦截式系统代理会挡掉 `ClientWebSocket` 的 localhost 连接（demo 连本地服务器直接失败）。绕过代理让「直连语义」与「demo 可跑」统一。需要走代理的少数场景自写 provider（接缝已留）。
- **demo / 测试的内嵌 WS 服务器用 `TcpListener` + 手写 RFC6455 握手/帧，不用 `HttpListener`**：实测 Unity Editor 的 Mono 运行时 `HttpListenerRequest.IsWebSocketRequest` 恒为 false（升级头 `Upgrade`/`Connection`/`Sec-WebSocket-Key` 都在也不认）、`AcceptWebSocketAsync` 不可用。手写握手只算一次 `Sec-WebSocket-Accept`（key + 魔术 GUID 的 SHA1），帧编解码仅覆盖小文本帧——只进 Demo/Test 程序集，不污染框架。**框架侧 `ClientWebSocketProvider`（客户端）不受影响**：`ClientWebSocket` 本身在 Mono 下工作正常，实测客户端能连、能收发。
- **Mono 的 `HttpListener.Start` 在端口冲突时可能直接抛 `SocketException(AddressAlreadyInUse)`**，不一定包装为 `HttpListenerException`。Demo、Outpost dev server 与测试 server 的端口扫描同时处理两种表现，每个失败 candidate 都关闭后再试下一端口；扫描耗尽时保留最后异常。HTTP/WS 构造使用局部资源，两个监听器都成功才提交 Running，后段失败按逆序回滚。Demo 中有副作用的 server 另放进 Eager `RegisterOwnedFactory`，让启动与后续服务构造处于同一个 Container Build 事务。

### 9. 刻意不做（记录在案，等真实需求）

- **自动重试**：幂等性只有业务知道，框架半吊子重试会诱导对非幂等 POST 重试；3 行 UniTask 退避循环即样板（guide 给）。多项目抄同一段后再议升格。
- **WS 自动重连**：重连绑着重新认证 / 状态恢复（纯业务）；机制 = 订 `WebSocketClosedEvent(ByUser:false)` + 循环 `Connect` 退避（guide 给 ~15 行样板）。
- **WebGL 的 WebSocket**：`ClientWebSocket` 不支持 WebGL；需要时写 JS-bridge 第二 provider，接缝已留。HTTP 路径 WebGL 天然兼容。
- **RPC-over-WS 请求响应关联（correlation id）/ 双向 RPC**：MagicOnion 领域。
- **大文件下载 / 进度 / 断点续传**：归资源系统（YooAsset 下载器已有），HTTP 门面不是下载器。
- **请求队列 / 限流 / 去重 / ETag 缓存 / query builder / multipart / Cookie 管理**：现有原语（`Send` 逃生舱 + 字符串插值 + 自定义 provider）可组合。
- **Mono 壳（MonoHttpUtility）**：BaseUrl / token 是环境配置不是场景配置（对齐 Localization / GameFlow 无 Mono 版先例）。
- **弱网模拟 / 抓包诊断**：Charles / Clumsy 等外部工具领域。

## Consequences

- 业务发请求 = `await http.Post<LoginReq, LoginResp>("api/login", req, ct)` 一行；消费推送 = `Bag.Subscribe<TickPushEvent>` 与订 Model 事件零差别——网络数据以受控姿势进入单向数据流。
- 线程边界由框架兜住：业务永远在主线程收到回调，「后台线程碰 UI/容器」这类偶发 bug 被结构性消灭。
- 默认 JsonUtility 的限制随文档声明（只认 `[Serializable]` 字段、无 Dictionary / 多态）：请求/响应/推送类型照此设计；换强格式 = 换 serializer 一行构造参数，业务类型标注随所选库调整。
- envelope 二次编码有每条消息一次额外字符串分配的代价——推送频率在「游戏服务器广播」量级（每秒个位数条）时无感知；高频实时同步（帧同步/状态同步）本就不该走这套（见刻意不做的 RPC/私有协议条目）。
- 不做自动重试/重连意味着业务必须自己写这两段样板（guide 提供）——换来的是重试边界、认证时序完全显式，没有框架黑盒行为可猜。
- WS 每实例一条连接：多连接 = 多注册（不同 contract key 或子 Context），连接归属清晰、随作用域自动清理。
