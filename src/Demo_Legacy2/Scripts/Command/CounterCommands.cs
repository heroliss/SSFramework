using Game.Framework.Command;
using R3;

namespace Game.Framework.Demo.Command
{
    /// <summary>
    /// 计数器相关 Command。
    /// </summary>
    /// <remarks>
    /// 全部用 <c>readonly struct</c>，零分配；写命令通过 <c>ctx.GetSystem&lt;T&gt;()</c> 调 System，
    /// 查询命令直接取 Model 的只读 RP——避免 System 透传字段。
    /// </remarks>

    public readonly struct IncrementCommand : ICommand
    {
        public void Execute(ICommandContext ctx)
            => ctx.GetSystem<System.ICounterSystem>().Increment();
    }

    public readonly struct DecrementCommand : ICommand
    {
        public void Execute(ICommandContext ctx)
            => ctx.GetSystem<System.ICounterSystem>().Decrement();
    }

    public readonly struct ResetCounterCommand : ICommand
    {
        public void Execute(ICommandContext ctx)
            => ctx.GetSystem<System.ICounterSystem>().Reset();
    }

    /// <summary>查询计数（只读订阅源）。View 通过此命令拿到 RP 引用，自行 Subscribe。</summary>
    public readonly struct GetCountStateCommand : ICommand<ReadOnlyReactiveProperty<int>>
    {
        public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx)
            => ctx.GetModel<Model.CounterModel>().Count;
    }

    /// <summary>查询累计 Command 次数（只读订阅源）。</summary>
    public readonly struct GetCommandCountStateCommand : ICommand<ReadOnlyReactiveProperty<int>>
    {
        public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx)
            => ctx.GetModel<Model.CounterModel>().CommandCount;
    }

    /// <summary>
    /// 发送日志事件（演示 View 通过 Command 间接获得 SendEvent 权限）。
    /// </summary>
    /// <remarks>View 没有 <c>ICanSendEvent</c> 权限，必须借助 Command 才能 SendEvent。</remarks>
    public readonly struct SendLogCommand : ICommand
    {
        public readonly string Message;
        public SendLogCommand(string msg) => Message = msg;

        public void Execute(ICommandContext ctx)
            => ctx.SendEvent(new Event.LogEvent(Message, UnityEngine.Time.time));
    }
}
