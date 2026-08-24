using Game.Framework.Internal;
using UnityEngine;

namespace Game.Framework.Utility
{
    /// <summary>
    /// Utility 的 Mono 实现基类。挂在 Context 子节点上，Awake 时自动注册到容器、注入 <c>[Inject]</c> 字段，
    /// OnDestroy 时自动释放 <see cref="MonoLayerBase{TLayer}.Bag"/> 并反注册。最大的好处是能在 Inspector 配置工具参数。
    /// </summary>
    /// <remarks>
    /// <b>谁该用：</b>需要 Inspector 配置（如 <c>EncryptUtility._key</c>）、Unity 生命周期或 Hierarchy 可见性的 Utility。
    /// 不需要 MonoBehaviour 的纯 C# Utility 按所有权用 <c>RegisterValue</c> / <c>RegisterOwned</c> 注册，无需此基类。<br/>
    /// <b>执行顺序：</b><c>DefaultExecutionOrder(-400)</c>，最早执行。
    /// Utility 不依赖任何业务层，先注册让后续 Model/System Awake 可以 <c>[Inject]</c> 到。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item>Utility 可以持有连接、缓存、窗口栈等基础设施状态，但不持有 HP、金币、库存等业务状态；后者应归 Model / System。</item>
    ///   <item>子类覆写 <c>Awake</c> / <c>OnDestroy</c> 时必须调 <c>base.Xxx()</c>。</item>
    ///   <item>注册/注入/释放/反注册的通用实现见 <see cref="MonoLayerBase{TLayer}"/>。</item>
    /// </list>
    /// </remarks>
    [DefaultExecutionOrder(-400)]
    public abstract class MonoUtilityBase : MonoLayerBase<IUtility>, IUtility
    {
    }
}
