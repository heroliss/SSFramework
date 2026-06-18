namespace Game.Framework.Command
{
    /// <summary>
    /// 同步 Command 接口（无返回值）。
    /// 用法：直接实现接口即可，无需继承基类。
    /// - class Command：可在字段上用 [Inject]，CommandSystem 会在 Execute 前自动注入；Execute 内用 ctx 参数访问层。
    /// - struct Command：不能用 [Inject]（struct 字段在装箱副本上被反射写入，原值不变）；
    ///   只用 ctx 参数访问层（ctx.GetSystem&lt;T&gt;() / ctx.GetModel&lt;T&gt;() 等），零装箱零分配。
    /// </summary>
    public interface ICommand : ICommandBase
    {
        void Execute(ICommandContext ctx);
    }
}
