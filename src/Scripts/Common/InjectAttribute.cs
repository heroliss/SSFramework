using System;

namespace Game.Framework.Common
{
    /// <summary>
    /// 标记字段 / 属性 / 方法为依赖注入目标——框架在 Awake / Command 执行前会查容器并自动填充。
    /// </summary>
    /// <remarks>
    /// <b>谁该用：</b>class Command 依赖项；MonoXxxBase 子类引用其他层（如 <c>[Inject] private IInventorySystem _inventory;</c>）。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item><b>不要在 struct Command 上用</b>——反射 SetValue 只修改装箱副本，源 struct 字段不变；struct 走 <c>ctx.GetXxx&lt;T&gt;()</c> 实时解析。</item>
    ///   <item>禁止注入 <c>GameContext</c> / <c>IGameContext</c>——拿到完整 Context 会绕过权限接口；框架在 <see cref="Game.Framework.Internal.InjectionPlan"/> 内强制报错。</item>
    ///   <item><b>注入目标受层权限校验</b>（与 <c>this.GetXxx</c> 同源）：宿主有 <c>ICanGetModel</c> / <c>ICanGetSystem</c> / <c>ICanGetUtility</c> 才能注入对应 Model/System/Utility 层，否则注入期 <c>LogError</c> 拦下——如 View 注 Model、Model 注 System、Utility 注任何层都会被挡。Command 例外（经 ctx 有完整访问权）。</item>
    ///   <item>注入顺序"基类先于派生类"，但<b>同一类内字段/属性/方法相对顺序不可依赖</b>（依赖反射 API 的非保证顺序）。</item>
    /// </list>
    /// 由 <see cref="Game.Framework.Internal.InjectionPlan"/> 在首次解析类型时缓存为委托数组，后续 Inject 无反射开销。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method)]
    public sealed class InjectAttribute : Attribute { }
}
