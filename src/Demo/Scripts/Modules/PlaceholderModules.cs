using Game.Framework.Demo.Core;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// "规划中"章节的基类：导航里弱化显示，点开只给一句预告（标题 + 简介已由外壳显示在顶部）。
    /// 实现某章节时，把对应占位类换成继承 <see cref="DemoModuleBase"/> 的真实模块即可。
    /// </summary>
    /// <remarks>
    /// 目的：先把整体章节规划填进左侧导航，让人一眼看到 demo 的全貌与推进路线，而不是只有一个孤零零的入门页。
    /// </remarks>
    public abstract class ComingSoonModuleBase : DemoModuleBase
    {
        public override bool IsComingSoon => true;

        public override void Build(DemoModuleHost host)
        {
            host.AddSectionTitle("规划中");
            host.AddNote("本节正在建设中，敬请期待 —— 上方简介是它将覆盖的内容。");
        }
    }

    // 入门“框架总览”已实现为独立模块 OverviewModule（见 Modules/OverviewModule.cs）。

    // ───────────── 核心 ─────────────
    // 核心“Model·响应式状态”已实现为独立模块 ModelReactiveModule（见 Modules/ModelReactiveModule.cs）。

    public sealed class CommandKindsModule : ComingSoonModuleBase
    {
        public override string Id => "command-kinds";
        public override string Title => "Command · 同步 / 异步 / 查询";
        public override string Category => "核心";
        public override int Order => 20;
        public override string Summary =>
            "struct vs class Command、同步 ExecuteCommand、异步 ExecuteCommandAsync（带取消令牌）、只读查询 Command 返回订阅源。";
    }

    // 核心“System·逻辑归位”已实现为独立模块 SystemDemoModule（见 Modules/SystemDemoModule.cs）。

    public sealed class EventBusModule : ComingSoonModuleBase
    {
        public override string Id => "event-bus";
        public override string Title => "Event · 事件总线";
        public override string Category => "核心";
        public override int Order => 30;
        public override string Summary =>
            "按类型订阅 / 发送事件，Bag.Subscribe 的几种重载，invokeImmediately 立即触发，谁能发、谁能收（权限分层）。";
    }

    public sealed class ContainerModule : ComingSoonModuleBase
    {
        public override string Id => "container";
        public override string Title => "依赖注入 · Container";
        public override string Category => "核心";
        public override int Order => 40;
        public override string Summary =>
            "RegisterValue / RegisterFactory、解析顺序、父子 Context 回退、[Inject] 字段注入。";
    }

    public sealed class MultiContextModule : ComingSoonModuleBase
    {
        public override string Id => "multi-context";
        public override string Title => "多 Context · 作用域树";
        public override string Category => "核心";
        public override int Order => 45;
        public override string Summary =>
            "GameContext 是一棵作用域树：全局 / 场景 / 局部各成一层，子 Context 解析不到就回退父级。演示不同作用域的注册、覆盖与回退。";
    }

    public sealed class LifetimeModule : ComingSoonModuleBase
    {
        public override string Id => "lifetime";
        public override string Title => "生命周期 · DisposableBag";
        public override string Category => "核心";
        public override int Order => 50;
        public override string Summary =>
            "订阅 / 资源句柄 / 池租借 / 子作用域统一进 Bag，宿主销毁批量释放；CreateChild 做更短作用域。";
    }

    // ───────────── 能力 ─────────────

    public sealed class ObjectPoolModule : ComingSoonModuleBase
    {
        public override string Id => "object-pool";
        public override string Title => "对象池 · C# / GameObject";
        public override string Category => "能力";
        public override int Order => 10;
        public override string Summary =>
            "Bag.Rent（C# 对象）/ Bag.Spawn（GameObject）自动归还、分帧 Prewarm 预热、IPoolable 重置钩子。";
    }

    public sealed class AssetLoadingModule : ComingSoonModuleBase
    {
        public override string Id => "asset-loading";
        public override string Title => "资源加载 · YooAsset";
        public override string Category => "能力";
        public override int Order => 20;
        public override string Summary =>
            "Bag.Load<T> / LoadScene / LoadText、AssetReference 拖拽引用、初始化状态订阅、按 tag 下载进度。";
    }

    // ─────────── View 层（也是 MVCS 一层，归入核心） ───────────

    public sealed class UGuiViewModule : ComingSoonModuleBase
    {
        public override string Id => "ugui-view";
        public override string Title => "View · MonoViewBase";
        public override string Category => "核心";
        public override int Order => 35;
        public override string Summary =>
            "View 也是 MVCS 的一层——UI 接缝，核心层对 UI 技术无关。框架 Phase 1 的真实 View 层 MonoViewBase：自动注入 + Bag + ExecuteCommand，用一个 UGUI 小例子覆盖 View 手感，并对比 demo 自身用的纯 C# view-role。";
    }

    // ───────────── 进阶 ─────────────

    public sealed class R3StreamModule : ComingSoonModuleBase
    {
        public override string Id => "r3-streams";
        public override string Title => "R3 · 一切皆流";
        public override string Category => "进阶";
        public override int Order => 10;
        public override string Summary =>
            "进阶收束：Model 状态 / 框架 Event / UnityEvent / 按钮点击 全都能变成 Observable，再用 Where / Throttle / CombineLatest 等操作符组合。";
    }

    // ───────────── 规划中 ─────────────

    public sealed class HotUpdateModule : ComingSoonModuleBase
    {
        public override string Id => "hotupdate";
        public override string Title => "热更 · HybridCLR";
        public override string Category => "规划中";
        public override int Order => 10;
        public override string Summary =>
            "AOT / 热更程序集分界、经资源系统拉取热更 DLL 的引导流程（设计阶段，见 ADR-0008）。";
    }

    public sealed class ConfigTableModule : ComingSoonModuleBase
    {
        public override string Id => "config-table";
        public override string Title => "配置表 · Luban";
        public override string Category => "规划中";
        public override int Order => 20;
        public override string Summary =>
            "构建期 CLI 生成配置代码 + 数据，运行期经资源系统加载，镜像资源系统三段式（设计阶段，见 ADR-0009）。";
    }
}
