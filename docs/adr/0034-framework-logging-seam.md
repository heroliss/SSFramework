# ADR-0034：框架日志接缝 —— 内核 ILogSink 多播 + Console/File sink + Unity 日志桥

**Status:** Accepted（阶段 A 接缝 + 阶段 C 门面通用化，2026-07-14）——ZLogger 客户端模块（原规划的阶段 B）经实测放弃（依赖过重，见 §Decision 3「实测复盘」），服务端直接用；接缝已为将来接入留位。阶段 C 把门面从「框架内部诊断」升为「框架与业务共用的通用日志」，并补上零分配 / 全量捕获两块，见 §Decision 6。

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

### 6. 阶段 C（2026-07-14）：门面通用化 —— `FrameworkLog` → `Log`

> 「阶段 B」原指 ZLogger 可选模块（见 §Decision 3，实测后放弃）；本节是接缝落地后的下一步，故记为阶段 C。

阶段 A 的门面定位是「框架内部诊断」，放在 `Game.Framework.Internal`。但目标本就是「业务新代码也走接缝」——而 `Internal` 这个命名空间在向所有人喊「别用我」，与目标直接矛盾。阶段 B 把它升为**框架与业务共用**的通用日志门面。

**① 重命名 + 搬家**：`Game.Framework.Internal.FrameworkLog` → `Game.Framework.Logging.Log`。方法名 `Info` / `Warning` / `Error` / `Trace`（先例：Serilog 的 `Log.Information`、Unity 官方 `com.unity.logging` 包的 `Log.Info`）。旧 `LogVerbose` 别名删除（调用点全部迁移，共 8 处）。

**② 参数形状：重新设计，不对齐 `Debug.Log`，也不做兼容层。**
`Debug.Log(object)` 有三个硬伤——`object` 参数（装箱）、无惰性求值、除级别外无任何维度（无 category / context / 结构化）。对齐它等于把三个伤一起继承过来。也**不做 `using Debug = Log` 的 alias 迁移法**：它靠一个别处的 `global using` 隐形改写 `Debug.Log` 的语义（正是 #6「`System` 段劫持」烧过一次的那类隐式解析惊喜），且 alias 掉 `Debug` 就得连 `DrawLine` / `Break` / `isDebugBuild` 一起转发——一个日志门面上挂画 gizmo 的方法，架构上说不通。迁移直接改调用点（AI 批量替换成本极低），换来的是**调用点明写着 `Log.`，读代码的人一眼知道走了框架**。

**③ `Trace` 走 C# 10 插值字符串处理器（本阶段最大的收获）**：`Log.Trace($"[Container] REGISTER {type.Name}")`。
- **动机是一个真实存在的浪费**：阶段 A 的 `#if` 写在**方法体内**，发布版方法体是空的，但**调用点的实参照样求值**——`Trace($"解析 {type.Name} 耗时 {ms}ms")` 在 Verbose 关时仍会拼字符串、调 `ToString()`、分配内存，然后丢弃。容器每解析一次就白拼一个字符串。
- **处理器把守卫下沉到编译期**：编译器把 `$"..."` 改写成一串 `Append` 调用，外裹 `if (shouldAppend)`（值来自处理器构造函数里的 `Log.IsEnabled`）。级别没开 → 整块跳过 → 表达式一次都不求值。
- **代价（唯一的）**：求值语义变了——插值参数里的副作用（`i++`）在级别没开时不执行。但这与手写 `if (Verbose) Trace(...)` 是**完全相同**的语义，而「日志开不开会改变程序行为」本身就是 bug，故此语义是刻意接受的，并写进 AGENTS #34 与 XML doc。
- 另叠 `[Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]`：发布版整个调用连同实参从 IL 中删除，比「方法体空转」更彻底。
- **依赖**：Unity BCL（netstandard2.1 档）没有 `InterpolatedStringHandlerAttribute`（实测确认），框架自带一份 `internal` polyfill——R3 / ObservableCollections / Roslyn 自己都是这么做的（实测均为 `internal`）。**跨程序集可用性已实测**：`Game.Framework.Test` / `Asset.Yoo` 都不声明 polyfill，仍能正确绑到处理器重载（`LoggingTests.Trace_Interpolation_IsLazy_WhenDisabled` 就是这条的回归测试）。
- **顺带**：ZLogger 的两大卖点之一「零分配」我们自己拿到了，进一步坐实了「客户端不引 ZLogger」的决定。

**④ `[HideInCallstack]` 是前提、不是可选，且必须**全链**覆盖**：任何「包一层 `Debug.Log`」的门面，若不标它，Console 双击日志会跳进门面的转发方法而不是真正的调用点——这一条足以让所有人退回裸 `Debug.Log`，是此类封装最常见的死因。

⚠ **踩到的坑**：Unity 的规则是「从 `Debug.Log` 那帧往外走，**跳过所有标了该特性的帧，停在第一个没标的帧**」。所以只标最外层门面**不够**——实测（读 `UnityEditor.LogEntries` 的 `file`/`line`，那正是双击真正打开的位置）当时双击落在 `UnityDebugLogSink.cs:44`：`Log.Info` 被跳过了，但链上的 `Log.Dispatch` 与 `UnityDebugLogSink.Log` 没标，Unity 就停在了后者。补齐这两层后，门面日志与裸 `Debug.Log` 的定位结果**完全一致**。
（`in` 参数在接口实现处会生成一个 `modreq` 桥接帧、无法标注，但它没有调试信息，Unity 做 file/line 解析时天然跳过，不影响。）
症状只有人肉双击才看得见，故用 `LoggingTests.EntireForwardingChain_IsHiddenFromCallstack` 反射断言全链已标——将来给链条加层（新 sink 包装 / 装饰器）忘了标，测试会红。

**⑤ 接管 Unity 日志流 `Log.CaptureUnityLogs()`（补上最大的缺口）**：订阅 `Application.logMessageReceivedThreaded`，把**引擎报错、第三方包日志（YooAsset / UniTask / R3）、业务裸 `Debug.Log`、未捕获异常**全部灌进 sink。
- **动机**：阶段 A 的 `FileLogSink` 只收显式调用门面的日志——玩家崩在 `NullReferenceException` 上时，那条崩溃**根本不在日志文件里**，而它恰恰最该捞到。
- 它也**大幅降低了「迁移调用点」的紧迫性**：裸 `Debug.Log` 照样进文件/遥测，迁移只为拿更好的 API（category / context / 结构化 / Trace 门控），可以慢慢来。
- **防回声**：`UnityDebugLogSink` 转发的 `Debug.Log` 会触发桥接回调 → 不拦就重复落盘 + 坏 sink 的告警无限递归。用 `[ThreadStatic]` 标记「本线程正在由框架往 Console 写」让回调忽略；桥接条目标 `LogEntry.FromUnity`，`UnityDebugLogSink` 跳过（Console 里已有）。用 ThreadStatic 而非普通静态：`logMessageReceivedThreaded` 在**产生日志的那个线程**上同步回调，而框架日志可能来自任意线程。

**⑥ 补齐两处实用信息**：`LogEntry.Context`（`UnityEngine.Object`——点 Console 高亮定位场景物体，Unity 独有的实用能力）；`LogEntry.StackTrace`（`Error` 且无异常时自动补抓——落盘的 error 若既无异常又无栈，事后只剩一句话、无从定位）。`FileLogSink` 每次开档写**会话头**（设备 / 系统 / 版本 / 时间）：日志追加叠加，没有分隔就分不清哪段是哪次运行、玩家用的什么机器。

`LogEntry.Exception` **不只属于 Error**：能降级、丢弃坏输入或继续清理的失败仍是 Warning，但同样需要把原始异常交给文件 / 遥测 sink，不能提前压成 `Exception.Message`。这类少数场景走现有 `Log.Write(LogLevel.Warning, ..., exception: e)`，不为对称性再加一个可能与 `Warning(message, category)` 产生 `null` 重载歧义的便利方法。默认 `UnityDebugLogSink` 把异常附在同一条 Warning 后，既保留详情又不额外制造 Error；Error 仍单独调用 `Debug.LogException` 保持 Unity 原生异常定位。

**⑥.5 `Verbose` 布尔被级别体系吸收 → 收敛成全局 `Log.MinLevel`（默认 `Info`）。**

`Verbose` 是 sink/`MinLevel` 体系（阶段 A）**出现之前**就有的老开关，级别体系落地后它其实已经被吸收了，只是没人回头清理——两者**同构**：

- `Verbose = false` ≡ 「所有 sink 的 `MinLevel` ≥ `Info`」
- `Verbose = true` ≡ 「至少一个 sink 的 `MinLevel` ≤ `Trace`」

并存不只是冗余，而是**有害**：`UnityDebugLogSink.MinLevel` 默认是 `Trace`（全收），于是面板上会出现「sink 明明写着接收 Trace，但发 Trace 就是不出现」——用户去调那个下拉，调了也没用，真正挡住它的是旁边那个布尔。**讽刺的是：正是把诊断面板做出来（⑧），才让这个藏了很久的重复变得刺眼**——此前 `Verbose` 在菜单里、`MinLevel` 在代码里，两者不照面。

改为**一个概念（级别）、两个作用域**：全局 `Log.MinLevel`（总闸，短路掉连 `LogEntry` 都不构造）+ 各 sink 的 `MinLevel`（分闸，路由），串联。这正是 Serilog / MS.Extensions.Logging 的模型。附带获得原来做不到的能力：`Log.MinLevel = Warning` 可全局压掉 Info 噪音，不必逐个改 sink。

「开 Verbose」只是「把总闸门放行到 `Trace`」的俗称，菜单名保留（大家嘴里就是这么叫的），但**API 里不再有 `Verbose` 这个概念**。惰性求值不受影响：`IsEnabled` 本就在扫 sink 的 `MinLevel`，换个判断条件而已。

**⑦ 仍然刻意不做**：**消息模板**（Serilog / MEL 的 `"处理了 {Count} 条"`，占位符自动变结构化字段）。它是服务端共识，但客户端几乎不产结构化日志（正是不上 ZLogger 的同一条理由），为它自研模板解析 + 缓存不划算。要结构化就 `Log.Write(level, msg, fields)` 显式传。

**⑧ 编辑器可观测 + 可就地改（`Log.Sinks` / `Log.IsCapturingUnityLogs` + 诊断面板日志栏）**：sink 与 `CaptureUnityLogs` 都是业务在**启动期用代码**装配的（§3 决定：显式注册、不走配置资产），代价有两层——**编辑器里完全看不见**（「我的日志怎么没落盘？」无从判断是压根没装、还是被 `MinLevel` 卡掉了），而且**想临时调一下就得改代码 + 重进 Play**。

故补两个只读自省 API（`Log.Sinks` / `Log.IsCapturingUnityLogs`），并在「框架诊断面板」顶部加一栏，三样都**可读可改**：
- **全局级别 下拉**（总闸门 `Log.MinLevel`，4 档）——经 `FrameworkLogMenu.SetMinLevel` 写入本次 Editor 会话并在域重载后恢复。2026-08-28 起按 ADR-0043 移除直接改状态的顶部菜单，人工入口收敛在运行时诊断窗口，避免菜单误触和两处交互漂移。
- **接管 Unity 日志流** 勾选框——`CaptureUnityLogs` 本就幂等、可随时开关。
- **每个 sink 的 MinLevel 下拉**——典型用法：想把这次复现的细粒度日志抓进文件，把文件 sink 调到 `Trace` + 总闸门放行到 `Trace` 即可，不必改代码重进 Play。
- 无 sink 时红字「日志无处可去！」。

`MinLevel` 在 `ILogSink` 上**刻意保持只读**（不强迫所有 sink 可变——固定级别的 sink 只有 getter 是合理的），面板改而在**具体类型**上反射找可写的 `MinLevel`：找得到给下拉、找不到只读显示。反射只发生在 sink 组成变化时、且按类型缓存。

面板改动**立即生效但不持久**——下次运行仍由业务启动代码决定，面板不悄悄改变正式行为。

**这一栏刻意不做的**：一键装/卸 sink（会让「日志去哪」变成两个真源：代码 + 面板，正是 §3 要避免的）。

**刻意不加的日志菜单**（想过但否掉）：① Console 级别过滤——**Unity Console 自带 Log/Warning/Error 过滤按钮**，重复造轮子；② 编辑器内一键开文件日志——编辑器里 Unity **已经把全量日志写进 `Editor.log`**，文件 sink 的战场是玩家包而玩家包没有菜单；③ 日志配置 ScriptableObject——与 §3「显式注册」直接冲突，两行 bootstrap 比「配置藏在 SO 里被隐式读取」清晰；④ 「日志自检」菜单——demo 章已覆盖。（持久化目录由 `FrameworkPathBrowserWindow`，即 `SSFramework/开发辅助/常用目录` 统一说明和打开。）

## Consequences

- 日志获得可替换接缝：按模块过滤 / 静音、落文件、测试捕获断言、遥测重定向，全部有了统一着力点；`FrameworkLog` 从「一个 bool」长成真正的日志门面。
- **内核零新增依赖、Console 观感与定位不变**；「落文件」由内核 `FileLogSink` 零依赖兜底，覆盖绝大多数客户端排查场景。
- ZLogger 成为**可选升级**：客户端默认不吞 `Microsoft.Extensions.Logging` DLL；要结构化 / 遥测时按需接入，且服务端（Outpost `Server~/` 已是 ASP.NET Core）能与客户端共用同一套日志抽象心智。
- 原有 180 处 `Debug.Log` 采用按 Module 渐进迁移，没有一次性大改风险。到 2026-08-30，Core 与可选 Runtime Adapter 中除 Logging Implementation 自身外已全部收敛；`FrameworkSelfCheck` 保留 Unity context，`LoggingCommandSystem` 的可选 Console echo 也穿过同一 Seam。AOT Boot 继续按下述隔离理由保留原生日志，Editor 工具与 Demo 的“裸日志桥接”实验不伪装成 Runtime 缺口。`CaptureUnityLogs()` 仍保证引擎与第三方原生日志进入 sink。
- UI Core、UGUI、Toolkit 与融合 Bridge 的 Runtime 配置错误和 hook 异常已迁入 Seam；hook 日志补上窗口类型与阶段，UGUI 绑定错误保留窗口 context。迁移过程同时修正了 UGUI Adapter 仅在文案声明、却未真正执行的窗口基类校验，说明按 Module 收敛的价值不只是统一写法，还能让错误语义与真实 Interface 契约对齐。
- Asset Core 与 Yoo Adapter 的 Runtime 失败证据已迁入同一 Seam：Core 输入守卫在第三方工作前 fail-fast，`AssetUtility` 携带 Unity context，Yoo 加载失败保留独立 Adapter category，初始化 owner 保留原始 exception；YooAsset 自身日志仍由 Unity 日志桥按需接管，不重复包装。
- Audio Runtime 的淡变/回收驱动异常与 Dispose 后误用已迁入 Seam，异步异常保留 exception 和可用的 Unity context。Config Runtime 的清单 / 资源 / 表构造失败也由同一 Seam 记录具体服务类型、根 exception 与组件 context，并把原始异常另交给 `EnsureReady` 调用者；日志不再替代失败语义。AOT `Game.Framework.Boot` 则明确保留原生 `Debug.*`：它在框架与热更程序集加载前自举，asmdef 刻意不引用 `Game.Framework`；为统一写法反向依赖 Core 会破坏 Boot Module 的隔离，另造一套启动日志门面也没有 Leverage。
- **阶段 C 的净收益**：门面对业务开放（`Log.Info` / `Log.Error`）；`Trace` 关掉时**真·零成本**（插值处理器，实测回归覆盖）——顺带自己拿到了 ZLogger 两大卖点之一的「零分配」，坐实客户端不引它；`CaptureUnityLogs` 补上「玩家崩溃不在日志文件里」这个最大缺口；`[HideInCallstack]` 保住双击定位（否则这类门面必然被弃用）。代价是 `Trace` 插值参数不得有副作用（已入 AGENTS #34 + XML doc）。
- **依赖引入分界**：接缝 + 两个内核 sink（阶段 A）零第三方依赖、**已落地**。ZLogger 模块（阶段 B）实测后**放弃**（依赖 ≈ 1.4 MB，见上「实测复盘」）——客户端框架保持零第三方日志依赖，ZLogger 留作服务端 / 将来客户端遥测刚需时、接缝后的可选实现。**接缝先行的价值在此兑现**：试错第三方库的代价被压到「删依赖」，内核与业务代码零改动。

## 五件套落地

- ① ADR：本文。
- ② 接口在内核、实现在模块：`Core/Logging/`（`Log` 门面 + `ILogSink` + 两个默认 sink + `UnityLogBridge` + 插值处理器 & polyfill）✅；`Game.Framework.Logging.ZLogger/`（可选 sink 模块）——实测放弃、未落地（见「实测复盘」）。
- ③ 测试：`LoggingTests`（PlayMode）✅ 覆盖多播 / per-sink `MinLevel` / `IsEnabled` / Trace 门控 / **插值惰性求值（兼跨程序集处理器识别的回归测试）** / 异常与自动抓栈 / `context` 透传 / sink 异常隔离 / **Unity 日志流桥接 + 防回声** / `FileLogSink` 落盘·会话头·滚动；`UIRuntimeLoggingTests` 穿过 UI Adapter 验证 category、context 与 fail-fast 副作用顺序；`DiagnosticsTests` 锁定命令 echo 的消息与 category；`AssetOperationCoordinationTests` / `YooAssetLoadTests` 则锁定资源 Core 守卫、初始化根异常与 Yoo Adapter 分类。
- ④ demo：能力章「日志 · 分级 + 可插拔 sink」（`LoggingDemoModule`）✅——装 demo 捕获 sink 看多播、调其 `MinLevel` 看每 sink 独立过滤、**用一个计数器亲眼验证「Verbose 关时插值表达式一次都没求值」**、点「发一条裸 `Debug.LogError`」看它经桥接进入 sink、装 `FileLogSink` 看落盘、`Write(fields)` 看结构化字段。（原判「无业务 API、参照 ADR-0026 诊断面板 demo 不适用」——后修正：门面/sink 虽是基础设施，但「可替换接缝 + 广播 + 分级 + 惰性求值」这套心智值得一个可点的章，尤其惰性求值这种「看不见的行为」，用计数器演示远胜纯文字。）
- ⑤ guide §28 + AGENTS #18 / #34 ✅。
