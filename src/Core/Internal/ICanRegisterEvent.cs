namespace Game.Framework.Internal
{
    /// <summary>
    /// 权限标记：实现者可通过 <c>this.RegisterEvent&lt;T&gt;(...)</c> / <c>Bag.Subscribe&lt;T&gt;(...)</c> 监听事件。
    /// </summary>
    /// <remarks>
    /// 实现者：View、System。Command <b>不</b>实现本标记——且 <see cref="Game.Framework.Command.ICommandContext"/> 刻意不提供 RegisterEvent：命令是瞬时一次性的，不持有订阅。<br/>
    /// 与 <see cref="ICanSendEvent"/> 分开：监听权限放宽到 View，但发送依然只属于 System（及经 ctx 的 Command）。<br/>
    /// Model/Utility 不实现——Model 是被动数据载体；Utility 必须保持业务无关，订阅业务事件会形成基础设施对上层的反向依赖。<br/>
    /// 空接口，仅做编译期权限分发，业务代码无须自己实现。
    /// </remarks>
    public interface ICanRegisterEvent
    {
    }
}
