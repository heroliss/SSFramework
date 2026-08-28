using Game.Framework.Demo.Core;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·热更机制：讲 HybridCLR 热更的列表驱动设计——热更范围是部署决策不是代码属性、程序集三层、
    /// 构建工作台步骤与迭代边界、Boot 引导流程。编辑器恒走旁路（程序集本就在 AppDomain），
    /// 真实「下载 → Assembly.Load」只在 IL2CPP 真机发生，所以本章以讲解 + 源码跳转为主，
    /// 唯一可现场执行的是构建期校验（真实调用引用图校验器）。
    /// </summary>
    public sealed class HotUpdateModule : DemoModuleBase
    {
        private const string WorkbenchMenu = "SSFramework/构建与发布/代码热更新";
        private const string ModuleAuditMenu = "SSFramework/诊断与分析/模块与依赖";

        public override string Id => "hotupdate";
        public override string Title => "热更 · HybridCLR";
        public override string Category => "进阶";
        public override int Order => 20;
        public override string Summary =>
            "列表驱动热更范围（框架本体也可热更）+ 薄 Boot 程序集引导。编辑器恒走旁路、真机才走下载加载，" +
            "本章讲原理、可删除构建模块与发布分工，校验按钮真实可跑；深度见 framework-guide §15 / ADR-0008 / ADR-0045。";

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("编辑器看不到热更，因为它被刻意旁路了");
            host.AddNote("编辑器下所有程序集本就在 AppDomain 里，引导器直接反射进入口——没有下载、没有 Assembly.Load，开发期对热更机制零感知（这是设计目标，不是缺演示）。真实链路只在 IL2CPP 真机发生：普通算法改动通常只重打代码包；签名、泛型、布局等结构边界由 stamp 判断，必要时会要求重新 Generate 和构建玩家包。",
                new CodeRef("Assets/Game/Framework/Boot/HotUpdateLauncher.cs", "class HotUpdateLauncher", "Boot 引导器（编辑器旁路 + 真机全流程）"));

            // ── 心智模型 ──
            host.AddSectionTitle("心智模型：热更范围是部署决策，不是代码属性");
            host.AddNote("哪些程序集热更，由 FrameworkHotUpdateProfile 的列表决定，所以目录与程序集按领域命名（Game.Main / Game.X / Game.DLC.Y），不按「HotUpdate」这种部署属性命名。调整 AOT / 热更归属通常不改业务 API，但必须让列表对引用关系闭合，不能任意逐个取消。");
            host.AddTable(
                new[] { "层", "程序集", "热更？" },
                new[] { "引导", "`Game.Framework.Boot`（薄壳：下载/补元数据/加载/反射入口）", "永不（鸡生蛋）" },
                new[] { "框架", "`Game.Framework`（内核）、`Game.Framework.Asset.Yoo`（YooAsset 适配）", "默认热更，可退 AOT" },
                new[] { "业务", "`Game.Main` 及未来模块 / DLC", "热更（主战场）" });
            host.AddSubNote("热更程序集一律 `autoReferenced:false`（防止散落脚本隐式引用构成「AOT→热更」违规）；这只关闭隐式引用，不会让 asmdef 停止编译。若 Core 热更，仍参与 Player 编译且引用 Core 的可选 Module 也必须热更，除非同时删除 / 卸载它；铁律由构建期校验器机器执行。");

            // ── 引导流程 ──
            host.AddSectionTitle("真机引导流程（Boot 场景是唯一随包场景）");
            host.AddNote("初始化代码包（专用 RawFile 包，与资源包彻底分家）→ 检查/下载代码更新（按 hash 增量，改哪个程序集下哪个）→ 读热更清单 → 逐个补 AOT 泛型元数据 → 按引用图拓扑序 Assembly.Load → 反射调入口 GameEntry.Enter()。入口之后就是热更世界：创建全局 Context、初始化资源系统、加载真实首场景都从这往下走。",
                new CodeRef("Assets/Game/Main/GameEntry.cs", "class GameEntry", "入口约定（当前为 IL2CPP 真机框架自检）"));

            // ── 构建分工 ──
            host.AddSectionTitle("构建工作台分工与迭代边界");
            host.AddTable(
                new[] { "代码热更新工作台步骤", "何时执行", "耗时" },
                new[] { "1. 同步热更设置", "改了热更列表后", "秒" },
                new[] { "2. 生成桥接与裁剪文件（Generate All）", "首次接入 / 升级环境 / 改 AOT、签名、泛型、布局或原生调用边界（stamp 会拦截）", "分钟" },
                new[] { "3. 构建代码包", "日常每次热更迭代", "几十秒" },
                new[] { "4. 部署代码包", "跟在 3 后面（平铺到 Deploy，与资源包同套 CDN 结构）", "秒" });
            host.AddSubNote("普通 YooAsset 资源构建与 HybridCLR 热更新构建是单向两个 Editor Module：资源侧不引用 Boot、HybridCLR 或 dnlib；热更新侧复用它的版本、部署与安全路径。小项目不用代码热更新时可以删除 `Build/HybridCLR` 与 Boot，资源构建仍成立。CodePackage 要在资源 Profile 明确关闭“参与构建”，误启用 RawFile 会在写产物前失败，而不是靠隐藏包名静默跳过。",
                new CodeRef("Assets/Game/Framework/Build/Editor/FrameworkAssetBuilder.cs", "public static class FrameworkAssetBuilder", "独立的普通资源构建 Module"));
            host.AddSubNote("迭代边界：只改普通算术、分支、常量等业务算法，且不改变元数据依赖拓扑时，只需 3+4。新增方法/签名/泛型实例、值类型字段布局、P/Invoke / calli 或相关 Attribute 可能改变 Link、AOT 或 MethodBridge，构建器会比较 HybridCLR 目标 DLL 元数据拓扑与 AOT / linker 输入指纹并拒绝沿用旧 Generate；此时执行 2，并按平台重新构建安装包。不要凭『代码在热更程序集里』就断言永远不用 Generate。",
                new CodeRef("Assets/Game/Framework/Build/HybridCLR/Editor/FrameworkHotUpdateBuilder.cs", "class FrameworkHotUpdateBuilder", "构建实现（CompileDll → 清单 → RawFile 包）"));
            host.AddSubNote("不确定自己漏了哪一步时，模块裁剪审计的“热更产物链”会只读比较唯一 Profile → HybridCLRSettings → Generate stamp → 当前热更拓扑 / AOT 补元数据清单 → DLL 中转，并明确建议执行 1 / 2 / 3。绿色只证明结构与所列文件一致，不证明 DLL 已包含最新源码，也不代表步骤 4 的 Deploy / CDN 已完成；空 Profile 不要求 Generate，但若启用场景仍挂着 HotUpdateLauncher，Player 分支仍需要步骤 3 生成空清单代码包。只有改成直接 AOT 启动后 CodePackage 才可省略。");

            // ── 可现场执行的部分:构建期校验 ──
            host.AddSectionTitle("现场可跑：构建期校验（真实调用，与工作台同源）");
            host.AddNote("校验「AOT 不引用热更」（违规逐条指出元凶与修法）并展示自动拓扑排序的加载顺序——这是构建管线在编辑器侧真实存在的部分，与真机无关，可以现场跑。");
            host.AddActionRow("打开代码热更新工作台（校验 · 配置 · 构建）", () => RunMenu(WorkbenchMenu),
                new CodeRef("Assets/Game/Framework/Build/HybridCLR/Editor/HotUpdateAssemblyGraph.cs", "class HotUpdateAssemblyGraph", "引用图校验 + 拓扑排序"));
            host.AddActionRow("查看热更产物链（只读，不自动修改）", () => RunMenu(ModuleAuditMenu),
                new CodeRef("Assets/Game/Framework/Build/HybridCLR/Editor/FrameworkHotUpdateBuilder.cs", "InspectEvidence(FrameworkHotUpdateProfile profile)", "Profile → Settings → Generate → DLL 中转证据"));

            host.AddTip("深度阅读：docs/framework-guide.md §15（用法手册）、docs/adr/0008（热更机制）、docs/adr/0045（构建 Module 拆分与删除测试）。");
        }

        private static void RunMenu(string path)
        {
            if (!UnityEditor.EditorApplication.ExecuteMenuItem(path))
                Debug.LogWarning($"[HotUpdateModule] 菜单执行失败：{path}（菜单路径变更？）");
        }
    }
}
