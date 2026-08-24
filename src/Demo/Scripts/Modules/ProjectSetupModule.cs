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
        private const string BuildSizeProbeMenu = "SSFramework/诊断/真实构建体积证据";

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

            // ── 程序集接线与模块选择 ──
            host.AddSectionTitle("程序集接线：显式引用不等于自动瘦身");
            host.AddNote("框架程序集 `Game.Framework` 是 `autoReferenced:false`——业务 asmdef 必须**显式**把它加进 references 才能用；业务代码直接使用 `R3`（如 `RP<T>`）或 `UniTask` 类型时，也显式引用对应程序集。这样能看清依赖方向，但不能单凭 references 判断最终包体。");
            host.AddTable(
                new[] { "状态", "回答什么", "不要误读成" },
                new[] { "源码 / Package 存在", "目录、导入器、asmdef 已安装", "已经被业务使用" },
                new[] { "参与 Player 编译", "当前平台会产出 DLL", "最终 Player 一定保留" },
                new[] { "DLL 真实引用", "Framework / 项目谁直接消费它", "能看见字符串反射或场景根" },
                new[] { "linker / 热更根", "link.xml 是否保留；Profile 是否部署完整 DLL", "已经完成同步和 Generate" },
                new[] { "目标平台 Build", "IL2CPP、引擎模块、压缩后的结果", "能从 Windows 外推到 WebGL" });
            host.AddSubNote("一个关键例外：当前可选 Runtime Module 都引用 Core。若 Core 热更，只要某个 Module 仍参与 Player 编译，它就不能被单独留在 AOT；否则会形成 `AOT → 热更` 引用，校验器会拒绝。",
                new CodeRef("Assets/Game/Framework/Build/Editor/HotUpdateAssemblyGraph.cs", "class HotUpdateAssemblyGraph", "热更传播约束 · AOT 不引用热更"));

            // ── 裁剪工作流 ──
            host.AddSectionTitle("小体积 / Web：先查原因，再做结构裁剪");
            host.AddNote("先用模块裁剪审计分开查看 Player 真实消费者与全 asmdef 删除阻塞者（含 Demo / Editor / Tests），再看热更传播和 `link.xml` 根；它还能把任意 Module 当入口做 what-if。然后用隔离构建探针物理排除未选目录，读取当前目标平台 Player BuildReport 的可比较体积上界。");
            host.AddStep("①", "从 Core-only 起步，只按需加入 UI Core 与一个后端；Bridge、Fonts、Yoo、Proto 等由真实需求驱动，不为‘也许会用’提前接入。");
            host.AddStep("②", "准备移除时先迁移审计列出的直接消费者；若受热更传播约束，把**退出 Player 编译图**与**清理热更 Profile**作为同一次代码变更，不要先单独取消并同步。");
            host.AddStep("③", "让 Module 自有 `link.xml` 随目录消失；若只是改成条件保留，先验证反射入口，再做 IL2CPP 回归。随后同步热更设置、重新 Generate / 构建代码包。");
            host.AddStep("④", "最后跑 Module Audit、Unity 测试和目标平台真实构建。Console 安静只证明没显式报错，不证明 Module 已从发布物消失。");
            host.AddConcept("和 Unity Package Manager 的分工", "asmdef 管编译依赖，UnityLinker 管成员裁剪，HybridCLR Profile 管热更部署，UPM 管 package 安装与版本。当前工具只给证据和移除清单，不自动删目录或改 manifest；稳定的粗粒度边界以后再抽成独立 UPM package。");
            host.AddSubNote("Module 的职责、依赖方向与删除测试集中记录在 `docs/framework-module-map.md`；新增程序集时先证明它有独立变化原因，而不是按文件数量机械拆分。");
#if UNITY_EDITOR
            host.AddActionRow("打开模块裁剪审计（逐 Module 保留原因 / 任意入口）",
                () => RunMenu(ModuleAuditMenu),
                new CodeRef("Assets/Game/Framework/Editor/FrameworkModuleAudit.cs", "internal static class FrameworkModuleAudit", "Framework Module Audit · 真实引用闭包"));
            host.AddActionRow("打开真实构建体积证据（隔离删除 / 任意 Module）",
                () => RunMenu(BuildSizeProbeMenu),
                new CodeRef("Assets/Game/Framework/Editor/FrameworkBuildSizeProbe.cs", "internal static class FrameworkBuildSizeProbe", "Framework Build Size Probe · 隔离删除构建"));
#endif

            host.AddTip("速记：主场景根 = MonoGlobalContext 子类；功能层 = 挂子树或 InstallBindings；View = 挂进子树、只发 Command。模块裁剪要分清五种状态，完整工作流见 guide §26 / ADR-0039。");
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
