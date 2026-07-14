# ADR-0034：框架日志接缝 —— 内核 ILogSink 多播 + 默认 Console/File sink + ZLogger 可选模块

**Status:** Accepted（阶段 A 接缝，2026-07-14）——ZLogger 客户端模块经实测放弃（依赖过重，见 §Decision 3「实测复盘」），服务端直接用；接缝已为将来接入留位。

## Context

roadmap「Cysharp 生态候选」里 **ZLogger**（零分配结构化日志）标着「评估与 `FrameworkLog` 的整合」。评估这条线时，先摸清了框架日志现状与真实缺口：

- **`FrameworkLog` 极简**：一个静态 `Verbose` bool + `LogVerbose(string)`，仅 `UNITY_EDITOR || DEVELOPMENT_BUILD` 下转发 `Debug.Log`，只服务内核基础设施诊断（`Container` / `InjectionPlan` / `MonoGameContextBase` 等）。
- **主力是裸 `Debug.Log*`**：框架内 180 处、跨 73 文件，**没有任何统一接缝**。
- **缺口不是「没有 ZLogger」，是「日志没有一层可替换的接缝」**：因此做不到——① 按模块分级过滤 / 静音；② 把日志落到文件（玩家包捞日志）；③ 测试期捕获日志断言；④ 重定向到结构化采集 / 遥测后台。这四件事的价值**独立于用不用 ZLogger**。

**ZLogger 评估结论**（已查证）：它建在 `Microsoft.Extensions.Logging` 上，零分配（interpolated string handler + source generator）、结构化（JSON / MessagePack）、多 sink（Console / File / RollingFile / InMemory / HTTP 批处理）；IL2CPP 下标准 `LoggerFactory` 不可用，但官方 `UnityLoggerFactory` 全平台可用。**但它的杀手锏（零分配 / 结构化 / 高吞吐文件与遥测）主要在服务端和线上运营兑现**——客户端开发期日志量不大、发布版日志编译消除，且开发者依赖 Unity Console 的双击定位。因此：**框架内核不应强绑 `Microsoft.Extensions.Logging` 的心智与一串 DLL**，而应按框架一贯做法——先补接缝、把第三方藏在接口后按需引入（像 `IAssetProvider` 隔离 YooAsset 那样）。

未来最常用需求排序（据此决定「内核零依赖做到哪、ZLogger 从哪接手」）：① 按模块分级过滤（开发期，天天用）→ ② 落文件（玩家包 QA / 用户反馈捞日志）→ ③ 结构化 / 零分配 / 遥测（服务端、线上运营，后期）。**①② 用内核零依赖就能覆盖，③ 才需要 ZLogger。**

## Decision

### 1. 接缝形态：静态 `FrameworkLog` 门面 + `ILogSink` 多播（**不是** DI Utility）

日志必须在**任何地方**可用——包括没有 `Context`、身处 DI 之下的内核基础设施（`Container` / `InjectionPlan` / 构造期）。它们不能反向依赖 DI 去 `GetUtility` 取 logger（循环依赖 + 时序倒置）。`FrameworkLog` 现在正因此是静态。所以接缝**保持静态门面**，不做 `ILogUtility` 那种 DI 服务。

- `FrameworkLog` 升级为门面：`Trace / Info / Warning / Error(+ exception)`，每条带 `category`（默认取调用方类型名 / caller）与可选结构化字段。保留旧 `Verbose` / `LogVerbose` 语义（`Verbose=true` = 放行 `Trace` 级到 Console），既有调用点零改动。
- `ILogSink`：`void Log(in LogEntry entry)`。可注册**多个** sink 广播；每个 sink 自带 `MinLevel` 过滤。门面提供 `AddSink / RemoveSink / ClearSinks`。
- `LogEntry`：`readonly struct`，携 `LogLevel` + `category` + `message` + 可选结构化字段 + `exception` + `timestamp` + caller。以 `in` 传递；热路径只在**有 sink 会消费该级别**时才构造 message（`Verbose` 关时 `Trace` 直接短路，同现状零成本）。

### 2. 内核默认两个**零依赖** sink（覆盖最常用 ①②）

- **`UnityDebugLogSink`（默认装配）**：把 entry 转发到 `Debug.Log / LogWarning / LogError`——Console 观感、双击定位、stack trace 全不变。沿用现有条件编译语义（`UNITY_EDITOR || DEVELOPMENT_BUILD` 才输出；Release 编译期消除）。这保证「迁移到接缝」对 Console 输出**零行为变化**，迁移可渐进。
- **`FileLogSink`（opt-in）**：`StreamWriter` 追加 + 按大小滚动（保留最近 N 份），纯 C# 零依赖，玩家包也能开。给「落文件捞日志」这个最常用的未来客户端需求兜底——**不必为了「写个日志文件」就吞下一串 `Microsoft.Extensions.Logging` DLL**。刻意不做异步批处理 / 精细滚动策略（要那些就上 ZLogger）。

### 3. ZLogger 作为**可选升级模块** `Game.Framework.Logging.ZLogger`

定位：当需要**零分配 / 结构化 JSON / RollingFile 精细化 / HTTP 遥测**时的进阶 sink，尤其服务端与线上运营。

- `ZLoggerLogSink : ILogSink` 桥接到 ZLogger 的 `LoggerFactory`（Unity 侧用 `UnityLoggerFactory` 保 IL2CPP）：`Log(entry)` 把结构化字段 / 级别 / category 翻成 ZLogger 调用。
- **姿势同 `Asset.Yoo` / `Network.Proto`**：独立 asmdef（`references: ["Game.Framework"]` + ZLogger DLL 经 NuGetForUnity auto-ref）、目录下 `link.xml` 防 IL2CPP 裁剪、`autoReferenced:false`、**可整块删除**。第三方依赖（`Microsoft.Extensions.Logging` 系列）收口于此，**内核零依赖不变**。
- **显式注册，不走反射工厂**：启动代码 `FrameworkLog.AddSink(new ZLoggerLogSink(cfg))`。sink 替换是用户的启动期配置、没有 `IAssetProvider` 那种「内核构造时刻必须装配一个」的时序刚性，显式比反射清晰（`AssetProviderFactory` 用反射是因为内核禁止编译期引用模块又必须自动装配——日志默认 sink 在内核自带，无此约束）。
- 依赖现状：ZLogger 的核心传递依赖（`Microsoft.Bcl.AsyncInterfaces` / `Microsoft.Bcl.TimeProvider` / `System.Threading.Channels`）**已在项目**（R3 等带入），真装时主要新增 ZLogger 本体 + `Microsoft.Extensions.Logging`；source generator 需正确标 `RoslynAnalyzer`。**引入这串 DLL 是一次显式的依赖决策，落地前单独确认**（见 Consequences）。

#### 实测复盘（2026-07-14）：客户端放弃、回退依赖

按上述路径实际装了 ZLogger 2.5.10（NuGetForUnity 可编程安装）后，依赖链比探路预估**重得多**：除 `Microsoft.Extensions.Logging` 一串（DI / Options / Primitives / Abstractions），还硬拖进 **`System.Text.Json`（594 KB）**、`System.Diagnostics.DiagnosticSource`（171）、`System.Text.Encodings.Web`（77）、`Utf8StringInterpolation`（33）等——**运行时托管 DLL 增量 ≈ 1.4 MB**（另有几十个多语言 `*.resources.dll`），并把 `Microsoft.Bcl.AsyncInterfaces` 被动从 6.0 升到 8.0。且为 IL2CPP 真机不崩需 `link.xml preserve`，与「靠裁剪压包体」直接对冲——想要 AOT 正确就压不下体积。

其中最大的 `System.Text.Json` 纯为**结构化 JSON 输出**，而客户端几乎不产 JSON 日志——**最大的一块开销花在客户端最用不上的功能上**。故决定：**客户端框架不引入 ZLogger**，已 git 回退全部依赖到接缝提交态（阶段 A）。ZLogger 的结构化 / 零分配 / 遥测能力真正的落点是**服务端**（Outpost `Server~/` 本就是 .NET，直接用 ZLogger、无包体顾虑）与将来确有「客户端结构化日志上报后台」刚需时——那时它作为 `ILogSink` 接缝后的一个实现接入，本 ADR 的接缝设计已为此留好位置（`AddSink` + `ILogSink`，业务零改动）。

**教训**：第三方依赖的真实成本要**实装量过**再拍板（探路阶段只看文档会低估传递依赖链）；幸而先做了零依赖接缝、ZLogger 隔在接口后，回退只是删依赖、内核与业务代码零改动。

### 4. 迁移策略：渐进，**不做一次性 180 处大改**

- 默认 `UnityDebugLogSink` 转发 `Debug.Log` ⇒ 「迁移到接缝」对 Console 输出零行为变化，无需一次性重写。
- **立即走接缝**：内核诊断（原 `FrameworkLog.LogVerbose` 调用点）+ 本 ADR 之后的新代码。
- **逐步迁移**：既有 180 处 `Debug.Log*` 按接触逐步搬到接缝（改到哪个文件顺手搬），错误 / 警告优先（那些「必须可见 + 可能要落文件 / 上报」）。常规 `Debug.Log` 不强制。

### 5. 刻意不做

- **不把 `Microsoft.Extensions.Logging` 抽象（`ILogger<T>` / scope / category 体系）暴露进内核**——那是 ZLogger 模块的内部细节；内核门面保持朴素的 level + category + fields。
- **不做 DI Utility 版日志**：静态门面已覆盖，且内核基础设施需要「DI 之下可用」。
- **不自研结构化 JSON / MessagePack 序列化 / 异步批处理 / 遥测传输**：要这些就上 ZLogger，不重造半吊子。
- **不全量替换 `Debug.Log`**：见迁移策略。

## Consequences

- 日志获得可替换接缝：按模块过滤 / 静音、落文件、测试捕获断言、遥测重定向，全部有了统一着力点；`FrameworkLog` 从「一个 bool」长成真正的日志门面。
- **内核零新增依赖、Console 观感与定位不变**；「落文件」由内核 `FileLogSink` 零依赖兜底，覆盖绝大多数客户端排查场景。
- ZLogger 成为**可选升级**：客户端默认不吞 `Microsoft.Extensions.Logging` DLL；要结构化 / 遥测时按需接入，且服务端（Outpost `Server~/` 已是 ASP.NET Core）能与客户端共用同一套日志抽象心智。
- 180 处 `Debug.Log` 渐进迁移，无一次性大改风险。
- **依赖引入分界**：接缝 + 两个内核 sink（阶段 A）零第三方依赖、**已落地**。ZLogger 模块（阶段 B）实测后**放弃**（依赖 ≈ 1.4 MB，见上「实测复盘」）——客户端框架保持零第三方日志依赖，ZLogger 留作服务端 / 将来客户端遥测刚需时、接缝后的可选实现。**接缝先行的价值在此兑现**：试错第三方库的代价被压到「删依赖」，内核与业务代码零改动。

## 五件套落地

- ① ADR：本文。
- ② 接口在内核、实现在模块：`Core/Logging/`（门面 + `ILogSink` + 两个默认 sink）✅；`Game.Framework.Logging.ZLogger/`（可选 sink 模块）——阶段 B 实测放弃、未落地（见「实测复盘」）。
- ③ 测试：接缝多播 / 过滤 / `LogEntry` 短路纯 C# 可测；`FileLogSink` 落盘往返；ZLogger sink 往返（模块落地后）。
- ④ demo：日志是内部基础设施、无业务 API——参照 ADR-0026（诊断面板）「demo 章不适用」，guide 章节 + 现有 demo 场景即覆盖；若 ZLogger 模块落地，可在进阶章补一个「接文件 / 结构化 sink」的活样板。
- ⑤ guide 章节 + AGENTS 规则（`Assets/Game/AGENTS.md` 新增「日志」条）。
