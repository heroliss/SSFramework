namespace Game.Framework.Event
{
    /// <summary>
    /// Event 标记接口。瞬时可观察数据——发生即扇出，不保留当前值。
    /// </summary>
    /// <remarks>
    /// <b>定位：</b>事件和 Model 同属"数据层"，只是事件没有 current value（参考 framework-guide §1.3）。<br/>
    /// <b>谁该用：</b>所有"某件事发生了"的瞬时通知（PlayerHurt、LevelUp、AchievementUnlocked 等）。<br/>
    /// <b>怎么写：</b>推荐 C# 10 的 <c>record struct</c> 值类型，如 <c>record struct DamageEvent(int Damage) : IEvent;</c>；无参用 <c>SendEvent&lt;MyEvent&gt;()</c>。业务程序集需配置 C# 10；这是瞬时数据，不是 Unity 序列化配置。<br/>
    /// <b>选型提示：</b>问"新订阅者需要立刻知道当前状态吗？"——需要用 <c>RP&lt;T&gt;</c>，不需要用 <see cref="IEvent"/>。<br/>
    /// <b>权限：</b>发送需 <see cref="Game.Framework.Internal.ICanSendEvent"/>（Command/System），注册需 <see cref="Game.Framework.Internal.ICanRegisterEvent"/>（View/Command/System）。
    /// </remarks>
    public interface IEvent
    {
    }
}
