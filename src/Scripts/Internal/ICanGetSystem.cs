namespace Game.Framework.Internal
{
    /// <summary>
    /// 权限标记：实现者可通过 <c>this.GetSystem&lt;T&gt;()</c> 扩展方法获取 System。
    /// </summary>
    /// <remarks>
    /// 实现者：System。Command 不实现本标记，等价的 GetSystem 能力经 <see cref="Game.Framework.Command.ICommandContext"/> 参数提供。<br/>
    /// Model/View 不实现——Model 不依赖业务逻辑，View 通过 Command 间接驱动 System。<br/>
    /// 空接口，仅做编译期权限分发，业务代码无须自己实现。
    /// </remarks>
    public interface ICanGetSystem
    {
    }
}