namespace Game.Framework.Internal
{
    /// <summary>
    /// 权限标记：实现者可通过 <c>this.GetUtility&lt;T&gt;()</c> 扩展方法获取 Utility。
    /// </summary>
    /// <remarks>
    /// 实现者：Model / System / View（持有 Context 的层）+ **Utility 自身**（基础设施可互相组合，如配置表服务取资源服务加载数据）。Command 不实现本标记，等价的 GetUtility 能力经 <see cref="Game.Framework.Command.ICommandContext"/> 参数提供。<br/>
    /// Utility 是基础设施服务层，各层取用最频繁（权限最宽）；Utility 取其他 Utility 不反向依赖业务（仍禁读 Model/System），与 <c>ISystem : ICanGetSystem</c> 对称。<br/>
    /// 空接口，仅做编译期权限分发，业务代码无须自己实现。
    /// </remarks>
    public interface ICanGetUtility
    {
    }
}