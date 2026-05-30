namespace Game.Framework.Internal
{
    /// <summary>
    /// 权限标记：实现者可通过 <c>this.RegisterEvent&lt;T&gt;(...)</c> / <c>Bag.Subscribe&lt;T&gt;(...)</c> 监听事件。
    /// </summary>
    /// <remarks>
    /// 实现者：View、Command、System。<br/>
    /// 与 <see cref="ICanSendEvent"/> 分开：监听权限放宽到 View，但发送依然只属于 Command/System。<br/>
    /// Model/Utility 不实现——Model 是数据载体，Utility 是无状态工具，都不应订阅业务事件。<br/>
    /// 空接口，仅做编译期权限分发，业务代码无须自己实现。
    /// </remarks>
    public interface ICanRegisterEvent
    {
    }
}