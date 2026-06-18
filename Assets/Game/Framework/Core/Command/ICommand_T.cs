namespace Game.Framework.Command
{
    /// <summary>
    /// 带返回值的同步 Command 接口。
    /// 用法同 ICommand：直接实现接口，无需基类。class 支持 [Inject]，struct 仅用 ctx 参数访问层。
    /// </summary>
    public interface ICommand<TResult> : ICommandBase
    {
        TResult Execute(ICommandContext ctx);
    }
}
