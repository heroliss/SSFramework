using Game.Framework.Context;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 「多 Context · 作用域树」章的父作用域节点（挂在 DemoRoot 下、子作用域之上）。
    /// 注册 ScopedTag（父级值）+ ParentOnlyTag（只有父级有，用来演示子级回退）。
    /// </summary>
    public sealed class DemoScopeParent : MonoGameContextBase
    {
        protected override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterValue(new ScopedTag("父级的值"), typeof(ScopedTag));
            builder.RegisterValue(new ParentOnlyTag("只有父级注册了"), typeof(ParentOnlyTag));
        }
    }
}
