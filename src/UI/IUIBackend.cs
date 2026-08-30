using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;

namespace Game.Framework.UI
{
    /// <summary>
    /// UI 渲染后端的 Interface——把“窗口在屏幕上的物理存在”这件渲染相关的事收在一个 Seam 后，
    /// 让 <see cref="UIUtility"/> 的栈/层/缓存/生命周期编排与具体渲染技术解耦。
    /// UGUI（Canvas / RectTransform）与 UI Toolkit（UIDocument / VisualElement）各提供一个 Adapter。
    /// </summary>
    /// <remarks>
    /// Implementation 只负责“加载资源 → 实例化 → 绑定 Context → 挂到对应层根 → 排序 → 显隐 → 销毁”这些物理动作，
    /// <b>不</b>负责窗口生命周期 hook（<c>OnOpen</c>/<c>OnCover</c>… 由核心调）。
    /// Adapter 内部维护 <c>窗口 → 物理对象</c> 的映射，故除 <see cref="CreateWindow"/> 外的方法只需传入
    /// <see cref="IUIWindow"/>。全部成员由 <see cref="UIUtility"/> 在 Unity 主线程调用；传入的 Context、元数据与窗口
    /// 都是借用值，物理对象、资源句柄和销毁顺序由 Adapter 持有。<br/>
    /// <see cref="CreateWindow"/> 以“成功才提交映射”为事务边界：返回非 null 前，窗口必须已完整绑定并挂入层级；
    /// 预期的资源/配置不可用返回 null。取消保持 <see cref="OperationCanceledException"/>，其它异常原样传播，二者都必须先回滚
    /// 已创建的层级、View 和资源句柄，不能把半窗口留给 <see cref="Teardown"/> 才兜底。
    /// </remarks>
    public interface IUIBackend
    {
        /// <summary>
        /// 按 <see cref="UILayer"/> 顺序建立各层根容器（幂等）。<see cref="UIUtility"/> 首次打开窗口前调用；
        /// 其它成员只会在初始化成功后调用。
        /// </summary>
        void Initialize();

        /// <summary>
        /// 加载窗口资源、实例化、绑定 <paramref name="context"/>，并挂到 <paramref name="meta"/> 指定层的栈顶。
        /// 成功返回由本 Adapter 持有物理生命周期的借用窗口实例；资源或窗口配置无法形成有效实例时返回 null。
        /// 调用方取消抛 <see cref="OperationCanceledException"/>；取消与其它异常都必须在返回前回滚部分创建状态。
        /// </summary>
        UniTask<IUIWindow> CreateWindow(UIWindowMeta meta, IGameContext context, CancellationToken ct);

        /// <summary>把窗口移到所属层内的最上方（重新聚焦已打开窗口时用）。</summary>
        void BringToFront(IUIWindow window);

        /// <summary>显示 / 隐藏窗口（缓存策略下"关闭=隐藏"、"再开=显示"）。</summary>
        void SetVisible(IUIWindow window, bool visible);

        /// <summary>在 <paramref name="ownerWindow"/> 之下铺 / 撤模态遮罩，拦截更下层输入。</summary>
        void SetModalMask(IUIWindow ownerWindow, bool on);

        /// <summary>
        /// 销毁窗口及其加载资源（UGUI 销毁 GameObject、UIToolkit Dispose 视图并摘出可视树）。
        /// 未知或已销毁窗口为幂等 no-op；调用者不得绕过本方法自行销毁借用实例。
        /// </summary>
        void DestroyWindow(IUIWindow window);

        /// <summary>
        /// 全屏挡/放输入（盖在<b>所有</b>层之上的透明挡板）。核心在任一窗口过渡进行中开启（计数归零关闭），
        /// 防连点、防动画期间操作（ADR-0020）。实现应幂等（重复同值调用无害）。
        /// </summary>
        void SetInputBlocked(bool blocked);

        /// <summary>拆除全部层根、残留窗口与资源句柄并恢复到未初始化状态；幂等，由 <see cref="UIUtility"/> 释放时调用。</summary>
        void Teardown();
    }
}
