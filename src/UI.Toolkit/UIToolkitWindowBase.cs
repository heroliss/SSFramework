using Game.Framework.UI;

namespace Game.Framework.UI.Toolkit
{
    /// <summary>
    /// UI Toolkit 窗口基类——在 <see cref="UIToolkitViewBase"/>（纯 C# View 接入）之上实现 <see cref="IUIWindow"/> 生命周期。
    /// 享有 View 的全部能力：自动注入、<c>Bag</c>、<c>this.ExecuteCommand</c> 等；窗口由 UI 框架（<see cref="UIUtility"/>）创建与调度。
    /// </summary>
    /// <remarks>
    /// <b>怎么写业务窗口：</b>继承本类（需<b>无参构造</b>，框架用 <c>Activator</c> 实例化），在 <see cref="UIToolkitViewBase.OnCreated"/>
    /// 里搭 UI / 查询 UXML 子元素 + 接线，在 <see cref="OnOpen"/> 里取打开参数。元数据用类上的 <see cref="UIWindowAttribute"/> 声明
    /// （<c>Asset</c> 指向 UXML，留空则纯代码搭建）。<br/>
    /// 生命周期 hook 由框架调用，<see cref="UIToolkitViewBase.OnCreated"/> 经框架的 <c>OnCreate</c> 触发（绑定 Context 时不重复调）。
    /// </remarks>
    public abstract class UIToolkitWindowBase : UIToolkitViewBase, IUIWindow
    {
        // 显式实现 IUIWindow：OnCreate 触发视图基类的一次性建 UI/接线；其余转发到 protected 钩子。
        void IUIWindow.OnCreate() => InvokeCreated();
        void IUIWindow.OnOpen(object args) => OnOpen(args);
        void IUIWindow.OnClose() => OnClose();
        void IUIWindow.OnCover() => OnCover();
        void IUIWindow.OnReveal() => OnReveal();

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
