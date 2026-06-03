using Game.Framework.Context;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 「多 Context · 作用域树」章的子作用域节点（嵌在 DemoScopeParent 下）。
    /// 只注册 ScopedTag（覆盖父级同类型）；ParentOnlyTag 故意不注册，留给"回退到父级"演示。
    /// </summary>
    public sealed class DemoScopeChild : MonoGameContextBase
    {
        protected override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterValue(new ScopedTag("子级的值（覆盖）"), typeof(ScopedTag));
    }
}
