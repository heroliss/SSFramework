using Game.Framework.Internal;
using UnityEngine;

namespace Game.Framework.Systems
{
    /// <summary>
    /// System 的 Mono 实现基类。挂在 Context 子节点上，Awake 时自动注册到容器、注入 <c>[Inject]</c> 字段，
    /// OnDestroy 时自动释放 <see cref="MonoLayerBase{TLayer}.Bag"/> 并反注册。业务子类直接继承即可。
    /// </summary>
    /// <remarks>
    /// <b>谁该用：</b>需要 Inspector 配置参数、Unity 生命周期回调、或希望在 Hierarchy 里可见的 System。
    /// 纯 C# System 直接实现 <see cref="ISystem"/> + <see cref="IHasGameContext"/> 再用 <c>ctx.RegisterSystem</c> 即可，无需此基类。<br/>
    /// <b>执行顺序：</b><c>DefaultExecutionOrder(-200)</c>，晚于 Model(-300) 与 Utility(-400)，
    /// 早于 View(-100)。Awake 内可读 Model / Utility，但要访问其他兄弟 System 请在 Start 或第一次调用时懒解析。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item>子类覆写 <c>Awake</c> / <c>OnDestroy</c> 时必须调 <c>base.Xxx()</c>。</item>
    ///   <item>不要在子类的 Awake 里立即 <c>this.GetSystem&lt;T&gt;()</c> 访问兄弟 System——Unity 不保证同序脚本的 Awake 顺序。</item>
    ///   <item>注册/注入/释放/反注册的通用实现见 <see cref="MonoLayerBase{TLayer}"/>。</item>
    /// </list>
    /// </remarks>
    [DefaultExecutionOrder(-200)]
    public abstract class MonoSystemBase : MonoLayerBase<ISystem>, ISystem
    {
    }
}
