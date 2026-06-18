using Game.Framework.Internal;

namespace Game.Framework.View
{
    /// <summary>
    /// View 层标记接口。用户感知层——只观察数据、只发命令，不直接修改任何状态。
    /// </summary>
    /// <remarks>
    /// <b>职责：</b>把数据流投射为 UI / 视觉效果；把用户输入转成 Command 发出。<br/>
    /// <b>谁该用：</b>UI 控件、HUD、特效触发器、玩家输入处理等所有"用户可见可交互"的代码。<br/>
    /// <b>边界（编译期约束）：</b>
    /// <list type="bullet">
    ///   <item>View 可 SendCommand / RegisterEvent / GetUtility。</item>
    ///   <item>View 不能 GetModel/GetSystem（绕过 Command 直接读业务层会破坏单向数据流）。</item>
    ///   <item>View 不能 SendEvent（发事件是 System 改 Model 后的衍生动作）。</item>
    ///   <item>View 需要持续观察数据时，通过只读查询 Command 拿 <c>Observable&lt;T&gt;</c> / <c>ReadOnlyReactiveProperty&lt;T&gt;</c>，用 <c>Bag.Subscribe</c> 管理生命周期。</item>
    ///   <item>Mono 路径用 <see cref="Game.Framework.View.MonoViewBase"/>，会自动注入 <c>[Inject]</c> 字段并在 OnDestroy 释放 Bag。</item>
    /// </list>
    /// </remarks>
    public interface IView : ICanSendCommand, ICanRegisterEvent, ICanGetUtility
    {
    }
}
