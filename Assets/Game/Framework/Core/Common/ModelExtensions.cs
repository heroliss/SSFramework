using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Model;

namespace Game.Framework.Common
{
    /// <summary>
    /// Model 层访问扩展：通过 <c>this.GetModel&lt;T&gt;()</c> 按类型解析 Model 实例。
    /// </summary>
    /// <remarks>
    /// <b>谁能调：</b><see cref="ICanGetModel"/> 实现者——System / Model（持有 Context 的层）。<b>Command 不走本扩展</b>：经 <see cref="Game.Framework.Command.ICommandContext"/> 参数 <c>ctx.GetModel&lt;T&gt;()</c> 访问（struct 用扩展会装箱、且 Command 无 <see cref="IHasGameContext"/>）。<br/>
    /// <b>上下文解析：</b>通过 <see cref="IHasGameContext"/> 拿；MonoXxxBase 已自动实现。<br/>
    /// <b>未注册时：</b><see cref="GameContext.Resolve"/> 抛 <c>InvalidOperationException</c>，调用方应保证 Model 已注册（Mono 路径靠 Hierarchy 父子关系，纯 C# 路径调 <c>ctx.RegisterModel</c>）。
    /// </remarks>
    public static class ModelExtensions
    {
        /// <summary>按精确类型键解析 Model。<typeparamref name="T"/> 必须是注册时使用的 contract 类型（具体类或接口）。</summary>
        public static T GetModel<T>(this ICanGetModel self) where T : class, IModel
        {
            return GameContext.ResolveFrom(self).GetModel<T>();
        }
    }
}
