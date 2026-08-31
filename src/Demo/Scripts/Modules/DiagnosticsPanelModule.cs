using Game.Framework.Demo.Core;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·框架诊断面板：把「容器里注册了什么、Command 什么时候被谁发过、Context 树长什么样、Bag 存活多少」
    /// 这些不可见的运行时状态聚合成一个调试器风格窗口（菜单 SSFramework/诊断与分析/运行时诊断）。
    /// demo 根 Context 已注册 <c>LoggingCommandSystem</c>——本 demo 每个按钮发的 Command 都在流水里留痕，
    /// 打开面板就能对照前面各章观察。ADR-0026。
    /// </summary>
    public sealed class DiagnosticsPanelModule : DemoModuleBase
    {
        private const string PanelMenu = "SSFramework/诊断与分析/运行时诊断";

        public override string Id => "diagnostics-panel";
        public override string Title => "框架诊断面板";
        public override string Category => "核心";
        public override int Order => 55;
        public override DemoTeachingKind TeachingKind => DemoTeachingKind.Workflow;
        public override string Summary =>
            "前面各章「看不见的运行时」其实都可见：Context 作用域树 / 实际解析回退 / 容器注册表 / Command 流水 / Bag 存活趋势，" +
            "聚合在一个调试器窗口里（左树 · 右明细 · 下流水）。Demo 已接好 Command 流水，打开即看；设计见 ADR-0026。";

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("把前面各章「看不见的运行时」变成一个窗口");
            host.AddNote("「依赖注入」章说纯 C# 注册在 Inspector 看不到、「多上下文（Context）」章的作用域树只能靠想象、Command 执行更是无影无踪——诊断面板把它们全部可视化：**左侧 Context 作用域树 · 右侧选中 Context 的注册明细 · 底部 Command 流水**，顶栏还有 Context / Bag 存活计数（带约 30 秒趋势线）。进 Play 后打开，自动增量刷新，定位是**调试与泄漏排查入口**。");

            // ── 动手试 ──
            host.AddSectionTitle("动手试：打开面板，边玩 demo 边看");
            host.AddActionRow("打开框架诊断面板", () => DemoEditorNav.OpenMenu(PanelMenu),
                new CodeRef("Assets/Game/Framework/Editor/FrameworkDiagnosticsWindow.cs", "class FrameworkDiagnosticsWindow", "面板实现（EditorWindow）"));
            host.AddNote("打开后回 demo 随便点几个按钮再看面板——三块区域都能和前面的章节对上号：");
            host.AddStep("①", "**Context 树（左）**：能找到 demo 根 Context 和「多上下文（Context）」章的 SubContext——作用域树在这里成像，双击 Mono 节点直接定位场景对象。`可→Main` 只表示允许兜底；真正命中过 Main 后才变成警示色 `→Main ×N`。去「游戏流程」章 `GoTo` 几个阶段，还能看到状态子 Context 随进入出现、随切走消失——「整棵撤」的直观证据。");
            host.AddStep("②", "**注册与回退明细（右）**：选中 demo 根 Context——各章 `InstallBindings` 注册的纯 C# 层（`CounterModel`、`IPoolUtility`…）全在注册表里（契约 → 实例，工厂项**不触发构造**、观察不改变系统）；有真实父链 / Main 命中时，“解析回退”会列出契约、最终来源和 Resolve 次数。");
            host.AddStep("③", "**Command 流水（下）**：刚才每个按钮发的 Command 都在——时间 / 帧 / 同步异步 / 耗时 / 状态，新的在上、错误红字、超慢着色；支持搜索、「仅错误」过滤、复制 TSV。异步命令 await 完成后才落账，耗时才有意义。");
            host.AddConcept("策略与证据", "`可→Main` 是 Context 能力，不是已经发生；`→Main ×N` 才表示实际成功解析。失败 TryResolve、HasBinding 和面板只读观察都不计数。");
            host.AddConcept("解析次数", "N 表示 Resolve 次数，不是业务使用次数或静态依赖图；缓存后的服务被反复调用不会反复增长。");
            host.AddNote("流水是 **opt-in** 的：来自 `LoggingCommandSystem`——`ICommandSystem` 的**装饰器**（「命令分发可替换」的现成活样板），根 Context 注册它替换默认 `CommandSystem` 即得；泛型直转发、struct 路径保持零装箱、异常照原样冒出，不改任何执行语义。demo 根 Context 已这样注册：",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "new LoggingCommandSystem()", "demo 的接入（一行替换注册）"));

            // ── 初始化问题 ──
            host.AddSectionTitle("看到多个 Mono 初始化问题，先别按数量猜 bug");
            host.AddNote("子 Context 初始化时会先确保父 Context 已完成。若根 Context 的 `InstallBindings` 抛错，根、子、孙三层都可能失败——窗口会显示 **1 个根因、影响 3 个 Context**，而不是把同一故障冒充三处独立 bug。先定位每组的“最先失败对象”，再展开受影响链确认传播范围。",
                new CodeRef("Assets/Game/Framework/Editor/MonoContextIssueAnalysis.cs", "class MonoContextIssueAnalysis", "父子级联的只读聚合模型"));
            host.AddConcept("当前 Play", "故障正在影响这次运行，需要处理；首要根因卡使用 Error 语义。");
            host.AddConcept("历史证据", "已经退出 Play，只是保留上次失败供定位 / 复制，不表示当前仍在执行坏逻辑；场景重载后会重建。若项目关闭了 Scene Reload，先手动重载场景再复测。");
            host.AddConcept("时序提醒", "激活对象还停在 `Uninitialized/Initializing`，但尚未抛异常，所以不计入根因数。只短暂出现一帧通常无碍；持续存在再从“最上游未就绪”检查激活状态与 `Awake` 时序。");
            host.AddConcept("复制整组诊断", "一次带出最先失败对象、受影响链、父级和完整根因堆栈；贴 issue 或交给 AI 时不要重复复制多份级联异常。");
            host.AddSubNote("Edit Mode 里普通 `Uninitialized` 很正常：MonoBehaviour 还没执行 `Awake`。只有 Play 中激活对象持续没初始化，或状态明确为 `Failed`，面板才把它列为问题。当前 DemoScene 正常运行时三套 Context 都应为 Ready。",
                new CodeRef("Assets/Game/Framework/Editor/FrameworkDiagnosticsWindow.cs", "ShouldReportMonoIssue", "什么状态才需要报告"));

            // ── 泄漏排查 ──
            host.AddSectionTitle("泄漏排查三板斧");
            host.AddConcept("趋势线", "顶栏 Bag / Context 折线只升不降 = 有宿主没释放——先看这里定性。");
            host.AddConcept("订阅计数", "右侧明细里某个事件的订阅数只涨不跌 = 有订阅没进 Bag（或 Bag 没释放）。");
            host.AddConcept("树上残影", "切走的阶段 / 关卡 Context 还挂在树上 = 忘了 Dispose。登记表**刻意持强引用**——没释放的 Context 会一直挂着，这不是面板的 bug，正是它要暴露的泄漏。");

            // ── 边界 ──
            host.AddSectionTitle("边界");
            host.AddNote("采集仅在 Editor（玩家包连 Development Build 都编译消除、零成本）；真机诊断走「日志」章那套（`Log` + `CaptureUnityLogs` + `FileLogSink` 落盘）。纯 C# `new GameContext(...)` 时顺手设 `DebugName`（诊断专用、业务逻辑不得依赖），树上就不会出现匿名节点——场景 Context 与 Flow 状态子 Context 框架已自动命名。");

            host.AddTip("速记：进 Play → 菜单 SSFramework/诊断与分析/运行时诊断；`可→Main` 是策略、`→Main ×N` 才是实际解析；Mono 问题先看“根因”而非“影响数”，再分当前 / 历史；Command 流水 = 根 Context 注册 LoggingCommandSystem（demo 已接）；泄漏看趋势线 + 订阅计数 + 树上残影。深度见 framework-guide §23 / ADR-0026。");
        }

    }
}
