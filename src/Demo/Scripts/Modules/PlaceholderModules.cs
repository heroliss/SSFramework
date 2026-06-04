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
    // 核心“Command·三态”已实现为独立模块 CommandKindsDemoModule（见 Modules/CommandKindsDemoModule.cs）。
    // 核心“System·逻辑归位”已实现为独立模块 SystemDemoModule（见 Modules/SystemDemoModule.cs）。

    // 核心“Event·事件总线”已实现为独立模块 EventDemoModule（见 Modules/EventDemoModule.cs）。

    // 核心“依赖注入·Container”已实现为独立模块 ContainerDemoModule（见 Modules/ContainerDemoModule.cs）。

    // 核心“多 Context·作用域树”已实现为独立模块 MultiContextDemoModule（见 Modules/MultiContextDemoModule.cs）。
    // 核心“生命周期·DisposableBag”已实现为独立模块 LifetimeDemoModule（见 Modules/LifetimeDemoModule.cs）。

    // ───────────── 能力 ─────────────
    // 能力“对象池·C# / GameObject”已实现为独立模块 PoolDemoModule（见 Modules/PoolDemoModule.cs，
    // 配套 DemoPoolAssets（场景持有 prefab + 显示容器）+ PooledChip prefab）。

    // 能力“资源加载·YooAsset”已实现为独立模块 AssetLoadingModule（见 Modules/AssetLoadingModule.cs，
    // 配套 DemoAssetRefs（场景持有 AssetReference）+ DemoCdnBuilder（Editor 一键本地 CDN））。

    // ─────────── View 层（也是 MVCS 一层，归入核心） ───────────
    // 核心“View·MonoViewBase”已实现为独立模块 UGuiViewModule（见 Modules/UGuiViewModule.cs，
    // 配套 UGuiDemoView（真实 MonoViewBase）+ DemoUGuiAssets（场景持有 prefab）+ UGUI View prefab）。

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
