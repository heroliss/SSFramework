using System;
using System.Collections.Generic;
using System.IO;
using Game.Framework.Demo.Core;
using Game.Framework.Internal;
using Game.Framework.Logging;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·日志：框架统一日志门面 <see cref="FrameworkLog"/>（分级记录 + 广播到一组可插拔 <see cref="ILogSink"/>）。
    /// 定位是「日志有一层可替换的接缝」——按级别 / 来源过滤、落文件、测试捕获、遥测重定向都在这一层着力，
    /// 而不是把 <c>Debug.Log</c> 散落一地、事后无从拦截（ADR-0034）。
    /// 本章把「接缝」做成肉眼可见：装一个 demo 捕获 sink，同一条日志同时进 Unity Console 和右侧捕获面板，
    /// 演示多播 + 每个 sink 自带 <see cref="ILogSink.MinLevel"/> 独立过滤，再看落文件与结构化字段。
    /// </summary>
    public sealed class LoggingDemoModule : DemoModuleBase
    {
        public override string Id => "logging";
        public override string Title => "日志 · 分级 + 可插拔 sink";
        public override string Category => "能力";
        public override int Order => 35;   // 排在「本地存储(30)」「音频(40)」之间，归到基础设施类 Utility
        public override string Summary =>
            "FrameworkLog 静态门面：Trace/Info/Warning/Error 分级记录，广播到一组可插拔 ILogSink（Console / 文件 / 遥测）。" +
            "Trace 受 Verbose + 仅 Editor/Dev 双重门控；每个 sink 自带 MinLevel 独立过滤；落文件零依赖。" +
            "结构化 / 遥测评估过 ZLogger，实测客户端不引（依赖 ≈1.4MB），接缝已为服务端 / 将来留位。ADR-0034。";

        // 本章发出的日志统一打这个 category，便于和框架内部日志区分（也演示 category 的用法）。
        private const string DemoCategory = "Demo";

        /// <summary>
        /// demo 捕获 sink：把收到的每条日志回调出去喂给右侧「捕获面板」。演示两件事——
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

            // ⚠ 契约：Log 可能被任意线程调用（如网络后台线程记日志）。本 demo 的日志都由主线程点按钮触发，
            // 故直接回调更新 UI 是安全的；真实的跨线程 sink（如文件）要自行加锁（见 FileLogSink）。
            public void Log(in LogEntry entry) => _onLog(entry);
        }

        public override void Build(DemoModuleHost host)
        {
            // 进入本章时的全局日志状态快照，切走本章时原样恢复：FrameworkLog 是进程级静态门面，
            // demo 不能把它留在「装着捕获 sink / Verbose 开着」的脏状态里污染其它章。
            bool prevVerbose = FrameworkLog.Verbose;

            CapturingSink capturing = null;    // 当前装着的捕获 sink（null = 没装）
            FileLogSink fileSink = null;       // 当前装着的文件 sink（null = 没装）
            var captured = new List<string>(); // 捕获面板的行缓冲（只留最近若干行）

            // ── 定位 ──
            host.AddSectionTitle("定位：统一门面 + 广播到可插拔 sink");
            host.AddNote("框架内所有诊断输出走 `FrameworkLog` 一个静态门面：分级记录（`Trace` / `Info` / `Warning` / `Error`），再**广播**给一组可插拔 `ILogSink`（Console / 文件 / 遥测…）。价值是「日志有一层可替换的接缝」——按级别 / 来源过滤、落文件捞日志、测试期捕获断言、重定向遥测，全在这一层着力，而不是把 `Debug.Log` 散落一地、事后无从拦截。",
                new CodeRef("Assets/Game/Framework/Core/Internal/FrameworkLog.cs", "public static class FrameworkLog", "日志门面"));
            host.AddSubNote("为什么是**静态**门面而非 DI 服务：日志要在**任何地方**可用，包括身处 DI 之下、没有 `Context` 的内核基础设施（`Container` / 构造期）——它们不能反向依赖容器去取 logger。所以门面静态、出厂即用（默认装一个转 `Debug.Log` 的 `UnityDebugLogSink`，Console 观感 / 双击定位 / 堆栈全不变）。");

            // ── 分级门面 ──
            host.AddSectionTitle("分级门面：Info / Warning / Error（看 Unity Console）");
            var levelLabel = host.AddValueDisplay("点下面按钮 → 看 Unity Console：门面把日志转给默认 sink，观感 / 定位与裸 Debug.Log 完全一致（迁移到接缝对 Console 零行为变化）。");
            levelLabel.style.whiteSpace = WhiteSpace.Normal;
            host.AddActionRow("Info", () =>
            {
                FrameworkLog.Info("玩家进入战斗", DemoCategory);
                levelLabel.text = "已发一条 Info（Console 里是普通 Log）。第二参 category 可选——给结构化 sink 分组 / 过滤用，一般 message 前缀 [Xxx] 也够。";
            }, CodeRef.Here("FrameworkLog.Info(\"玩家进入战斗\"", "Info"));
            host.AddActionRow("Warning", () =>
            {
                FrameworkLog.Warning("配置缺省，回退默认值", DemoCategory);
                levelLabel.text = "已发一条 Warning（Console 里是黄色警告）。";
            }, CodeRef.Here("FrameworkLog.Warning(\"配置缺省", "Warning"));
            host.AddActionRow("Error", () =>
            {
                FrameworkLog.Error("存档写入失败", DemoCategory);
                levelLabel.text = "已发一条 Error（Console 里是红色报错）。";
            }, CodeRef.Here("FrameworkLog.Error(\"存档写入失败\"", "Error"));
            host.AddActionRow("Error + 异常", () =>
            {
                FrameworkLog.Error("存档反序列化失败", new InvalidOperationException("bad json"), DemoCategory);
                levelLabel.text = "已发 Error + 异常：默认 sink 额外走一次 Debug.LogException 保留堆栈定位（Console 会多一条带调用栈的异常）。";
            }, CodeRef.Here("new InvalidOperationException(\"bad json\")", "Error + 异常"));

            // ── Trace 门控 ──
            host.AddSectionTitle("Trace + Verbose：诊断噪音的双重门控");
            var traceLabel = host.AddValueDisplay();
            void RefreshTrace() => traceLabel.text =
                $"FrameworkLog.Verbose = {FrameworkLog.Verbose}　｜　Trace 只在 Verbose 开 + 仅 Editor/Development 构建时才输出（发布版编译期短路、零成本）。";
            RefreshTrace();
            host.AddActionRow("切换 Verbose", () =>
            {
                FrameworkLog.Verbose = !FrameworkLog.Verbose;
                RefreshTrace();
            }, new CodeRef("Assets/Game/Framework/Core/Internal/FrameworkLog.cs", "public static bool Verbose", "Verbose 开关"));
            host.AddActionRow("发一条 Trace", () =>
            {
                FrameworkLog.Trace("注册 / 覆盖 / 容器解析等内核噪音", DemoCategory);
                traceLabel.text = FrameworkLog.Verbose
                    ? "Verbose 开 → 这条 Trace 进了 Console（和捕获面板，如果装了 sink）。"
                    : "Verbose 关 → 这条 Trace 被门面短路，哪个 sink 都没收到。先「切换 Verbose」再试。";
            }, CodeRef.Here("FrameworkLog.Trace(", "Trace"));
            host.AddNote("`Trace` 是框架内核诊断噪音（注册 / 覆盖 / 容器解析 / 重试…）的级别，等价旧 `FrameworkLog.LogVerbose`。它受 **`Verbose` 开关 + 仅 `UNITY_EDITOR || DEVELOPMENT_BUILD`** 双重门控：发布版里根本不构造、不到 sink，零成本。`Info` 及以上则**始终广播**给 sink、由各 sink 自行决定去向。也可在 Editor 菜单 `SSFramework/诊断/Verbose 日志` 勾选（本会话有效）。");

            // ── 可插拔 sink（核心） ──
            host.AddSectionTitle("接缝（核心）：装一个捕获 sink，看多播 + 每 sink 自带 MinLevel");
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
                string ex = e.Exception != null ? $" ⟨{e.Exception.GetType().Name}⟩" : "";
                string fields = "";
                if (e.Fields != null && e.Fields.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in e.Fields) parts.Add($"{kv.Key}={kv.Value}");
                    fields = " {" + string.Join(", ", parts) + "}";
                }
                captured.Add($"{e.TimestampUtc.ToLocalTime():HH:mm:ss} [{e.Level}] {cat}{e.Message}{ex}{fields}");
                if (captured.Count > 8) captured.RemoveAt(0);
                panel.text = "捕获面板（最近 8 条）：\n" + string.Join("\n", captured);
            }
            RefreshSink();

            host.AddActionRow("装捕获 sink（AddSink，MinLevel=Trace 全收）", () =>
            {
                if (capturing != null) return;
                capturing = new CapturingSink(LogLevel.Trace, AppendCaptured);
                FrameworkLog.AddSink(capturing);
                RefreshSink();
            }, CodeRef.Here("FrameworkLog.AddSink(capturing)", "AddSink 装 sink"));
            host.AddActionRow("把它 MinLevel 提到 Warning（过滤掉 Info/Trace）", () =>
            {
                if (capturing == null) return;
                capturing.MinLevel = LogLevel.Warning;
                RefreshSink();
            }, CodeRef.Here("capturing.MinLevel = LogLevel.Warning", "调 sink MinLevel"));
            host.AddActionRow("拆掉捕获 sink（RemoveSink）", () =>
            {
                if (capturing == null) return;
                FrameworkLog.RemoveSink(capturing);
                capturing = null;
                RefreshSink();
            }, CodeRef.Here("FrameworkLog.RemoveSink(capturing)", "RemoveSink"));
            host.AddNote("`ILogSink` 就一个 `Log(in LogEntry)` + 一个 `MinLevel`。`AddSink` 后同一条日志广播到每个 sink（Console + 捕获面板 + 未来的文件 / 遥测）；每个 sink 按自己的 `MinLevel` 独立过滤——可让 Console 只留 `Warning+`、细粒度进文件。测试静音 / 捕获断言就靠 `ClearSinks()` + 自装一个收集 sink（见 `LoggingTests`）。",
                new CodeRef("Assets/Game/Framework/Core/Logging/ILogSink.cs", "public interface ILogSink", "sink 接缝契约"));
            host.AddSubNote("⚠ `ILogSink.Log` 可能被**后台线程**调用（如网络接收循环记日志）：持可变状态（文件句柄 / 缓冲）的 sink 要自行加锁（见 `FileLogSink`）。门面对 sink 列表用 copy-on-write，广播本身无锁。本 demo 捕获 sink 只在主线程点按钮触发，故直接更新 UI 安全。",
                CodeRef.Here("private sealed class CapturingSink", "demo 捕获 sink 实现"));

            // ── 落文件 ──
            host.AddSectionTitle("落文件：FileLogSink（零依赖、超阈值自动滚动）");
            string logDir = Path.Combine(Application.persistentDataPath, "framework-logs");
            string logPath = Path.Combine(logDir, "demo.log");
            var fileLabel = host.AddValueDisplay();
            fileLabel.style.whiteSpace = WhiteSpace.Normal;
            void RefreshFile() => fileLabel.text = fileSink == null
                ? "没装文件 sink。"
                : $"已装文件 sink → {logPath}（Info 及以上落盘，超阈值自动按大小滚动、保留最近几份）。发几条日志再「打开目录」看文件。";
            RefreshFile();
            host.AddActionRow("装文件 sink（AddSink FileLogSink）", () =>
            {
                if (fileSink != null) return;
                fileSink = new FileLogSink(logPath, LogLevel.Info);
                FrameworkLog.AddSink(fileSink);
                FrameworkLog.Info("文件 sink 已装上，这条会落盘", DemoCategory);
                RefreshFile();
            }, CodeRef.Here("new FileLogSink(logPath", "装文件 sink"));
            host.AddActionRow("拆掉文件 sink（RemoveSink + Dispose 关句柄）", () =>
            {
                if (fileSink == null) return;
                FrameworkLog.RemoveSink(fileSink);
                fileSink.Dispose();
                fileSink = null;
                RefreshFile();
            }, CodeRef.Here("fileSink.Dispose()", "拆文件 sink"));
#if UNITY_EDITOR
            host.AddActionRow("打开日志目录（看 demo.log，纯文本一行一条）", () =>
            {
                Directory.CreateDirectory(logDir);
                UnityEditor.EditorUtility.RevealInFinder(logDir);
            }, new CodeRef("Assets/Game/Framework/Core/Logging/FileLogSink.cs", "public sealed class FileLogSink", "文件 sink 实现"));
#endif
            host.AddNote("落文件是客户端最常用的未来需求（玩家包 / QA 捞日志 / 用户反馈）：`FileLogSink` 纯 C# `StreamWriter` 追加 + 按大小滚动，**零依赖**——不必为了「写个日志文件」就吞下一串 DLL。`AutoFlush` 开、崩溃前的日志不滞留缓冲；写 / 滚动异常被吞掉只告警一次，绝不打断业务。启动时 `AddSink` 配一次即可。");

            // ── 结构化字段 ──
            host.AddSectionTitle("结构化字段：Log(level, msg, fields)");
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
                FrameworkLog.Log(LogLevel.Info, "purchase", fields, DemoCategory);
                structLabel.text = capturing != null && capturing.MinLevel <= LogLevel.Info
                    ? "已发送：捕获面板里能看到 {userId=42, action=purchase, amount=9.99}；Console（文本 sink）只显示消息、忽略字段。"
                    : "已发送，但当前没有会收 Info 的结构化 sink——先「装捕获 sink」（MinLevel 别高于 Info）再点，就能在面板看到字段。";
            }, CodeRef.Here("FrameworkLog.Log(LogLevel.Info, \"purchase\"", "结构化字段"));
            host.AddNote("`FrameworkLog.Log(level, msg, fields, ...)` 可带一组结构化键值——绝大多数日志不带（此时热路径零额外分配）；带字段时才有一次列表分配（值 `object` 会装箱，属预期成本）。字段只有结构化 sink 会消费，文本 sink 忽略。这也是「接缝为将来留位」的一处：`Info/Warning/Error` 便利方法覆盖 99% 场景，要结构化时走 `Log` 通用入口，不必换 API。");

            // ── 扩展点 / 刻意不做 ──
            host.AddSectionTitle("扩展点与刻意不做");
            host.AddConcept("自定义去向 = ILogSink", "实现 `Log(in LogEntry)` + `MinLevel` 即可把日志导向任何后端（内存缓冲 / HTTP 遥测 / 平台原生日志）。⚠ 可能被后台线程调用，持可变状态自行加锁。");
            host.AddConcept("ZLogger 客户端不引", "零分配 / 结构化 JSON / HTTP 遥测评估过 Cysharp ZLogger，实测装它拖进 `System.Text.Json` 全家桶 ≈1.4MB、最大开销纯为客户端几乎不产的 JSON 日志——性价比不划算，客户端不引（ADR-0034 实测复盘）。");
            host.AddConcept("服务端才是落点", "结构化 / 遥测的价值在服务端（Outpost `Server~/` 本就是 .NET，直接用 ZLogger、无包体顾虑）；客户端将来真有「结构化上报后台」刚需，实现一个 `ZLoggerLogSink : ILogSink` 接进来即可——接缝已留位、业务零改动。");

            host.AddTip("速记：新代码日志走 FrameworkLog、别裸 Debug.Log（裸的拦不住、进不了文件 / 遥测 / 测试）；错误 / 警告尤其应走门面。Trace 是诊断噪音（Verbose + Editor/Dev 门控）；落文件 AddSink(new FileLogSink(...)) 启动配一次；每个 sink 自带 MinLevel。深度见 framework-guide 日志章 / ADR-0034。");

            // 切走本章：拆掉 demo 装的 sink、恢复 Verbose，不给全局静态门面留脏状态。
            Bag.Add(Disposable.Create(() =>
            {
                if (capturing != null) FrameworkLog.RemoveSink(capturing);
                if (fileSink != null) { FrameworkLog.RemoveSink(fileSink); fileSink.Dispose(); }
                FrameworkLog.Verbose = prevVerbose;
            }));
        }
    }
}
