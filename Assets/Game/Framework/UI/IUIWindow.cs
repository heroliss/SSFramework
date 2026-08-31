using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.UI
{
    /// <summary>
    /// 渲染中立的窗口契约——只定义生命周期 hook，不暴露任何 UGUI / UI Toolkit 类型。
    /// UGUI 窗口（<c>UGuiWindowBase</c>）与 UI Toolkit 窗口（<c>UIToolkitWindowBase</c>）各自实现它，
    /// UI 框架核心（<see cref="UIUtility"/>）只面向本接口编排，对底层渲染技术无感。
    /// </summary>
    /// <remarks>
    /// 这些 hook 由 <see cref="UIUtility"/> 在 Unity 主线程的恰当时机调用，<b>不是</b> Unity 生命周期：<br/>
    /// 调用次序：<c>OnCreate</c>（实例化 + 绑定 Context 后一次）→ <c>OnOpen</c>（每次打开，收参数）→
    /// <c>OnOpenTransition</c>（入场过渡）→ 期间可能 <c>OnCover</c>/<c>OnReveal</c>（被上层盖住 / 重新露出）→
    /// <c>OnCloseTransition</c>（出场过渡）→ <c>OnClose</c>（每次正常逻辑关闭）。
    /// 缓存复用的窗口会再次 <c>OnOpen</c>；销毁由 backend 负责（UGUI 销毁 GameObject、UIToolkit Dispose 视图）。
    /// UI owner / Context teardown 是纯物理拆除，会跳过全部 hook，避免销毁期业务代码触碰已经释放的 Context；
    /// 因而必须持久化的数据不能只依赖 <c>OnClose</c>，应在数据变更或更长寿命 owner 的收口点提交。
    /// 实现类通常把这些显式实现，转发到 <c>protected virtual</c> 钩子，业务窗口只重写需要的那几个。
    /// </remarks>
    public interface IUIWindow
    {
        /// <summary>实例化并绑定 Context 后调用一次——做只接一次的初始化（查询子元素、订阅查询 Command、接按钮）。</summary>
        void OnCreate();

        /// <summary>每次打开（显示）时调用，<paramref name="args"/> 为打开参数（可空）。缓存复用的窗口也会再次收到。</summary>
        void OnOpen(object args);

        /// <summary>
        /// 每次正常 <c>Close</c> / <c>CloseAll</c> 逻辑关闭时调用——提交临时态、收尾。
        /// 在出场过渡完成之后、窗口被隐藏（缓存）或销毁之前；UI owner / Context teardown 不调用。
        /// </summary>
        void OnClose();

        /// <summary>同层有新窗口盖在本窗口之上时调用——典型用于暂停、停渲染省开销。</summary>
        void OnCover();

        /// <summary>盖在本窗口之上的窗口关闭、本窗口重新成为同层栈顶时调用——典型用于恢复。</summary>
        void OnReveal();

        /// <summary>
        /// 入场过渡（ADR-0020）：<c>OnOpen</c> 之后由框架调用，返回未完成的 task 期间框架全屏挡输入。
        /// 返回已完成 task（基类默认）= 无过渡、零开销。<paramref name="ct"/> 随 Context 销毁取消——动画实现应响应它。
        /// 只有 <paramref name="ct"/> 已取消时的 <see cref="System.OperationCanceledException"/> 属于正常生命周期收口；
        /// token 未取消时自行抛出的取消异常与其它异常一样会被框架记日志并视为完成（不会挡死输入）。
        /// 返回 task 可以在任意线程物理结束；核心会回主线程后再撤输入挡板或调用后续 hook/backend。
        /// </summary>
        UniTask OnOpenTransition(CancellationToken ct);

        /// <summary>
        /// 出场过渡（ADR-0020）：<c>OnClose</c> 之前由框架调用（窗口此时仍可见，播出场动画），期间全屏挡输入。
        /// 注意逻辑关闭已先行生效：此刻 <c>IsOpen</c> 已为 false、同类型可被重新打开——动画只是表现层残影。
        /// <c>CloseAll</c> / Context 销毁走立即路径，不会调用本 hook。
        /// 只有传入 token 已取消时的 <see cref="System.OperationCanceledException"/> 会静默收口；其它取消异常按 hook 故障记录，
        /// 但仍会放开挡板并完成 <c>OnClose</c> 与物理回收。
        /// 返回 task 可以在任意线程物理结束；核心会回主线程后再完成关闭提交。
        /// </summary>
        UniTask OnCloseTransition(CancellationToken ct);
    }
}
