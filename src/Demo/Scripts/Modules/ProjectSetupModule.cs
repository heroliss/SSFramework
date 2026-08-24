using Game.Framework.Demo.Core;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 入门·接入你的项目：看懂「最小闭环」后，回答"回到自己项目第一步做什么"——
    /// 全局根（MonoGlobalContext 子类）→ 功能层（Mono 挂子树 / 纯 C# 注册）→ View 挂进子树。
    /// demo 场景自身就是一份接入样板，顺带讲"客座场景为什么刻意不用 Global"的取舍。
    /// </summary>
    public sealed class ProjectSetupModule : DemoModuleBase
    {
        private const string ModuleAuditMenu = "SSFramework/诊断/模块裁剪审计";

        public override string Id => "project-setup";
        public override string Title => "接入你的项目";
        public override string Category => "入门";
        public override int Order => 20;
        public override DemoTeachingKind TeachingKind => DemoTeachingKind.Workflow;
        public override string Summary =>
            "把骨架搬回家：主场景根挂一个 MonoGlobalContext 子类当全局根（自动设 Main / 跨场景 / 查重），" +
            "功能层 Mono 挂子树或纯 C# 注册，View 挂进子树只发 Command。demo 场景自身就是接入样板。";

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("从「看 demo」到「在自己项目里开工」");
            host.AddNote("上一章亲手跑通了单向数据流，这一章回答「回到自己项目，第一步做什么」。骨架只有三步：**建全局根 → 摆功能层 → 挂 View**——全是场景里摆节点 + 少量注册代码，没有配置文件、没有启动魔法。");

            // ── 三步 ──
            host.AddSectionTitle("三步搭起骨架");
            host.AddStep("①", "**全局根**：主场景根建一个节点（如 `MainContext`），挂你的 `MonoGlobalContext` 子类。它 Awake 自动做三件事：设 `GameContext.Main`、`DontDestroyOnLoad` 跨场景保留、检测重复实例。覆写 `InstallBindings` 注册跨场景的纯 C# 服务（CommandSystem / 音频 / 存储…）。",
                new CodeRef("Assets/Game/Framework/Core/Context/MonoGlobalContext.cs", "class MonoGlobalContext", "MonoGlobalContext · 业务继承点"));
            host.AddStep("②", "**功能层**：两条路进容器——Mono 路径把 `MonoModelBase` / `MonoSystemBase` / `MonoUtilityBase` 子类挂在 Context 子树下（Awake 自动注册 + Inspector 可视）；纯 C# 路径在 `InstallBindings` 里注册（可热更、可单测）。两条路怎么选见「Model · 状态与 Inspector」章。");
            host.AddStep("③", "**View**：`MonoViewBase` 子类挂进（或运行时 Instantiate 进）Context 子树，`Awake` 里订阅查询 Command 刷 UI、按钮点击发 Command——写法就是「最小闭环」那一圈，界面复杂后再引入「UI 框架」章的窗口调度。");
            host.AddNote("五层各一段、完整可抄的最小代码在 `docs/framework-guide.md` §3「快速开始」（Context / Model / System / Command / View 串成一圈）；目录与程序集怎么摆见 §26「推荐项目结构」。");

            // ── demo 即样板 ──
            host.AddSectionTitle("demo 场景自身就是一份接入样板");
            host.AddNote("本 demo 场景就是按这套骨架搭的：根节点挂 `MonoDemoContext` 承载共享 Context（`InstallBindings` 注册公共服务 + 各章的纯 C# 层），各章的 Mono 层挂在它子树下自动注册，外壳与各章模块扮演 View 角色。点下面按钮去 Hierarchy 看真实结构。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "class MonoDemoContext", "MonoDemoContext · demo 的根 Context"));
#if UNITY_EDITOR
            host.AddActionRow("选中 demo 根 Context 节点（Main Context）", () =>
            {
                var ctx = Object.FindFirstObjectByType<MonoDemoContext>();
                if (ctx != null) DemoEditorNav.PingSceneObject(ctx.gameObject);
            });
#endif
            host.AddSubNote("一个取舍细节：demo 的根 Context 用的是场景级 `MonoGameContextBase`、**刻意不用** `MonoGlobalContext`——demo 只是「别人项目里的一个客座场景」，不该把 `GameContext.Main` 设成自己、抢走宿主项目的全局根。你的项目**主场景**才是 Global 的位置；被嵌进别人工程的场景（demo / 插件样例 / 子游戏）用场景级 Context。");

            // ── 程序集接线 ──
            host.AddSectionTitle("程序集接线（asmdef）");
            host.AddNote("框架程序集 `Game.Framework` 是 `autoReferenced:false`（热更边界要求）——业务 asmdef 必须**显式**把它加进 references 才能用；业务代码直接用到 `R3`（如 `RP<T>`）或 `UniTask` 类型时，把它们也加上。要让业务程序集可热更，再把它登记进热更列表即可（部署决策、代码零改动，见「热更 · HybridCLR」章）。");
            host.AddSubNote("小体积 / Web 项目从 Core-only 开始，只按需加 UI Core 与一个后端；`autoReferenced:false` 保住依赖方向，但最终包体仍受 DLL 真实引用、IL2CPP 裁剪和热更列表影响。模块裁剪审计会把这三层证据分开：原始托管闭包只用于找候选，最终以目标平台 Player BuildReport 为准。");
            host.AddSubNote("Module 的职责、依赖方向与删除测试集中记录在 `docs/framework-module-map.md`；新增程序集时先证明它有独立变化原因，而不是按文件数量机械拆分。");
#if UNITY_EDITOR
            host.AddActionRow("打开模块裁剪审计（Core / UGUI / Toolkit / 热更档位）",
                () => RunMenu(ModuleAuditMenu),
                new CodeRef("Assets/Game/Framework/Editor/FrameworkModuleAudit.cs", "internal static class FrameworkModuleAudit", "Framework Module Audit · 真实引用闭包"));
#endif

            host.AddTip("速记：主场景根 = MonoGlobalContext 子类（自动 Main / 跨场景 / 查重）；功能层 = 挂子树或 InstallBindings；View = 挂进子树、只发 Command；客座场景用 MonoGameContextBase。完整代码 guide §3、项目结构 §26。");
        }

#if UNITY_EDITOR
        private static void RunMenu(string path)
        {
            if (!UnityEditor.EditorApplication.ExecuteMenuItem(path))
                Log.Warning($"[Demo] 菜单不存在：{path}");
        }
#endif
    }
}
