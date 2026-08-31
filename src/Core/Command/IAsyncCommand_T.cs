using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Command
{
    /// <summary>
    /// 带返回值的异步 Command 接口。
    /// 用法同 ICommand：直接实现接口，无需基类。默认 readonly struct（零分配，经 ctx 参数访问层；struct 也可有 async 方法）；
    /// 仅当确实需要 [Inject] 字段注入时才用 class——这与同步/异步无关。
    /// </summary>
    public interface IAsyncCommand<TResult> : ICommandBase
    {
        /// <summary>
        /// 异步执行命令并返回结果。应把 <paramref name="cancellationToken"/> 作为本次执行的有效令牌继续透传；
        /// Context 与解析结果均只借用。命令可在工作线程处理纯数据，分发器会在 Unity 主线程交付所有终态。
        /// </summary>
        UniTask<TResult> ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken);
    }
}
