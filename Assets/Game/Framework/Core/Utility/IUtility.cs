namespace Game.Framework.Utility
{
    /// <summary>
    /// Utility 层标记接口。无状态工具——把"通用计算"和"业务逻辑"分开。
    /// </summary>
    /// <remarks>
    /// <b>职责：</b>提供纯函数能力（加密、格式化、序列化、随机数、坐标变换…），所有层都可直接调用。<br/>
    /// <b>谁该用：</b>所有"不依赖游戏状态、可以在任何上下文里跑相同结果"的工具代码。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item>Utility <b>不持有业务状态</b>——存了状态就该升级为 Model 或 System。</item>
    ///   <item>Utility 不读 Model/System，避免"工具反向依赖业务"的耦合。</item>
    ///   <item>命名空间约定：公共工具放 <c>Game.Framework.Utility</c>；层专用工具按子命名空间分（如 <c>Game.Framework.System.Utility</c>），由 <c>using</c> 控制可见范围。</item>
    ///   <item>Mono 路径用 <see cref="Game.Framework.Utility.MonoUtilityBase"/>（仅当需要 Inspector 配置或 Unity 生命周期时）；纯 C# 路径用 <c>builder.RegisterValue</c> 直接注册。</item>
    /// </list>
    /// </remarks>
    public interface IUtility
    {
    }
}