using System.Threading;
using Game.Framework.Event;
using Game.Framework.Model;
using Game.Framework.System;
using Game.Framework.Utility;

namespace Game.Framework.Command
{
    /// <summary>
    /// Command 执行时拿到的<b>受限上下文接口</b>——只暴露 Command 合法的能力，隐藏 Container、Inject、Register* 等
    /// 注册管理能力，防止 Command 内顺手做出"绕过框架约束"的事。
    /// </summary>
    /// <remarks>
    /// <b>能做：</b>读取 Model/System/Utility、发送 Event、读取 CancellationToken、调用子 Command。<br/>
    /// <b>不能做：</b>注册/反注册层、修改 Container、写 <see cref="Game.Framework.Context.GameContext.Main"/>——
    /// 这些都不是命令应有的副作用。<br/>
    /// <b>谁会拿到：</b>所有 Command 实现（struct + class，含 async 重载）的 <c>Execute / ExecuteAsync</c> 参数。
    /// struct Command 必须通过它访问层（不能用 <c>this.GetXxx</c> 扩展方法，会装箱）；
    /// class Command 可用 <see cref="Game.Framework.Common.InjectAttribute"/> 字段配合，也可继续走 ctx。
    /// </remarks>
    public interface ICommandContext
    {
        T GetModel<T>()   where T : class, IModel;
        T GetSystem<T>()  where T : class, ISystem;
        T GetUtility<T>() where T : class, IUtility;
        void SendEvent<T>(T e) where T : IEvent;
        CancellationToken CancellationToken { get; }

        void ExecuteCommand<T>(T command) where T : ICommand;
        TResult ExecuteCommand<TResult>(ICommand<TResult> command);
        TResult ExecuteCommand<T, TResult>(T command) where T : ICommand<TResult>;
    }
}
