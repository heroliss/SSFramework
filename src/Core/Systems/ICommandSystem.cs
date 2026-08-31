using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Context;

namespace Game.Framework.Systems
{
    /// <summary>
    /// 命令分发器接口——所有 Command 的执行都经此 dispatcher 统一分发，业务可替换实现以加入横切逻辑
    /// （日志 / 回放 / 撤销 / 优先级队列 / 调试拦截…）。
    /// </summary>
    /// <remarks>
    /// <b>层级定位：</b>名称中的 <c>System</c> 是早期公共命名，不表示五层业务 <see cref="ISystem"/>；
    /// 本接口是 Context 持有的基础设施 Seam，既不继承 <see cref="ISystem"/>，也不通过
    /// <c>RegisterSystem</c> 注册。容器按精确契约解析，应使用
    /// <c>builder.RegisterValue(dispatcher, typeof(ICommandSystem))</c>。<br/>
    ///
    /// <b>设计要点：</b>无状态 dispatcher——每次执行都用调用方传入的 <see cref="GameContext"/>，
    /// 跨级继承时仍能正确响应每个上下文的局部 DI 注册（子级覆盖父级）。自定义实现保持无状态约定即可。<br/>
    ///
    /// <b>怎么替换：</b>实现本接口并通过 <c>builder.RegisterValue(new MyCommandDispatcher(), typeof(ICommandSystem))</c>
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
    /// <b>异步取消语义：</b>dispatcher 不创建或合并 token，只把入口已经决定好的
    /// <c>cancellationToken</c> 原样交给 Command：<c>IGameContext</c> 无 token 重载传 Context token，
    /// 显式 token 重载传调用方 token；View 扩展入口才会链接 Context、View 销毁与调用方 token。
    /// 自定义 Implementation 必须保留这一语义，并让取消与 Command 异常通过返回的 <c>UniTask</c> 原样传播。<br/>
    ///
    /// <b>异步线程语义：</b>Command 可在内部下工作线程处理纯数据，但 dispatcher 返回的 <c>UniTask</c>
    /// 必须在 Unity 主线程交付成功、失败或取消；调用方 await 后才能直接继续使用 Context / Event / Model。
    /// 默认 <see cref="CommandSystem"/> 与 <see cref="LoggingCommandSystem"/> 都会封闭这条边界；自定义实现也必须保持。
    /// </remarks>
    public interface ICommandSystem
    {
        /// <summary>同步执行无返回值 Command；dispatcher 不拥有 <paramref name="command"/>，异常原样传播。</summary>
        void ExecuteCommand<T>(T command, GameContext ctx) where T : ICommand;

        /// <summary>同步执行接口形式的带返回值 Command；适合 class 或已有接口引用，异常原样传播。</summary>
        TResult ExecuteCommand<TResult>(ICommand<TResult> command, GameContext ctx);

        /// <summary>
        /// 异步执行无返回值 Command；<paramref name="cancellationToken"/> 由上层入口决定并原样转发，
        /// dispatcher 不负责链接或释放 token source。
        /// </summary>
        UniTask ExecuteCommandAsync<T>(T command, GameContext ctx, CancellationToken cancellationToken) where T : IAsyncCommand;

        /// <summary>异步执行接口形式的带返回值 Command；token、取消与异常语义同无返回值重载。</summary>
        UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, GameContext ctx, CancellationToken cancellationToken);

        /// <summary>struct Command + 返回值：双泛型保持值类型语义，避免装箱。</summary>
        TResult ExecuteCommand<T, TResult>(T command, GameContext ctx) where T : ICommand<TResult>;

        /// <summary>struct AsyncCommand + 返回值：双泛型保持值类型语义，避免装箱。</summary>
        UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, GameContext ctx, CancellationToken cancellationToken) where T : IAsyncCommand<TResult>;
    }
}
