using Game.Framework.Context;
using Game.Framework.Pool;
using Game.Framework.System;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 的根场景上下文。挂在 DemoRoot 上，承载所有章节共享的 <c>GameContext</c>。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="MonoGameContextBase"/> 而非 MonoGlobalContext：demo 只是别人项目里的一个场景，不该把
    /// <c>GameContext.Main</c> 设成自己、污染业务的全局上下文。<br/>
    /// <see cref="InstallBindings"/> 注册公共服务（CommandSystem / PoolUtility）+ 各 <see cref="IDemoModule"/>
    /// 贡献的绑定（这是<b>纯 C# 路径</b>）；而场景里挂在本节点下的 <c>MonoModelBase</c> 等层会按 Hierarchy 父子关系
    /// <b>自动注册</b>进同一个容器（这是 <b>Mono 路径</b>）。两条路径注册进同一容器，正好支撑"纯 C# vs Mono"对比演示。<br/>
    /// 执行顺序：<c>MonoGameContextBase</c> 的 <c>[DefaultExecutionOrder]</c> 比外壳与各 Mono 层都早，先把 Context 建好。
    /// </remarks>
    public sealed class MonoDemoContext : MonoGameContextBase
    {
        protected override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            // 池工具用 RegisterOwned：随本 Context.Dispose 自动清池（停放节点 + 空闲实例），不靠 DontDestroyOnLoad 长留。
            builder.RegisterOwned(new PoolUtility(), typeof(IPoolUtility));

            // 各模块的纯 C# 层绑定（这里用的是临时实例，只为收集绑定；真正驱动 UI 的模块由外壳另行实例化并注入同一 Context）。
            foreach (var module in DemoShellController.DiscoverModules())
                module.InstallBindings(builder);
        }
    }
}
