using Game.Framework.View;

namespace Game.Framework.UI.UGui
{
    /// <summary>
    /// UGUI 窗口基类——一个挂在窗口 prefab 根上的 <see cref="MonoViewBase"/>，再实现 <see cref="IUIWindow"/> 生命周期。
    /// 享有 <c>MonoViewBase</c> 的全部能力：实例化到层根下自动注入、<c>Bag</c> 生命周期、<c>this.ExecuteCommand</c> 等。
    /// </summary>
    /// <remarks>
    /// <b>怎么写业务窗口：</b>继承本类，在 <see cref="OnCreated"/> 里接线（订阅查询 Command、接按钮），在 <see cref="OnOpen"/> 里取打开参数。
    /// <b>不要</b>覆写 Awake——注入由 <c>MonoViewBase</c> 负责。资源两种来源：<c>[UIWindow(Asset="ui/x")]</c> 指 prefab（拖好引用），
    /// 或 <b>Asset 留空纯代码搭建</b>（backend 空 GameObject + AddComponent，窗口自己代码搭 UGUI 控件）。<br/>
    /// <b>生命周期 hook</b>（由 <see cref="UIUtility"/> 调，非 Unity 生命周期）：
    /// <c>OnCreated</c>（建后一次）→ <c>OnOpen</c>（每次打开）→ 可能 <c>OnCover</c>/<c>OnReveal</c> → <c>OnClose</c>（每次关闭）。
    /// 销毁由 backend <c>Destroy</c> GameObject → <c>MonoViewBase.OnDestroy</c> → <c>Bag.Dispose</c> 退订。<br/>
    /// 元数据（层 / 资源 prefab / 缓存 / 模态）用类上的 <see cref="UIWindowAttribute"/> 声明。
    /// </remarks>
    public abstract class UGuiWindowBase : MonoViewBase, IUIWindow
    {
        // 显式实现 IUIWindow：把渲染中立的窗口 hook 转发到 protected 钩子，业务窗口只重写需要的。
        void IUIWindow.OnCreate() => OnCreated();
        void IUIWindow.OnOpen(object args) => OnOpen(args);
        void IUIWindow.OnClose() => OnClose();
        void IUIWindow.OnCover() => OnCover();
        void IUIWindow.OnReveal() => OnReveal();

        /// <summary>窗口建好、Context 已注入后调用一次——接线（订阅查询 Command、接按钮）。此时各层就绪，可直接 <c>this.ExecuteCommand(...)</c>。</summary>
        protected virtual void OnCreated() { }

        /// <summary>每次打开（显示）调用，<paramref name="args"/> 为打开参数（可空）。</summary>
        protected virtual void OnOpen(object args) { }

        /// <summary>每次关闭调用——停动画、提交临时态等。之后窗口被隐藏（缓存）或销毁。</summary>
        protected virtual void OnClose() { }

        /// <summary>被同层新窗口盖住时调用。</summary>
        protected virtual void OnCover() { }

        /// <summary>盖在上面的窗口移开、本窗口重新成为同层栈顶时调用。</summary>
        protected virtual void OnReveal() { }
    }
}
