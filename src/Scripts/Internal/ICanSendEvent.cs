namespace Game.Framework.Internal
{
    /// <summary>
    /// 权限标记：实现者可通过 <c>this.SendEvent(...)</c> 发出事件。
    /// </summary>
    /// <remarks>
    /// 实现者：Command、System。<br/>
    /// View 不实现，因为 View 只能"观察"数据流，不应主动发事件（事件本质是数据层的瞬时通知，归 System 写入）。<br/>
    /// Model/Utility 不实现，因为它们不感知业务事件语义。<br/>
    /// 与 <see cref="ICanRegisterEvent"/> 分开：发与收是两套独立权限，View 可注册但不可发。<br/>
    /// 空接口，仅做编译期权限分发，业务代码无须自己实现。
    /// </remarks>
    public interface ICanSendEvent
    {
    }
}