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

    // ───────────── 入门 ─────────────

    public sealed class OverviewModule : ComingSoonModuleBase
    {
        public override string Id => "overview";
        public override string Title => "框架总览";
        public override string Category => "入门";
        public override int Order => 0;
        public override string Summary =>
            "先建立整体认知：MVCS 五层（View / Command / System / Model+Event / Utility）、单向数据流、" +
            "编译期权限接口、自研 DI 容器、生命周期统一为 IDisposable。看完再进具体功能就不懵了。";
    }

    // ───────────── 核心 ─────────────

    public sealed class ModelReactiveModule : ComingSoonModuleBase
    {
        public override string Id => "model-reactive";
        public override string Title => "Model · 响应式状态";
        public override string Category => "核心";
        public override int Order => 10;
        public override string Summary =>
            "Model 怎么存状态、为什么用 RP<T>（响应式属性），View 如何只读订阅 ReadOnlyReactiveProperty<T> 自动刷新。";
    }

    public sealed class CommandKindsModule : ComingSoonModuleBase
    {
        public override string Id => "command-kinds";
        public override string Title => "Command · 同步 / 异步 / 查询";
        public override string Category => "核心";
        public override int Order => 20;
        public override string Summary =>
            "struct vs class Command、同步 ExecuteCommand、异步 ExecuteCommandAsync（带取消令牌）、只读查询 Command 返回订阅源。";
    }

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

    public sealed class LifetimeModule : ComingSoonModuleBase
    {
        public override string Id => "lifetime";
        public override string Title => "生命周期 · DisposableBag";
        public override string Category => "能力";
        public override int Order => 30;
        public override string Summary =>
            "订阅 / 资源句柄 / 池租借 / 子作用域统一进 Bag，宿主销毁批量释放；CreateChild 做更短作用域。";
    }

    // ───────────── 视图 ─────────────

    public sealed class UGuiViewModule : ComingSoonModuleBase
    {
        public override string Id => "ugui-view";
        public override string Title => "UGUI · MonoViewBase";
        public override string Category => "视图";
        public override int Order => 10;
        public override string Summary =>
            "框架当前 Phase 1 的真实 View 层：MonoViewBase 自动注入 + Bag + ExecuteCommand，用一个 UGUI 小例子覆盖 View 手感。";
    }

    // ───────────── 进阶 ─────────────

    public sealed class HotUpdateModule : ComingSoonModuleBase
    {
        public override string Id => "hotupdate";
        public override string Title => "热更 · HybridCLR";
        public override string Category => "进阶";
        public override int Order => 10;
        public override string Summary =>
            "AOT / 热更程序集分界、经资源系统拉取热更 DLL 的引导流程（设计阶段，见 ADR-0008）。";
    }

    public sealed class ConfigTableModule : ComingSoonModuleBase
    {
        public override string Id => "config-table";
        public override string Title => "配置表 · Luban";
        public override string Category => "进阶";
        public override int Order => 20;
        public override string Summary =>
            "构建期 CLI 生成配置代码 + 数据，运行期经资源系统加载，镜像资源系统三段式（设计阶段，见 ADR-0009）。";
    }
}
