namespace Game.Framework.Internal
{
    /// <summary>
    /// 标记"持有 <see cref="IGameContext"/> 引用"的对象。框架扩展方法（<c>this.GetModel&lt;T&gt;()</c> 等）
    /// 通过此接口拿到上下文，避免依赖 <see cref="Game.Framework.Context.GameContext.Main"/> 静态全局。
    /// </summary>
    /// <remarks>
    /// <b>归属约束：</b>一个实例至多属于一个底层 <see cref="Game.Framework.Context.GameContext"/>。
    /// 重复附着到同一 Context（包括它的 Mono 代理）是幂等操作，跨 Context 复用则会 fail-fast；
    /// 真正需要跨作用域共享的无状态值不应实现本接口。这样可避免 <c>[Inject]</c> 依赖来自新 Context、
    /// 扩展方法却仍通过旧 Context 解析的“双重真源”。<br/>
    /// <b>谁该实现：</b>
    /// <list type="bullet">
    ///   <item>所有 <c>MonoXxxBase</c> 子类——框架已自动实现，业务派生类不用关心。</item>
    ///   <item>纯 C# 业务类（如手工 <c>new XxxSystem()</c> 后 <c>ctx.RegisterSystem</c> 的场景）——需要显式实现，并配合 <c>ctx.AttachTo(this)</c> 注入 GameContext 字段。</item>
    /// </list>
    /// <b>显式接口实现技巧：</b>各 Mono*Base 用 <c>IGameContext IHasGameContext.Context => _ctx;</c> 这种<b>显式接口实现</b>语法——
    /// 业务子类拿不到完整 <see cref="IGameContext"/>，只能用扩展方法（由 <c>ICanXxx</c> 权限接口约束），这是权限分层的关键一环。
    /// </remarks>
    public interface IHasGameContext
    {
        /// <summary>关联的上下文实例。在持有者初始化完成前可能为 null。</summary>
        IGameContext Context { get; }
    }
}
