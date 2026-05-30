using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Command
{
    /// <summary>
    /// 异步 Command 接口（无返回值），返回 UniTask。
    /// 用法同 ICommand：直接实现接口，无需基类。class 支持 [Inject]，struct 仅用 ctx 参数访问层。
    /// </summary>
    public interface IAsyncCommand : ICommandBase
    {
        UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken);
    }
}
