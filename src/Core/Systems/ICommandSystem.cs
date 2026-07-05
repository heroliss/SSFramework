using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Context;

namespace Game.Framework.Systems
{
    /// <summary>
    /// 命令处理系统接口——所有 Command 的执行都经此 dispatcher 统一分发，业务可替换实现以加入横切逻辑
    /// （日志 / 回放 / 撤销 / 优先级队列 / 调试拦截…）。
    /// </summary>
    /// <remarks>
    /// <b>设计要点：</b>无状态 dispatcher——每次执行都用调用方传入的 <see cref="GameContext"/>，
    /// 跨级继承时仍能正确响应每个上下文的局部 DI 注册（子级覆盖父级）。自定义实现保持无状态约定即可。<br/>
    ///
    /// <b>怎么替换：</b>实现本接口并通过 <c>builder.RegisterValue(new MyCommandSystem(), typeof(ICommandSystem))</c>
    /// 覆盖默认的 <see cref="CommandSystem"/>。装饰器模式可叠加多层横切（包住内层、六个重载泛型直转发）——
    /// 框架自带的 <see cref="LoggingCommandSystem"/>（命令流水记录，供诊断面板）就是这个模式的现成样板，
    /// 自定义装饰器（回放 / 撤销 / 拦截）照它写即可。
    ///
    /// <b>重载选择指南：</b>
    /// <list type="bullet">
    ///   <item><see cref="ExecuteCommand{T}"/>：通用同步 Command（无返回值），class 或 struct 均可。</item>
    ///   <item><see cref="ExecuteCommand{TResult}"/>：同步 Command（有返回值），用于 class Command 或接口引用。</item>
    ///   <item><see cref="ExecuteCommand{T, TResult}"/>：同步 Command（有返回值），<b>struct Command 必须用此重载</b>——双泛型保持值类型语义，避免装箱。</item>
    ///   <item>异步重载同上，将 <c>UniTask</c> 代替 <c>void</c>/<c>TResult</c>。</item>
    /// </list>
    ///
    /// <b>异步取消语义：</b><c>cancellationToken</c> 已合并 Context 销毁与调用方传入的 token，命令实现只用这一个参数。
    /// </remarks>
    public interface ICommandSystem
    {
        void ExecuteCommand<T>(T command, GameContext ctx) where T : ICommand;
        TResult ExecuteCommand<TResult>(ICommand<TResult> command, GameContext ctx);
        UniTask ExecuteCommandAsync<T>(T command, GameContext ctx, CancellationToken cancellationToken) where T : IAsyncCommand;
        UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, GameContext ctx, CancellationToken cancellationToken);

        /// <summary>struct Command + 返回值：双泛型保持值类型语义，避免装箱。</summary>
        TResult ExecuteCommand<T, TResult>(T command, GameContext ctx) where T : ICommand<TResult>;

        /// <summary>struct AsyncCommand + 返回值：双泛型保持值类型语义，避免装箱。</summary>
        UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, GameContext ctx, CancellationToken cancellationToken) where T : IAsyncCommand<TResult>;
    }
}
