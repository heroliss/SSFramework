using Game.Framework.Context;
using Game.Framework.Internal;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// 一个演示模块 = 一个框架功能的演示页。外壳（<see cref="DemoShellController"/>）反射收集所有实现，
    /// 按 <see cref="Category"/> + <see cref="Order"/> 排进左侧导航——新增一个模块即自动出现，无需改外壳。
    /// </summary>
    /// <remarks>
    /// <b>生命周期：</b><see cref="InstallBindings"/>（建 Context 前，贡献本模块要用的框架层）→
    /// <see cref="Initialize"/>（注入建好的 Context）→ 被选中时 <see cref="Build"/>（构建 UI）→
    /// 切走时 <see cref="Teardown"/>（释放订阅/资源）。可反复 Build/Teardown。<br/>
    /// 业务模块继承 <see cref="DemoModuleBase"/> 即可，无需直接实现本接口。
    /// </remarks>
    public interface IDemoModule
    {
        /// <summary>稳定唯一 id（用于选中状态、定位）。</summary>
        string Id { get; }

        /// <summary>导航与标题显示名。</summary>
        string Title { get; }

        /// <summary>分组名（左侧导航按此分组，如 "入门" / "核心" / "进阶"）。</summary>
        string Category { get; }

        /// <summary>组内排序，小的在前——用于"由简入深"。</summary>
        int Order { get; }

        /// <summary>模块顶部讲解文本（这个功能是什么、为什么这样设计）。</summary>
        string Summary { get; }

        /// <summary>是否为"规划中"占位章节（导航里弱化显示，点开只是预告）。已实现的模块为 false。</summary>
        bool IsComingSoon { get; }

        /// <summary>在 Context 构建前贡献本模块需要的框架层绑定（Model/System/Utility）。默认无。</summary>
        void InstallBindings(ContainerBuilder builder);

        /// <summary>外壳在 Context 建好后注入它。此后才能 ExecuteCommand / 订阅。</summary>
        void Initialize(IGameContext context);

        /// <summary>被选中时构建模块 UI 到 <paramref name="host"/>.Content。</summary>
        void Build(DemoModuleHost host);

        /// <summary>切走模块时释放本次 Build 期间的订阅与资源。</summary>
        void Teardown();
    }
}
