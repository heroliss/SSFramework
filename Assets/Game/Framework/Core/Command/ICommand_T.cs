namespace Game.Framework.Command
{
    /// <summary>
    /// 带返回值的同步 Command 接口。
    /// 用法同 ICommand：直接实现接口，无需基类。class 支持 [Inject]，struct 仅用 ctx 参数访问层。
    /// </summary>
    public interface ICommand<TResult> : ICommandBase
    {
        /// <summary>
        /// 在当前 Context 的受限视图中同步执行并返回结果。<paramref name="ctx"/> 只借用，不能缓存到命令之外；
        /// 未处理异常会原样传播给分发入口。
        /// </summary>
        TResult Execute(ICommandContext ctx);
    }
}
