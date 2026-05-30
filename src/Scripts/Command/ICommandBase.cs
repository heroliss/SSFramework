namespace Game.Framework.Command
{
    /// <summary>
    /// Command 基接口。把"用户意图"原子化封装成可序列化、可拦截、可重放的一次行为。
    /// </summary>
    /// <remarks>
    /// <b>职责：</b>View 表达"想干什么"的唯一入口；Command 内部组合调用 System 完成"如何做"。<br/>
    /// <b>谁该用：</b>所有"用户主动触发的操作"——按钮点击、玩家输入、网络回包后的本地动作等。<br/>
    /// <b>边界：</b>
    /// <list type="bullet">
    ///   <item>Command <b>不</b>继承权限接口（<see cref="Game.Framework.Internal.ICanGetModel"/> 等）。<br/>
    ///         原因：struct Command 调 <c>this.GetXxx&lt;T&gt;()</c> 扩展方法必然装箱、且无 <see cref="Game.Framework.Internal.IHasGameContext"/>，会崩。<br/>
    ///         统一通过 <see cref="ICommandContext"/> 参数访问层（<c>ctx.GetSystem&lt;T&gt;()</c> / <c>ctx.SendEvent&lt;T&gt;()</c>），无装箱无分配。</item>
    ///   <item>不直接用此接口——业务实现 <see cref="ICommand"/> / <see cref="ICommand{TResult}"/> / <see cref="IAsyncCommand"/> / <see cref="IAsyncCommand{TResult}"/> 之一。</item>
    ///   <item>同步默认 <c>readonly struct</c>（零 GC）；依赖项多时改 <c>class</c> 用 <c>[Inject]</c> 自动注入。</item>
    ///   <item>异步统一用 <c>class</c> + <c>IAsyncCommand</c>，签名带 <c>CancellationToken cancellationToken</c>。</item>
    /// </list>
    /// </remarks>
    public interface ICommandBase
    {
    }
}
