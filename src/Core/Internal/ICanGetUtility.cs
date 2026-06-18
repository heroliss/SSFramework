namespace Game.Framework.Internal
{
    /// <summary>
    /// 权限标记：实现者可通过 <c>this.GetUtility&lt;T&gt;()</c> 扩展方法获取 Utility。
    /// </summary>
    /// <remarks>
    /// 实现者：Model / System / View（持有 Context 的层）。Command 不实现本标记，等价的 GetUtility 能力经 <see cref="Game.Framework.Command.ICommandContext"/> 参数提供。<br/>
    /// Utility 是无状态工具层，对各业务层最常用（权限最宽）；但 Utility 自身不实现本标记——无状态工具不反向依赖其他层（含其他 Utility）。<br/>
    /// 空接口，仅做编译期权限分发，业务代码无须自己实现。
    /// </remarks>
    public interface ICanGetUtility
    {
    }
}