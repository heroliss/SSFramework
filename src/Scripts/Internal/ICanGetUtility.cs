namespace Game.Framework.Internal
{
    /// <summary>
    /// 权限标记：实现者可通过 <c>this.GetUtility&lt;T&gt;()</c> 扩展方法获取 Utility。
    /// </summary>
    /// <remarks>
    /// 实现者：所有层（Model / System / View / Utility / Command）。<br/>
    /// Utility 是无状态工具层，任何层都可读取，因此权限最宽。<br/>
    /// 空接口，仅做编译期权限分发，业务代码无须自己实现。
    /// </remarks>
    public interface ICanGetUtility
    {
    }
}