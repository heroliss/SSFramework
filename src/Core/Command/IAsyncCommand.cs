using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Command
{
    /// <summary>
    /// 异步 Command 接口（无返回值），返回 UniTask。
    /// 用法同 ICommand：直接实现接口，无需基类。默认 readonly struct（零分配，经 ctx 参数访问层；struct 也可有 async 方法）；
    /// 仅当确实需要 [Inject] 字段注入时才用 class——这与同步/异步无关。
    /// </summary>
    public interface IAsyncCommand : ICommandBase
    {
        /// <summary>
        /// 异步执行命令。应把 <paramref name="cancellationToken"/> 作为本次执行的有效令牌并继续向下游透传，
        /// 而不是改用 <paramref name="ctx"/> 的 Context 生命周期令牌；Context 与解析结果均只借用。
        /// 命令可在工作线程处理纯数据，分发器会在 Unity 主线程交付成功、失败或取消终态。
        /// </summary>
        UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken);
    }
}
