using Game.Framework.Internal;
using UnityEngine;

namespace Game.Framework.Model
{
    /// <summary>
    /// Model 的 Mono 实现基类。挂在 Context 子节点上，Awake 时自动注册到容器、注入 <c>[Inject]</c> 字段，
    /// OnDestroy 时自动释放 <see cref="MonoLayerBase{TLayer}.Bag"/> 并反注册。最大的好处是 Inspector 里能实时看到 Model 字段的当前值。
    /// </summary>
    /// <remarks>
    /// <b>谁该用：</b>所有"需要在 Hierarchy 里可见、Inspector 可配/可观察"的 Model。
    /// 纯配置类数据或非 Mono 上下文里的 Model 用 <c>ctx.RegisterModel(new ConfigModel())</c> 即可，无需此基类。<br/>
    /// <b>执行顺序：</b><c>DefaultExecutionOrder(-300)</c>，晚于 Utility(-400)、早于 System(-200) 与 View(-100)。
    /// 即"先有数据，再有逻辑，最后有视图"。<br/>
    /// <b>响应式字段：</b>对外暴露 <c>RP&lt;T&gt;</c>（<c>using R3;</c>）做可订阅状态——Inspector 直接显示值，无需多套一层；
    /// 只读返回类型用 <c>ReadOnlyReactiveProperty&lt;T&gt;</c>，零分配。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item>子类覆写 <c>Awake</c> / <c>OnDestroy</c> 时必须调 <c>base.Xxx()</c>。</item>
    ///   <item>Model 不写业务逻辑——状态变更应该由 System 调用 Model 暴露的 setter（或直接写 <c>RP.Value</c>）。</item>
    ///   <item>注册/注入/释放/反注册的通用实现见 <see cref="MonoLayerBase{TLayer}"/>。</item>
    /// </list>
    /// </remarks>
    [DefaultExecutionOrder(-300)]
    public abstract class MonoModelBase : MonoLayerBase<IModel>, IModel
    {
    }
}
