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
