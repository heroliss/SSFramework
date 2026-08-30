using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Event;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.Utility;

namespace Game.Framework.Command
{
    /// <summary>
    /// Command 执行时拿到的<b>受限上下文接口</b>——只暴露 Command 合法的能力，隐藏 Container、Inject、Register* 等
    /// 注册管理能力，防止 Command 内顺手做出"绕过框架约束"的事。
    /// </summary>
    /// <remarks>
    /// <b>能做：</b>读取 Model/System/Utility、发送 Event、读取 CancellationToken、调用同步 / 异步子 Command。<br/>
    /// <b>不能做：</b>注册/反注册层、修改 Container、写 <see cref="Game.Framework.Context.GameContext.Main"/>——
    /// 这些都不是命令应有的副作用。<br/>
    /// <b>谁会拿到：</b>所有 Command 实现（struct + class，含 async 重载）的 <c>Execute / ExecuteAsync</c> 参数。
    /// struct Command 必须通过它访问层（不能用 <c>this.GetXxx</c> 扩展方法，会装箱）；
    /// class Command 可用 <see cref="Game.Framework.Common.InjectAttribute"/> 字段配合，也可继续走 ctx。
    /// <br/><b>线程与所有权：</b>全部成员在 Unity 主线程调用；Command 只借用 Context 与解析结果，
    /// 不拥有任何 Model/System/Utility。同步与异步子 Command 的异常都原样传播。
    /// </remarks>
    public interface ICommandContext
    {
        /// <summary>解析当前 Context 可见的 Model；未注册或 Context 已释放时抛异常。</summary>
        T GetModel<T>()   where T : class, IModel;
        /// <summary>解析当前 Context 可见的 System；未注册或 Context 已释放时抛异常。</summary>
        T GetSystem<T>()  where T : class, ISystem;
        /// <summary>解析当前 Context 可见的 Utility；未注册或 Context 已释放时抛异常。</summary>
        T GetUtility<T>() where T : class, IUtility;

        /// <summary>在当前 Context 内同步发送瞬时 Event；不跨 Context 转发，也不保存历史。</summary>
        void SendEvent<T>(T e = default) where T : IEvent;  // 默认值：无数据事件可直接 ctx.SendEvent<T>()，与 GameContext / this.SendEvent 扩展一致

        /// <summary>
        /// 所属 Context 的生命周期令牌，不一定等于当前异步 Command 收到的执行令牌。
        /// <c>IAsyncCommand.ExecuteAsync</c> 内应优先使用方法参数 token，并把它显式传给异步子 Command。
        /// </summary>
        CancellationToken CancellationToken { get; }

        /// <summary>同步执行无返回值子 Command；异常原样传播。</summary>
        void ExecuteCommand<T>(T command) where T : ICommand;
        /// <summary>同步执行接口形式的带返回值子 Command；异常原样传播。</summary>
        TResult ExecuteCommand<TResult>(ICommand<TResult> command);
        /// <summary>以双泛型形式同步执行带返回值 struct 子 Command，避免装箱。</summary>
        TResult ExecuteCommand<T, TResult>(T command) where T : ICommand<TResult>;

        /// <summary>
        /// 异步执行无返回值子 Command。必须显式传入父 Command 收到的
        /// <paramref name="cancellationToken"/>；dispatcher 不会再隐式与 Context token 合并。
        /// </summary>
        UniTask ExecuteCommandAsync<T>(T command, CancellationToken cancellationToken) where T : IAsyncCommand;
        /// <summary>异步执行接口形式的带返回值子 Command；取消与异常通过返回 task 原样传播。</summary>
        UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, CancellationToken cancellationToken);
        /// <summary>以双泛型形式异步执行带返回值 struct 子 Command，避免装箱；token 由调用方显式透传。</summary>
        UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, CancellationToken cancellationToken) where T : IAsyncCommand<TResult>;
    }
}
