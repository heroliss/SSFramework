namespace Game.Framework.Internal
{
    /// <summary>
    /// 权限标记：实现者可通过 <c>this.GetModel&lt;T&gt;()</c> 扩展方法读取 Model。
    /// </summary>
    /// <remarks>
    /// 实现者：System、Model。Command 不实现本标记，等价的 GetModel 能力经 <see cref="Game.Framework.Command.ICommandContext"/> 参数提供。<br/>
    /// View 不实现，因为 View 应通过只读查询 Command 拿状态而不是直接读 Model（参见框架理念）。<br/>
    /// 空接口，仅做编译期权限分发——扩展方法用 <c>this ICanGetModel</c> 约束调用者，业务代码无须自己实现。
    /// </remarks>
    public interface ICanGetModel
    {
    }
}