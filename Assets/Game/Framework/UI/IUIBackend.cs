using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;

namespace Game.Framework.UI
{
    /// <summary>
    /// UI 渲染后端的 port（端口）——把"窗口在屏幕上的物理存在"这件渲染相关的事抽象出来，
    /// 让 <see cref="UIUtility"/> 的栈/层/缓存/生命周期编排与具体渲染技术解耦。
    /// UGUI（Canvas / RectTransform）与 UI Toolkit（UIDocument / VisualElement）各实现一个 adapter。
    /// </summary>
    /// <remarks>
    /// 实现方只负责"加载资源 → 实例化 → 挂到对应层根 → 排序 → 显隐 → 销毁"这些物理动作，
    /// <b>不</b>负责窗口生命周期 hook（<c>OnOpen</c>/<c>OnCover</c>… 由核心调）。
    /// backend 内部维护 <c>窗口 → 物理对象</c> 的映射，故除 <see cref="CreateWindow"/> 外的方法只需传入 <see cref="IUIWindow"/>。
    /// </remarks>
    public interface IUIBackend
    {
        /// <summary>按 <see cref="UILayer"/> 顺序建立各层根容器（幂等）。<see cref="UIUtility"/> 首次打开窗口前调用。</summary>
        void Initialize();

        /// <summary>
        /// 加载窗口资源、实例化、绑定 <paramref name="context"/>，并挂到 <paramref name="meta"/> 指定层的栈顶。
        /// 返回窗口实例；资源加载失败返回 null（核心据此中止打开）。
        /// </summary>
        UniTask<IUIWindow> CreateWindow(UIWindowMeta meta, IGameContext context, CancellationToken ct);

        /// <summary>把窗口移到所属层内的最上方（重新聚焦已打开窗口时用）。</summary>
        void BringToFront(IUIWindow window);

        /// <summary>显示 / 隐藏窗口（缓存策略下"关闭=隐藏"、"再开=显示"）。</summary>
        void SetVisible(IUIWindow window, bool visible);

        /// <summary>在 <paramref name="ownerWindow"/> 之下铺 / 撤模态遮罩，拦截更下层输入。</summary>
        void SetModalMask(IUIWindow ownerWindow, bool on);

        /// <summary>销毁窗口及其加载的资源（UGUI 销毁 GameObject、UIToolkit Dispose 视图并摘出可视树）。</summary>
        void DestroyWindow(IUIWindow window);

        /// <summary>
        /// 全屏挡/放输入（盖在<b>所有</b>层之上的透明挡板）。核心在任一窗口过渡进行中开启（计数归零关闭），
        /// 防连点、防动画期间操作（ADR-0020）。实现应幂等（重复同值调用无害）。
        /// </summary>
        void SetInputBlocked(bool blocked);

        /// <summary>拆除全部层根与所有残留窗口。<see cref="UIUtility"/> 释放时调用。</summary>
        void Teardown();
    }
}
