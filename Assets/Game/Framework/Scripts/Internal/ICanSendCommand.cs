namespace Game.Framework.Internal
{
    /// <summary>
    /// 权限标记：实现者可通过 <c>this.ExecuteCommand(...)</c> / <c>this.ExecuteCommandAsync(...)</c> 发起命令。
    /// </summary>
    /// <remarks>
    /// 实现者：View、Command。<br/>
    /// View 通过 Command 表达"用户意图"，Command 内部可再发起子 Command 组合行为。<br/>
    /// Model/System/Utility 不发 Command——避免出现 System 反过来通过 Command 调自己造成的环。<br/>
    /// 空接口，仅做编译期权限分发，业务代码无须自己实现。
    /// </remarks>
    public interface ICanSendCommand
    {
    }
}