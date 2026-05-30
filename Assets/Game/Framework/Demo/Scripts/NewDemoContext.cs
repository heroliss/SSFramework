using Game.Framework.Context;
using Game.Framework.System;

namespace Game.Framework.Demo
{
    /// <summary>
    /// 新 Demo 场景上下文。注册 <see cref="CommandSystem"/> 让 Demo 内的演示按钮能跑 Command。
    /// </summary>
    /// <remarks>
    /// <b>为什么不继承 MonoGlobalContext：</b>避免把 <c>GameContext.Main</c> 设为 Demo Context——
    /// 用户在自己项目里挂 Demo 场景时不应污染他们的全局上下文。建议把本组件挂在场景根节点的子物体上，
    /// 在 Inspector 中设置 <c>_inheritFromGlobal = false</c>，独立运行。
    /// </remarks>
    public class NewDemoContext : MonoGameContextBase
    {
        protected override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
        }
    }
}
