using System;
using Game.Framework.Command;
using Game.Framework.Internal;

namespace Game.Framework.Demo.Command
{
    /// <summary>
    /// 诊断 Command：检查指定类型是否能在当前 Container 中解析（包含父级回退）。
    /// </summary>
    /// <remarks>
    /// <b>仅 Demo / 诊断用。</b>业务代码不应该用"动态类型检查"来分支逻辑——这是把"层注册"
    /// 当数据用，反而绕开了框架的类型安全。<br/>
    /// 这里通过 <c>ctx is IGameContext</c> 拿到完整接口的 <c>TryResolve(Type, out object)</c>。
    /// </remarks>
    public readonly struct CheckResolveCommand : ICommand<bool>
    {
        public readonly Type Target;
        public CheckResolveCommand(Type t) => Target = t;

        public bool Execute(ICommandContext ctx)
        {
            if (Target == null) return false;
            if (ctx is IGameContext gctx) return gctx.TryResolve(Target, out _);
            return false;
        }
    }
}
