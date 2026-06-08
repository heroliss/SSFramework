using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Utility;

namespace Game.Framework.Common
{
    /// <summary>
    /// Utility 层访问扩展：通过 <c>this.GetUtility&lt;T&gt;()</c> 按类型解析 Utility 实例。
    /// </summary>
    /// <remarks>
    /// <b>谁能调：</b><see cref="ICanGetUtility"/> 实现者——Model / System / View（持有 Context 的层）。<b>Command 不走本扩展</b>：经 <see cref="Game.Framework.Command.ICommandContext"/> 参数 <c>ctx.GetUtility&lt;T&gt;()</c> 访问。Utility 自身不实现（不反向依赖其他层）。<br/>
    /// <b>上下文解析：</b>通过 <see cref="IHasGameContext"/> 拿；MonoXxxBase 已自动实现。<br/>
    /// <b>未注册时：</b>抛 <c>InvalidOperationException</c>。Utility 通常在 <c>InstallBindings</c> 构建期注册或挂 <see cref="Game.Framework.Utility.MonoUtilityBase"/> 子节点自动注册。
    /// </remarks>
    public static class UtilityExtensions
    {
        /// <summary>按精确类型键解析 Utility。<typeparamref name="T"/> 必须是注册时使用的 contract 类型。</summary>
        public static T GetUtility<T>(this ICanGetUtility self) where T : class, IUtility
        {
            return GameContext.ResolveFrom(self).GetUtility<T>();
        }
    }
}
