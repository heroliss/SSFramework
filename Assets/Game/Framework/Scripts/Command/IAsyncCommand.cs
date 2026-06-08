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
        UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken);
    }
}
