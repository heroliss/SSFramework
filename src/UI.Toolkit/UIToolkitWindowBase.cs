using System.Threading;
using Cysharp.Threading.Tasks;
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
    /// 正常逻辑关闭会调用 <see cref="OnClose"/>；UI owner / Context teardown 会跳过 hook 做纯物理拆除，
    /// 因此关键持久化不要只依赖 <see cref="OnClose"/>。刻意让物理任务越过窗口生命周期时，异步 continuation
    /// 应先检查 <see cref="CanUpdateVisuals"/>，不要自行复制只覆盖正常 Close 的 <c>_closed</c> 标记。
    /// </remarks>
    public abstract class UIToolkitWindowBase : UIToolkitViewBase, IUIWindow
    {
        private bool _isLogicallyOpen;

        /// <summary>
        /// 当前窗口是否仍允许更新自己的可视元素：只有正常打开后、逻辑关闭前，且 View 尚未被物理释放时为 true。
        /// <para>默认把异步工作绑定 <c>Bag.DisposeToken</c> 时无需额外判断；仅当包下载等物理任务刻意忽略窗口 token、
        /// 会在关闭后继续时，才在每个 await 后用本属性拦住迟到 UI 写入。它同时覆盖正常 Close、Cache 隐藏、
        /// Context / UI owner teardown 和缓存实例重开，不要在派生窗口再维护第二份 <c>_closed</c>。</para>
        /// </summary>
        protected bool CanUpdateVisuals => _isLogicallyOpen && !IsDisposed;

        // 显式实现 IUIWindow：OnCreate 触发视图基类的一次性建 UI/接线；其余转发到 protected 钩子。
        void IUIWindow.OnCreate() => InvokeCreated();
        void IUIWindow.OnOpen(object args)
        {
            _isLogicallyOpen = true;
            OnOpen(args);
        }

        void IUIWindow.OnClose()
        {
            _isLogicallyOpen = false;
            OnClose();
        }
        void IUIWindow.OnCover() => OnCover();
        void IUIWindow.OnReveal() => OnReveal();
        UniTask IUIWindow.OnOpenTransition(CancellationToken ct) => OnOpenTransition(ct);
        UniTask IUIWindow.OnCloseTransition(CancellationToken ct) => OnCloseTransition(ct);

        /// <summary>每次打开（显示）调用，<paramref name="args"/> 为打开参数（可空）。</summary>
        protected virtual void OnOpen(object args) { }

        /// <summary>每次正常逻辑关闭调用——停动画、提交临时态等。之后窗口被隐藏（缓存）或销毁；UI owner / Context teardown 不调用。</summary>
        protected virtual void OnClose() { }

        /// <summary>被同层新窗口盖住时调用。</summary>
        protected virtual void OnCover() { }

        /// <summary>盖在上面的窗口移开、本窗口重新成为同层栈顶时调用。</summary>
        protected virtual void OnReveal() { }

        /// <summary>
        /// 入场过渡：<see cref="OnOpen"/> 之后播放，返回未完成的 task 期间框架全屏挡输入（ADR-0020）。
        /// 默认无过渡（零开销）。动画实现应响应 <paramref name="ct"/>（Context 销毁时取消）。
        /// </summary>
        protected virtual UniTask OnOpenTransition(CancellationToken ct) => UniTask.CompletedTask;

        /// <summary>
        /// 出场过渡：<see cref="OnClose"/> 之前播放（窗口仍可见），期间全屏挡输入。逻辑关闭已先行生效
        /// （<c>IsOpen</c> 已 false、同类型可重开），动画只是表现层残影。<c>CloseAll</c> / Context 销毁不播。
        /// </summary>
        protected virtual UniTask OnCloseTransition(CancellationToken ct) => UniTask.CompletedTask;
    }
}
