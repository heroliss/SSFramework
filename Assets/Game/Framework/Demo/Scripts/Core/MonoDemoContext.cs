using System;
using Game.Framework.Context;
using Game.Framework.Pool;
using Game.Framework.Systems;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 的根场景上下文。挂在场景的 Main Context 节点上，承载所有章节共享的 <c>GameContext</c>。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="MonoGameContextBase"/> 而非 MonoGlobalContext：demo 只是别人项目里的一个场景，不该把
    /// <c>GameContext.Main</c> 设成自己、污染业务的全局上下文。<br/>
    /// <see cref="InstallBindings"/> 注册公共服务（命令分发器 / PoolUtility）+ 各 <see cref="IDemoModule"/>
    /// 贡献的绑定（这是<b>纯 C# 路径</b>）；而场景里挂在本节点下的 <c>MonoModelBase</c> 等层会按 Hierarchy 父子关系
    /// <b>自动注册</b>进同一个容器（这是 <b>Mono 路径</b>）。两条路径注册进同一容器，正好支撑"纯 C# vs Mono"对比演示。<br/>
    /// 执行顺序：<c>MonoGameContextBase</c> 的 <c>[DefaultExecutionOrder]</c> 比外壳与各 Mono 层都早，先把 Context 建好。
    /// </remarks>
    public sealed class MonoDemoContext : MonoGameContextBase
    {
        private DemoModuleCatalog _moduleCatalog;

        /// <summary>由根 Context 持有的唯一章节目录。只有 Context 完成初始化后，外壳才能取得。</summary>
        internal DemoModuleCatalog ModuleCatalog => _moduleCatalog ?? throw new InvalidOperationException(
            "Demo module catalog is unavailable because the root Context has not completed InstallBindings.");

        protected override void InstallBindings(ContainerBuilder builder)
        {
            // 目录先一次性发现并固定 Adapter 身份；后续 Install、Initialize、Build、Teardown 都复用同一批实例。
            _moduleCatalog = DemoModuleCatalog.Discover();

            // 命令分发用 LoggingCommandSystem 装饰默认 CommandSystem（可插拔的活样板）：demo 里点任何按钮，
            // 「SSFramework/诊断与分析/运行时诊断」的 Command 流水即实时可见；不需要流水时注册 CommandSystem 即可。
            builder.RegisterValue(new LoggingCommandSystem(), typeof(ICommandSystem));
            // 池工具用层感知 owned 入口：自动登记 PoolUtility + IPoolUtility，并随本 Context.Dispose 自动清池。
            builder.RegisterOwnedUtility(new PoolUtility());

            _moduleCatalog.InstallBindings(builder);
        }

        protected override void OnInitialized()
        {
            base.OnInitialized();
            _moduleCatalog.Initialize(this);
        }

        protected override void OnDestroy()
        {
            try
            {
                // Unity 不承诺父子节点 OnDestroy 的精确先后；目录作为最终 owner，在 Context 仍可用时兜底结束活动章节。
                _moduleCatalog?.Dispose();
            }
            finally
            {
                base.OnDestroy();
            }
        }
    }
}
