using Game.Framework.Internal;

namespace Game.Framework.Model
{
    /// <summary>
    /// Model 层标记接口。游戏状态的载体——把"当前有什么数据"和"如何修改数据"分开。
    /// </summary>
    /// <remarks>
    /// <b>职责：</b>持有可订阅的响应式状态（推荐 <c>RP&lt;T&gt;</c>），不写业务逻辑、不发事件。<br/>
    /// <b>谁该用：</b>所有"需要持续存在、被多处观察"的游戏数据（HP、金币、库存、关卡进度等）。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item>Model 只读业务无关的 Utility 基础设施能力，不能 GetSystem/GetModel——避免 Model 之间相互耦合。</item>
    ///   <item>Model 不能 SendEvent——事件归 System 写入，由 System 在改 Model 后顺手发出。</item>
    ///   <item>Mono 路径用 <see cref="Game.Framework.Model.MonoModelBase"/>，纯 C# 路径直接实现并 <c>ctx.RegisterModel</c>。</item>
    /// </list>
    /// </remarks>
    public interface IModel : ICanGetUtility
    {
    }
}
