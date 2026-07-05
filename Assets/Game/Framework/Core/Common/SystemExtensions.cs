using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Systems;

namespace Game.Framework.Common
{
    /// <summary>
    /// System 层访问扩展：通过 <c>this.GetSystem&lt;T&gt;()</c> 按类型解析 System 实例。
    /// </summary>
    /// <remarks>
    /// <b>谁能调：</b><see cref="ICanGetSystem"/> 实现者——System（持有 Context 的层）。<b>Command 不走本扩展</b>：经 <see cref="Game.Framework.Command.ICommandContext"/> 参数 <c>ctx.GetSystem&lt;T&gt;()</c> 访问（struct 用扩展会装箱、且 Command 无 <see cref="IHasGameContext"/>）。<br/>
    /// <b>上下文解析：</b>通过 <see cref="IHasGameContext"/> 拿；MonoXxxBase 已自动实现。<br/>
    /// <b>未注册时：</b>抛 <c>InvalidOperationException</c>。注意层标记接口 <see cref="ISystem"/> 本身<b>不</b>注册，要用具体类型或派生接口（如 <c>IPlayerSystem</c>）解析。
    /// </remarks>
    public static class SystemExtensions
    {
        /// <summary>按精确类型键解析 System。<typeparamref name="T"/> 必须是注册时使用的 contract 类型（具体类或派生接口）。</summary>
        public static T GetSystem<T>(this ICanGetSystem self) where T : class, ISystem
        {
            return GameContext.ResolveFrom(self).GetSystem<T>();
        }
    }
}
