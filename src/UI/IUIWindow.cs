namespace Game.Framework.UI
{
    /// <summary>
    /// 渲染中立的窗口契约——只定义生命周期 hook，不暴露任何 UGUI / UI Toolkit 类型。
    /// UGUI 窗口（<c>UGuiWindowBase</c>）与 UI Toolkit 窗口（<c>UIToolkitWindowBase</c>）各自实现它，
    /// UI 框架核心（<see cref="UIUtility"/>）只面向本接口编排，对底层渲染技术无感。
    /// </summary>
    /// <remarks>
    /// 这些 hook 由 <see cref="UIUtility"/> 在恰当时机调用，<b>不是</b> Unity 生命周期：<br/>
    /// 调用次序：<c>OnCreate</c>（实例化 + 绑定 Context 后一次）→ <c>OnOpen</c>（每次打开，收参数）→
    /// 期间可能 <c>OnCover</c>/<c>OnReveal</c>（被上层盖住 / 重新露出）→ <c>OnClose</c>（每次关闭）。
    /// 缓存复用的窗口会再次 <c>OnOpen</c>；销毁由 backend 负责（UGUI 销毁 GameObject、UIToolkit Dispose 视图）。
    /// 实现类通常把这些显式实现，转发到 <c>protected virtual</c> 钩子，业务窗口只重写需要的那几个。
    /// </remarks>
    public interface IUIWindow
    {
        /// <summary>实例化并绑定 Context 后调用一次——做只接一次的初始化（查询子元素、订阅查询 Command、接按钮）。</summary>
        void OnCreate();

        /// <summary>每次打开（显示）时调用，<paramref name="args"/> 为打开参数（可空）。缓存复用的窗口也会再次收到。</summary>
        void OnOpen(object args);

        /// <summary>每次关闭时调用——停动画、提交临时态等。之后窗口被隐藏（缓存）或销毁。</summary>
        void OnClose();

        /// <summary>同层有新窗口盖在本窗口之上时调用——典型用于暂停、停渲染省开销。</summary>
        void OnCover();

        /// <summary>盖在本窗口之上的窗口关闭、本窗口重新成为同层栈顶时调用——典型用于恢复。</summary>
        void OnReveal();
    }
}
