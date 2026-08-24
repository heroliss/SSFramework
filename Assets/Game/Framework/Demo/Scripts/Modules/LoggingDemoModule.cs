using System;
using System.Collections.Generic;
using System.IO;
using Game.Framework.Demo.Core;
using Game.Framework.Logging;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·日志：框架统一日志门面 <see cref="Log"/>（分级记录 + 广播到一组可插拔 <see cref="ILogSink"/>）。
    /// 定位是「日志有一层可替换的接缝」——按级别 / 来源过滤、落文件、测试捕获、遥测重定向都在这一层着力，
    /// 而不是把 <c>Debug.Log</c> 散落一地、事后无从拦截（ADR-0034）。
    /// 本章把三件不可见的事做成可见：① 多播（装捕获 sink 看同一条日志两处落地）；
    /// ② 插值惰性求值（总闸门没放行到 Trace 时表达式根本不求值，用计数器证明）；
    /// ③ 接管 Unity 日志流（裸 <c>Debug.LogError</c> / 引擎报错也能进文件）。
    /// </summary>
    public sealed class LoggingDemoModule : DemoModuleBase
    {
        public override string Id => "logging";
        public override string Title => "日志 · 分级 + 可插拔 sink";
        public override string Category => "能力";
        public override int Order => 35;   // 排在「本地存储(30)」「音频(40)」之间，归到基础设施类 Utility
        public override string Summary =>
            "Log 提供统一分级门面，将记录广播到 Console、文件或遥测 sink；全局与 sink 两级过滤，Trace 未启用时不构造消息。" +
            "它还能接管 Unity/第三方日志并零依赖落盘，设计权衡见 ADR-0034。";

        // 本章发出的日志统一打这个 category，便于和框架内部日志区分（也演示 category 的用法）。
        private const string DemoCategory = "Demo";

        /// <summary>
        /// demo 捕获 sink：把收到的每条日志回调出去喂给「捕获面板」。演示两件事——
        /// ① <see cref="ILogSink"/> 是可插拔接缝（<c>AddSink</c> 就能让日志多一个去向）；
        /// ② 每个 sink 自带 <see cref="MinLevel"/>、独立过滤（面板可只留 Warning+，而 Console 照收全部）。
        /// </summary>
        private sealed class CapturingSink : ILogSink
        {
            private readonly Action<LogEntry> _onLog;
            public LogLevel MinLevel { get; set; }

            public CapturingSink(LogLevel minLevel, Action<LogEntry> onLog)
            {
                MinLevel = minLevel;
                _onLog = onLog;
            }

            // ⚠ 契约：Log 可能被任意线程调用（如网络后台线程记日志）。本 demo 的日志都由主线程触发，
            // 故直接回调更新 UI 是安全的；真实的跨线程 sink（如文件）要自行加锁（见 FileLogSink）。
            public void Log(in LogEntry entry) => _onLog(entry);
        }

        // 插值惰性求值的探针：被求值就自增。用它证明「总闸门没放行到 Trace 时 $"..." 里的表达式一次都没跑」。
        private int _touchCount;

        private string Touch()
        {
            _touchCount++;
            return "我被求值了";
        }

        public override void Build(DemoModuleHost host)
        {
            // 进入本章时的全局日志状态快照，切走本章时原样恢复：Log 是进程级静态门面，
            // demo 不能把它留在「装着捕获 sink / 总闸门开着 / 接管着 Unity 日志流」的脏状态里污染其它章。
            var prevMinLevel = Log.MinLevel;

            CapturingSink capturing = null;    // 当前装着的捕获 sink（null = 没装）
            FileLogSink fileSink = null;       // 当前装着的文件 sink（null = 没装）
            bool capturingUnity = false;       // 是否已接管 Unity 日志流
            var captured = new List<string>(); // 捕获面板的行缓冲（只留最近若干行）

            // ── 定位 ──
            host.AddSectionTitle("定位：统一门面 + 广播到可插拔 sink");
            host.AddNote("框架和业务**共用同一个入口** `Log`：分级记录（`Trace` / `Info` / `Warning` / `Error`），再**广播**给一组可插拔 `ILogSink`（Console / 文件 / 遥测…）。价值是「日志有一层可替换的接缝」——按级别 / 来源过滤、落文件捞日志、测试期捕获断言、重定向遥测，全在这一层着力，而不是把 `Debug.Log` 散落一地、事后无从拦截。",
                new CodeRef("Assets/Game/Framework/Core/Logging/Log.cs", "public static class Log", "日志门面"));
            host.AddSubNote("为什么是**静态**门面而非 DI 服务：日志要在**任何地方**可用，包括身处 DI 之下、没有 `Context` 的内核基础设施（`Container` / 构造期）——它们不能反向依赖容器去取 logger。所以门面静态、出厂即用（默认装一个转 `Debug.Log` 的 `UnityDebugLogSink`，Console 观感 / 双击定位 / 堆栈全不变）。");
            host.AddSubNote("**双击定位**能保住，靠的是门面方法上的 `[HideInCallstack]`：没有它，Console 里双击任何一条日志都会跳进框架的转发方法、而不是你真正的调用点——这是所有「包一层 Debug.Log」的门面最常见的死因。");

            // ── 分级门面 ──
            host.AddSectionTitle("分级门面：Info / Warning / Error（看 Unity Console）");
            var levelLabel = host.AddValueDisplay("点下面按钮 → 看 Unity Console：门面把日志转给默认 sink，观感 / 定位与裸 Debug.Log 完全一致。");
            levelLabel.style.whiteSpace = WhiteSpace.Normal;
            host.AddActionRow("Info", () =>
            {
                Log.Info("玩家进入战斗", DemoCategory);
                levelLabel.text = "已发一条 Info（Console 里是普通 Log）。第二参 category 可选——给结构化 sink 分组 / 过滤用。";
            }, CodeRef.Here("Log.Info(\"玩家进入战斗\"", "Info"));
            host.AddActionRow("Warning", () =>
            {
                Log.Warning("配置缺省，回退默认值", DemoCategory);
                levelLabel.text = "已发一条 Warning（Console 里是黄色警告）。";
            }, CodeRef.Here("Log.Warning(\"配置缺省", "Warning"));
            host.AddActionRow("Error（自动补抓堆栈）", () =>
            {
                Log.Error("存档写入失败", category: DemoCategory);
                levelLabel.text = "已发一条 Error。注意：没带异常的 Error，门面会**自动补抓调用栈**存进 LogEntry.StackTrace——落盘的 error 若既无异常又无栈，事后只剩一句话、根本没法定位。";
            }, CodeRef.Here("Log.Error(\"存档写入失败\"", "Error"));
            host.AddActionRow("Error + 异常", () =>
            {
                Log.Error("存档反序列化失败", new InvalidOperationException("bad json"), DemoCategory);
                levelLabel.text = "已发 Error + 异常：异常自带堆栈，故不再补抓。默认 sink 额外走一次 Debug.LogException 保留 Unity 的定位能力。";
            }, CodeRef.Here("new InvalidOperationException(\"bad json\")", "Error + 异常"));
            host.AddActionRow("Info + context（点 Console 高亮场景物体）", () =>
            {
                var assets = UnityEngine.Object.FindFirstObjectByType<DemoPoolAssets>();
                Log.Info("这条日志挂了个 context——去 Console 点它，Hierarchy 会高亮到对应物体", DemoCategory, context: assets);
                levelLabel.text = assets != null
                    ? "已发送。去 Unity Console **点这条日志**，Hierarchy 里会高亮定位到那个 GameObject——这是 Unity 独有的实用能力（等价 Debug.Log(msg, context) 的第二参）。"
                    : "场景里没找到可用作 context 的物体，但 API 用法就是第三参 context。";
            }, CodeRef.Here("context: assets", "context 参数"));

            // ── 两道闸门 ──
            host.AddSectionTitle("两道闸门：全局 MinLevel（总闸）+ 每个 sink 的 MinLevel（分闸）");
            host.AddNote("一条日志要送达某个 sink，得**同时**过两道：**总闸门** `Log.MinLevel`（全局，默认 `Info`）和该 sink 自己的**分闸门** `MinLevel`。这是**一个概念（级别）、两个作用域**，与 Serilog / MS.Extensions.Logging 的模型一致。",
                new CodeRef("Assets/Game/Framework/Core/Logging/Log.cs", "public static LogLevel MinLevel", "总闸门"));
            host.AddSubNote("**为什么不是一个 `Verbose` 布尔**：早期确实是。但 sink + `MinLevel` 体系落地后它就被吸收了——「`Verbose=false`」≡「所有 sink 的 `MinLevel` ≥ `Info`」，两者做的是同一件事。并存反而制造陷阱：sink 明明写着接收 `Trace`，日志却被另一个布尔挡着，怎么调都不出来。收敛成单一的级别概念后，串联关系一目了然。附带好处：`Log.MinLevel = Warning` 可**全局压掉 Info 噪音**，这是原来做不到的。");

            // ── Trace + 惰性求值（核心） ──
            host.AddSectionTitle("Trace：诊断噪音，且「关掉时真·零成本」");
            var traceLabel = host.AddValueDisplay();
            traceLabel.style.whiteSpace = WhiteSpace.Normal;
            var touchLabel = host.AddValueDisplay();
            touchLabel.style.whiteSpace = WhiteSpace.Normal;

            void RefreshTrace()
            {
                traceLabel.text = $"总闸门 Log.MinLevel = {Log.MinLevel}　｜　Trace 要送达，总闸门得放行到 Trace（俗称「开 Verbose」），且仅 Editor / Development 构建。";
                touchLabel.text = $"插值表达式被求值的次数：{_touchCount}　（总闸门没放行到 Trace 时点「发一条插值 Trace」，这个数不该涨）";
            }
            RefreshTrace();

            host.AddActionRow("总闸门放行到 Trace（= 开 Verbose）", () =>
            {
                Log.MinLevel = LogLevel.Trace;
                RefreshTrace();
            }, CodeRef.Here("Log.MinLevel = LogLevel.Trace", "开总闸门"));
            host.AddActionRow("总闸门收回到 Info（默认）", () =>
            {
                Log.MinLevel = LogLevel.Info;
                RefreshTrace();
            }, CodeRef.Here("Log.MinLevel = LogLevel.Info", "收总闸门"));
            host.AddActionRow("发一条插值 Trace（含一个会计数的表达式）", () =>
            {
                // ★ 本章最值钱的一行：Trace 没放行时，Touch() 根本不会被调用——编译器在调用点插了 if (shouldAppend) 守卫。
                Log.Trace($"诊断噪音：{Touch()}", DemoCategory);
                RefreshTrace();
            }, CodeRef.Here("Log.Trace($\"诊断噪音：{Touch()}\"", "插值 Trace（惰性求值）"));
            host.AddActionRow("重置计数", () =>
            {
                _touchCount = 0;
                RefreshTrace();
            });

            host.AddNote("**先在总闸门 = `Info`（默认）时连点几次「发一条插值 Trace」——求值次数纹丝不动。再把总闸门放行到 `Trace` 去点，它才开始涨。** 这就是插值字符串处理器（C# 10）：`Log.Trace($\"...\")` 的参数不是先拼好的字符串，而是被编译器改写成一串 `Append` 调用，外面裹着一个 `if (级别放行吗)` 守卫。级别没放行 → 整块跳过 → **表达式一次都不求值、字符串一个字符都不拼**。",
                new CodeRef("Assets/Game/Framework/Core/Logging/TraceInterpolatedStringHandler.cs", "public ref struct TraceInterpolatedStringHandler", "插值处理器实现"));
            host.AddSubNote("对比普通 `string` 参数：`Log.Trace($\"解析 {type.Name} 耗时 {ms}ms\")` 会**先把字符串拼好**，进到方法里才发现级别没放行、直接丢弃——白拼、白分配。容器每解析一次就白拼一个字符串，这是真实存在、天天在发生的浪费。框架内 `Container` / `YooAssetProvider` 的诊断日志现在都走这条惰性路径。");
            host.AddSubNote("⚠ **唯一要守的纪律**：惰性意味着求值语义变了——参数里只放**纯读取**（属性、`ToString()`、拼字符串），**不要放有副作用的表达式**（`i++` / `list.Pop()`），因为级别没放行时它们不会执行。这与手写 `if (Log.IsEnabled(LogLevel.Trace)) Log.Trace(...)` 是完全相同的语义，处理器只是把守卫自动化了。");
            host.AddSubNote("发布版里 `Trace` 调用连同实参**整个从 IL 中删除**（`[Conditional(\"UNITY_EDITOR\")]` + `[Conditional(\"DEVELOPMENT_BUILD\")]`），比「方法体空转」更彻底。也可在 Editor 菜单 `SSFramework/诊断/日志级别` 或「框架诊断面板」顶部的日志栏直接调总闸门。");

            // ── 可插拔 sink ──
            host.AddSectionTitle("接缝：装一个捕获 sink，看多播 + 每 sink 自带 MinLevel");
            var panel = host.AddValueDisplay("捕获面板（空）：装上 demo 捕获 sink 后，同一条日志会**同时**进 Unity Console 和这里——这就是「多播」。");
            panel.style.whiteSpace = WhiteSpace.Normal;
            panel.enableRichText = false; // 捕获到的日志正文可能含任意字符，关富文本免得被当标签吞掉
            var sinkLabel = host.AddValueDisplay();
            sinkLabel.style.whiteSpace = WhiteSpace.Normal;

            void RefreshSink() => sinkLabel.text = capturing == null
                ? "当前没装捕获 sink。发的日志只进默认 Console sink。"
                : $"已装捕获 sink，MinLevel = {capturing.MinLevel}。低于它的级别不会进捕获面板，但仍进 Console——每个 sink 独立过滤。";

            // 捕获 sink 的回调：把一条 LogEntry 格式化进面板缓冲（只留最近 8 行）。
            void AppendCaptured(LogEntry e)
            {
                string cat = e.Category != null ? $"[{e.Category}] " : "";
                string from = e.FromUnity ? " ⟨来自 Unity 日志流⟩" : "";
                string ex = e.Exception != null ? $" ⟨{e.Exception.GetType().Name}⟩" : "";
                string stack = e.StackTrace != null ? " ⟨含堆栈⟩" : "";
                string fields = "";
                if (e.Fields != null && e.Fields.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in e.Fields) parts.Add($"{kv.Key}={kv.Value}");
                    fields = " {" + string.Join(", ", parts) + "}";
                }
                captured.Add($"{e.TimestampUtc.ToLocalTime():HH:mm:ss} [{e.Level}] {cat}{e.Message}{ex}{stack}{fields}{from}");
                if (captured.Count > 8) captured.RemoveAt(0);
                panel.text = "捕获面板（最近 8 条）：\n" + string.Join("\n", captured);
            }
            RefreshSink();

            host.AddActionRow("装捕获 sink（AddSink，MinLevel=Trace 全收）", () =>
            {
                if (capturing != null) return;
                capturing = new CapturingSink(LogLevel.Trace, AppendCaptured);
                Log.AddSink(capturing);
                RefreshSink();
            }, CodeRef.Here("Log.AddSink(capturing)", "AddSink 装 sink"));
            host.AddActionRow("把它 MinLevel 提到 Warning（过滤掉 Info/Trace）", () =>
            {
                if (capturing == null) return;
                capturing.MinLevel = LogLevel.Warning;
                RefreshSink();
            }, CodeRef.Here("capturing.MinLevel = LogLevel.Warning", "调 sink MinLevel"));
            host.AddActionRow("拆掉捕获 sink（RemoveSink）", () =>
            {
                if (capturing == null) return;
                Log.RemoveSink(capturing);
                capturing = null; // 操作按钮主动拆除
                RefreshSink();
            }, CodeRef.Here("capturing = null; // 操作按钮主动拆除", "RemoveSink"));
            host.AddNote("`ILogSink` 就一个 `Log(in LogEntry)` + 一个 `MinLevel`。`AddSink` 后同一条日志广播到每个 sink；每个 sink 按自己的 `MinLevel` 独立过滤——可让 Console 只留 `Warning+`、细粒度进文件。测试静音 / 捕获断言就靠 `ClearSinks()` + 自装一个收集 sink（见 `LoggingTests`）。",
                new CodeRef("Assets/Game/Framework/Core/Logging/ILogSink.cs", "public interface ILogSink", "sink 接缝契约"));
            host.AddSubNote("⚠ `ILogSink.Log` 可能被**后台线程**调用（如网络接收循环记日志）：持可变状态（文件句柄 / 缓冲）的 sink 要自行加锁（见 `FileLogSink`）。门面对 sink 列表用 copy-on-write，广播本身无锁。",
                CodeRef.Here("private sealed class CapturingSink", "demo 捕获 sink 实现"));

            // ── 接管 Unity 日志流 ──
            host.AddSectionTitle("接管 Unity 日志流：让引擎报错 / 第三方 / 裸 Debug.Log 也进 sink");
            var unityLabel = host.AddValueDisplay();
            unityLabel.style.whiteSpace = WhiteSpace.Normal;
            void RefreshUnity() => unityLabel.text = capturingUnity
                ? "已接管 ✓ 现在裸 Debug.Log* 也会进 sink（捕获面板 / 文件）。试试下面「发一条裸 Debug.LogError」。"
                : "未接管。此时裸 Debug.Log* **不会**进 sink——玩家崩溃的那个 NullReferenceException 根本不在你的日志文件里。";
            RefreshUnity();

            host.AddActionRow("接管 Unity 日志流（CaptureUnityLogs）", () =>
            {
                if (capturingUnity) return;
                Log.CaptureUnityLogs(true);
                capturingUnity = true;
                RefreshUnity();
            }, CodeRef.Here("Log.CaptureUnityLogs(true)", "接管 Unity 日志流"));
            host.AddActionRow("发一条裸 Debug.LogError（完全不走门面）", () =>
            {
                Debug.LogError("[裸 Debug] 我根本没调用 Log 门面");
                unityLabel.text = capturingUnity
                    ? "已发送。装了捕获 sink 的话，面板里能看到它，并标着「来自 Unity 日志流」——一行调用点都没改，它就进了 sink。"
                    : "已发送，但没接管 → 它只在 Console，进不了任何 sink。先点上面「接管」再试。";
            }, CodeRef.Here("Debug.LogError(\"[裸 Debug]", "裸 Debug.LogError"));
            host.AddActionRow("取消接管", () =>
            {
                if (!capturingUnity) return;
                Log.CaptureUnityLogs(false);
                capturingUnity = false; // 操作按钮取消接管
                RefreshUnity();
            }, CodeRef.Here("capturingUnity = false; // 操作按钮取消接管", "取消接管"));

            host.AddNote("`Log.CaptureUnityLogs()` 订阅 `Application.logMessageReceivedThreaded`，把 **Unity 自己的日志流**灌进 sink：不只是你的裸 `Debug.Log`，还包括**引擎级报错**（NullReferenceException、shader 错误）和**第三方包**（YooAsset / UniTask / R3）内部的日志。**一行调用点都不用改**，全量日志自动落盘 / 上报。不开的话，`FileLogSink` 只收显式调用门面的日志——而玩家崩溃时最该捞到的那条，恰恰不在里面。",
                new CodeRef("Assets/Game/Framework/Core/Logging/UnityLogBridge.cs", "internal static class UnityLogBridge", "Unity 日志流桥"));
            host.AddSubNote("**防回声**是这里的关键坑：`UnityDebugLogSink` 会把门面日志转发成 `Debug.Log`，而那次 `Debug.Log` 又会触发桥接回调——不拦就会重复落盘、甚至无限回环。桥用一个**线程私有**标记（`[ThreadStatic]`）记住「本线程此刻正在由框架往 Console 写」，回调见到就忽略；桥接来的条目标记 `LogEntry.FromUnity`，`UnityDebugLogSink` 直接跳过（Console 里已经有了），而文件 / 遥测 sink 照常收。");

            // ── 落文件 ──
            host.AddSectionTitle("落文件：FileLogSink（零依赖、会话头、error 带栈、自动滚动）");
            string logDir = Path.Combine(Application.persistentDataPath, "framework-logs");
            string logPath = Path.Combine(logDir, "demo.log");
            var fileLabel = host.AddValueDisplay();
            fileLabel.style.whiteSpace = WhiteSpace.Normal;
            void RefreshFile() => fileLabel.text = fileSink == null
                ? "没装文件 sink。"
                : $"已装文件 sink → {logPath}（Info 及以上落盘，超阈值自动按大小滚动、保留最近几份）。";
            RefreshFile();
            host.AddActionRow("装文件 sink（AddSink FileLogSink）", () =>
            {
                if (fileSink != null) return;
                fileSink = new FileLogSink(logPath, LogLevel.Info);
                Log.AddSink(fileSink);
                Log.Info("文件 sink 已装上，这条会落盘", DemoCategory);
                RefreshFile();
            }, CodeRef.Here("new FileLogSink(logPath", "装文件 sink"));
            host.AddActionRow("拆掉文件 sink（RemoveSink + Dispose 关句柄）", () =>
            {
                if (fileSink == null) return;
                Log.RemoveSink(fileSink);
                fileSink.Dispose();
                fileSink = null; // 操作按钮关闭句柄后清引用
                RefreshFile();
            }, CodeRef.Here("fileSink = null; // 操作按钮关闭句柄", "拆文件 sink"));
#if UNITY_EDITOR
            host.AddActionRow("打开日志目录（看 demo.log）", () =>
            {
                Directory.CreateDirectory(logDir);
                UnityEditor.EditorUtility.RevealInFinder(logDir);
            }, new CodeRef("Assets/Game/Framework/Core/Logging/FileLogSink.cs", "public sealed class FileLogSink", "文件 sink 实现"));
#endif
            host.AddNote("落文件是客户端最常用的需求（玩家包 / QA 捞日志 / 用户反馈）：纯 C# `StreamWriter` 追加 + 按大小滚动，**零依赖**——不必为了「写个日志文件」就吞下一串 DLL。每次开档写一段**会话头**（设备 / 系统 / 版本 / 时间）：日志是追加的，多次启动会叠在一起，没有这段分隔根本分不清哪段是哪次运行、玩家用的什么机器——而这恰恰是排查的第一步。`Error` 条目自动带**堆栈**。",
                new CodeRef("Assets/Game/Framework/Core/Logging/FileLogSink.cs", "private void WriteSessionHeader", "会话头"));

            // ── 结构化字段 ──
            host.AddSectionTitle("结构化字段：Write(level, msg, fields)");
            var structLabel = host.AddValueDisplay();
            structLabel.style.whiteSpace = WhiteSpace.Normal;
            structLabel.text = "结构化字段供 JSON / 遥测这类结构化 sink 分组检索；文本 sink（Console / 文件）忽略它。先装上面的捕获 sink，再点下面按钮看差别。";
            host.AddActionRow("发一条带字段的日志（Info + 3 字段）", () =>
            {
                var fields = new List<KeyValuePair<string, object>>
                {
                    new("userId", 42),
                    new("action", "purchase"),
                    new("amount", 9.99),
                };
                Log.Write(LogLevel.Info, "purchase", fields, DemoCategory);
                structLabel.text = capturing != null && capturing.MinLevel <= LogLevel.Info
                    ? "已发送：捕获面板里能看到 {userId=42, action=purchase, amount=9.99}；Console（文本 sink）只显示消息、忽略字段。"
                    : "已发送，但当前没有会收 Info 的结构化 sink——先「装捕获 sink」（MinLevel 别高于 Info）再点。";
            }, CodeRef.Here("Log.Write(LogLevel.Info, \"purchase\"", "结构化字段"));
            host.AddNote("`Log.Write(level, msg, fields, ...)` 是通用入口——绝大多数日志不带字段（此时热路径零额外分配）。`Info` / `Warning` / `Error` 便利方法覆盖 99% 场景，要结构化时走 `Write`，不必换 API。**刻意不做消息模板**（Serilog 的 `\"处理了 {Count} 条\"` 那套）：那是结构化日志的地盘，而 ADR-0034 已判定客户端几乎不产结构化日志。");

            // ── 扩展点 / 刻意不做 ──
            host.AddSectionTitle("扩展点与刻意不做");
            host.AddConcept("自定义去向 = ILogSink", "实现 `Log(in LogEntry)` + `MinLevel` 即可把日志导向任何后端（内存缓冲 / HTTP 遥测 / 平台原生日志 / 游戏内浮层）。⚠ 可能被后台线程调用，持可变状态自行加锁。");
            host.AddConcept("ZLogger 客户端不引", "零分配 / 结构化 JSON / HTTP 遥测评估过 Cysharp ZLogger，实测装它拖进 `System.Text.Json` 全家桶 ≈1.4MB、最大开销纯为客户端几乎不产的 JSON 日志——性价比不划算（ADR-0034 实测复盘）。零分配这一点我们用插值处理器已经拿到了。");
            host.AddConcept("服务端才是落点", "结构化 / 遥测的价值在服务端（Outpost `Server~/` 本就是 .NET，直接用 ZLogger、无包体顾虑）；客户端将来真有「结构化上报后台」刚需，实现一个 `ZLoggerLogSink : ILogSink` 接进来即可——接缝已留位、业务零改动。");

            host.AddTip("速记：新代码日志一律走 Log 门面、别裸 Debug.Log（裸的进不了文件 / 遥测 / 测试）——但真有漏网的（第三方 / 引擎），CaptureUnityLogs 会兜住。Trace 用插值 $\"...\" 写，关掉时零成本，但参数别放副作用。落文件 AddSink(new FileLogSink(...)) 启动配一次。深度见 framework-guide 日志章 / ADR-0034。");

            // 切走本章：拆掉 demo 装的 sink、取消接管、恢复总闸门——不给全局静态门面留脏状态。
            Bag.Add(Disposable.Create(() =>
            {
                if (capturing != null) Log.RemoveSink(capturing);
                if (fileSink != null) { Log.RemoveSink(fileSink); fileSink.Dispose(); }
                if (capturingUnity) Log.CaptureUnityLogs(false);
                Log.MinLevel = prevMinLevel;
            }));
        }
    }
}
