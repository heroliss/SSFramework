using Game.Framework.Internal;

namespace Game.Framework.Utility
{
    /// <summary>
    /// Utility 层标记接口。**基础设施服务层**——把"各层共用的能力 / 服务"与"业务逻辑"分开，所有层都可直接取用。
    /// </summary>
    /// <remarks>
    /// <b>职责：</b>提供各层共用的能力——既有纯函数工具（加密、格式化、序列化、坐标变换…），也有
    /// **持有服务级状态**的基础设施（资源系统 <c>IAssetUtility</c> 的包状态、UI <c>IUIUtility</c> 的窗口栈、
    /// 对象池、配置表数据等）。各层经 <c>GetUtility</c> / <c>[Inject]</c> 取用（含 View）。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item>Utility <b>不读 Model/System</b>——不反向依赖业务状态，保持"基础设施不黏业务"。</item>
    ///   <item>Utility <b>可取其他 Utility</b>（<c>IUtility : ICanGetUtility</c>）——基础设施可互相组合
    ///         （如配置表服务取资源服务来加载数据），与 <c>ISystem : ICanGetSystem</c> 对称。</item>
    ///   <item>纯函数工具天然无状态；**持有状态的"服务型" Utility 是刻意允许的**（资源/UI/池/配置即是），不是反模式。</item>
    ///   <item>命名空间约定：公共工具放 <c>Game.Framework.Utility</c>；层专用工具按子命名空间分（如 <c>Game.Framework.Systems.Utility</c>），由 <c>using</c> 控制可见范围。</item>
    ///   <item>需要 Inspector 配置或 Unity 生命周期时用 <see cref="Game.Framework.Utility.MonoUtilityBase"/>；纯 C# Utility 按所有权选择 <c>RegisterUtility</c> 或 <c>RegisterOwnedUtility</c>。</item>
    /// </list>
    /// </remarks>
    public interface IUtility : ICanGetUtility
    {
    }
}
