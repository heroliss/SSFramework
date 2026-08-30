using Game.Framework.Demo.Core;
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
            host.AddStep("①", "**全局根**：主场景根建一个节点（如 `MainContext`），挂你的 `MonoGlobalContext` 子类。它 Awake 自动做三件事：设 `GameContext.Main`、`DontDestroyOnLoad` 跨场景保留、检测重复实例。覆写 `InstallBindings` 注册跨场景的纯 C# 服务（命令分发器 / 音频 / 存储…）。",
                new CodeRef("Assets/Game/Framework/Core/Context/MonoGlobalContext.cs", "class MonoGlobalContext", "MonoGlobalContext · 业务继承点"));
            host.AddStep("②", "**功能层**：两条路进容器——Mono 路径把 `MonoModelBase` / `MonoSystemBase` / `MonoUtilityBase` 子类挂在 Context 子树下（Awake 自动注册 + Inspector 可视）；纯 C# 路径在 `InstallBindings` 里注册（可热更、可单测）。两条路怎么选见「数据模型（Model）· 状态与 Inspector」章。");
            host.AddStep("③", "**View**：`MonoViewBase` 子类挂进（或运行时 Instantiate 进）Context 子树，`Awake` 里订阅查询 Command 刷 UI、按钮点击发 Command——写法就是「最小闭环」那一圈，界面复杂后再引入「UI 框架」章的窗口调度。");
            host.AddNote("五层各一段、完整可抄的最小代码在 `docs/framework-guide.md` §3「快速开始」（Context / Model / System / Command / View 串成一圈）；目录与程序集怎么摆见 §26「推荐项目结构」。");
            host.AddCaution("如果业务代码放在自己的 asmdef 中，要显式 reference `Game.Framework`；代码直接出现 `RP<T>` / `UniTask` 类型时，也分别 reference `R3` / `UniTask`。这一步只是让编译依赖可见，不需要在首次接入时理解 Linker 或构建裁剪。");

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

            // ── 容易混淆 ──
            host.AddSectionTitle("第一次接入最容易混淆的四组关系");
            host.AddTable(
                new[] { "容易混淆", "正确关系", "判断方法" },
                new[] { "Context 与 Container", "Context 拥有容器、父子回退和生命周期；Container 只保存“类型 → 实例/工厂”的注册", "问“依赖从哪来、何时释放”看 Context；问“这个类型映射到谁”看 Container" },
                new[] { "Mono 与纯 C#", "只是两种注册载体，不是两套架构；注册后都从同一 Context 解析", "要 Inspector 配置选 Mono；要单测/热更/无 Unity 依赖选纯 C#" },
                new[] { "View 绑定与层注册", "View 是消费者，只绑定 Context 并接受注入；Model/System/Utility 才作为依赖注册", "是否会被其他对象按类型 Resolve，是两者的分界" },
                new[] { "Global 与场景 Context", "Global 是应用唯一根；场景 Context 是可销毁的局部作用域", "跨场景共享放 Global；客座场景和关卡私有状态放局部" });

            // ── 完成标准与下一步 ──
            host.AddSectionTitle("第一次接入做到什么程度就够了");
            host.AddConcept("能启动", "根 Context 正常进入 Ready，没有重复 Global，也没有未命名或未绑定的 View。");
            host.AddConcept("能走通一圈", "按钮经 Command 改 Model，标签订阅只读状态自动刷新；先证明最小闭环，再扩展功能。");
            host.AddConcept("能正确清理", "销毁 View 或局部 Context 后，其订阅和资源随 Bag 释放，不留下迟到回调或重复监听。");
            host.AddCaution("第一次接入不要同时搭满 Model、System、Event、Utility、多个 Context 和所有可选 Module。先用一个 Model + 两个 Command + 一个 View 跑通闭环；规则出现后再加 System，需要广播事实再加 Event，需要隔离生命周期再加子 Context。");
            host.AddTip("推荐下一步按左侧「核心」顺序阅读：先看 Model 两种注册方式，再看 Command 三态与 System 分界，随后比较 Event、两种 View、Context、Container 和 Bag。发布前才需要的 asmdef、link.xml、热更根与真实构建裁剪，已单独放到「进阶 / 模块化 · 依赖与裁剪」，不必在第一次接入时背下来。");
        }
    }
}
