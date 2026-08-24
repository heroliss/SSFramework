using Game.Framework.Demo.Core;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·框架诊断面板：把「容器里注册了什么、Command 什么时候被谁发过、Context 树长什么样、Bag 存活多少」
    /// 这些不可见的运行时状态聚合成一个调试器风格窗口（菜单 SSFramework/诊断/框架诊断面板）。
    /// demo 根 Context 已注册 <c>LoggingCommandSystem</c>——本 demo 每个按钮发的 Command 都在流水里留痕，
    /// 打开面板就能对照前面各章观察。ADR-0026。
    /// </summary>
    public sealed class DiagnosticsPanelModule : DemoModuleBase
    {
        private const string PanelMenu = "SSFramework/诊断/框架诊断面板";

        public override string Id => "diagnostics-panel";
        public override string Title => "框架诊断面板";
        public override string Category => "核心";
        public override int Order => 55;
        public override string Summary =>
            "前面各章「看不见的运行时」其实都可见：Context 作用域树 / 容器注册表 / Command 流水 / Bag 存活趋势，" +
            "聚合在一个调试器窗口里（左树 · 右明细 · 下流水）。Demo 已接好 Command 流水，打开即看；设计见 ADR-0026。";

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddSectionTitle("定位：把前面各章「看不见的运行时」变成一个窗口");
            host.AddNote("「依赖注入」章说纯 C# 注册在 Inspector 看不到、「多 Context」章的作用域树只能靠想象、Command 执行更是无影无踪——诊断面板把它们全部可视化：**左侧 Context 作用域树 · 右侧选中 Context 的注册明细 · 底部 Command 流水**，顶栏还有 Context / Bag 存活计数（带约 30 秒趋势线）。进 Play 后打开，自动增量刷新，定位是**调试与泄漏排查入口**。");

            // ── 动手试 ──
            host.AddSectionTitle("动手试：打开面板，边玩 demo 边看");
            host.AddActionRow("打开框架诊断面板", () => RunMenu(PanelMenu),
                new CodeRef("Assets/Game/Framework/Editor/FrameworkDiagnosticsWindow.cs", "class FrameworkDiagnosticsWindow", "面板实现（EditorWindow）"));
            host.AddNote("打开后回 demo 随便点几个按钮再看面板——三块区域都能和前面的章节对上号：");
            host.AddStep("①", "**Context 树（左）**：能找到 demo 根 Context 和「多 Context」章的 SubContext——作用域树在这里成像，双击 Mono 节点直接定位场景对象。去「游戏流程」章 `GoTo` 几个阶段，还能看到状态子 Context 随进入出现、随切走消失——「整棵撤」的直观证据。");
            host.AddStep("②", "**注册明细（右）**：选中 demo 根 Context——各章 `InstallBindings` 注册的纯 C# 层（`CounterModel`、`IPoolUtility`…）全在注册表里（契约 → 实例，工厂项**不触发构造**、观察不改变系统）；「依赖注入」章说 Inspector 看不到的，这里看得到。");
            host.AddStep("③", "**Command 流水（下）**：刚才每个按钮发的 Command 都在——时间 / 帧 / 同步异步 / 耗时 / 状态，新的在上、错误红字、超慢着色；支持搜索、「仅错误」过滤、复制 TSV。异步命令 await 完成后才落账，耗时才有意义。");
            host.AddNote("流水是 **opt-in** 的：来自 `LoggingCommandSystem`——`ICommandSystem` 的**装饰器**（「命令分发可替换」的现成活样板），根 Context 注册它替换默认 `CommandSystem` 即得；泛型直转发、struct 路径保持零装箱、异常照原样冒出，不改任何执行语义。demo 根 Context 已这样注册：",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "new LoggingCommandSystem()", "demo 的接入（一行替换注册）"));

            // ── 泄漏排查 ──
            host.AddSectionTitle("泄漏排查三板斧");
            host.AddConcept("趋势线", "顶栏 Bag / Context 折线只升不降 = 有宿主没释放——先看这里定性。");
            host.AddConcept("订阅计数", "右侧明细里某个事件的订阅数只涨不跌 = 有订阅没进 Bag（或 Bag 没释放）。");
            host.AddConcept("树上残影", "切走的阶段 / 关卡 Context 还挂在树上 = 忘了 Dispose。登记表**刻意持强引用**——没释放的 Context 会一直挂着，这不是面板的 bug，正是它要暴露的泄漏。");

            // ── 边界 ──
            host.AddSectionTitle("边界");
            host.AddNote("采集仅在 Editor（玩家包连 Development Build 都编译消除、零成本）；真机诊断走「日志」章那套（`Log` + `CaptureUnityLogs` + `FileLogSink` 落盘）。纯 C# `new GameContext(...)` 时顺手设 `DebugName`（诊断专用、业务逻辑不得依赖），树上就不会出现匿名节点——场景 Context 与 Flow 状态子 Context 框架已自动命名。");

            host.AddTip("速记：进 Play → 菜单 SSFramework/诊断/框架诊断面板；Command 流水 = 根 Context 注册 LoggingCommandSystem（demo 已接）；泄漏看趋势线 + 订阅计数 + 树上残影。深度见 framework-guide §23 / ADR-0026。");
        }

        private static void RunMenu(string path)
        {
            if (!UnityEditor.EditorApplication.ExecuteMenuItem(path))
                Debug.LogWarning($"[DiagnosticsPanelModule] 菜单执行失败：{path}（菜单路径变更？）");
        }
    }
}
