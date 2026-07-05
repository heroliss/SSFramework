using Game.Framework.Internal;

namespace Game.Framework.Systems
{
    /// <summary>
    /// System 层标记接口。业务逻辑的归宿——封装"怎么做"，是 Model 的唯一合法写入者。
    /// </summary>
    /// <remarks>
    /// <b>职责：</b>调度 Model 状态变更、协调多 Model/多 System 的规则、向外广播事件。<br/>
    /// <b>谁该用：</b>所有"业务规则"代码（伤害计算、库存增减、关卡推进、AI 决策等）。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item>System 可 GetModel/GetSystem/GetUtility、SendEvent/RegisterEvent；但<b>不能 ExecuteCommand</b>——防止 System 自己经 Command 反过来调自己造成的环。</item>
    ///   <item>Command 才是"用户意图"的唯一入口，System 实现"如何完成"；这条接缝由 <see cref="ICanSendCommand"/> 权限约束。</item>
    ///   <item>Mono 路径用 <see cref="Game.Framework.Systems.MonoSystemBase"/>，纯 C# 路径直接实现 + <see cref="Game.Framework.Internal.IHasGameContext"/> + <c>ctx.RegisterSystem</c>。</item>
    /// </list>
    /// </remarks>
    public interface ISystem : ICanGetModel, ICanGetSystem, ICanGetUtility, ICanSendEvent, ICanRegisterEvent
    {
    }
}
