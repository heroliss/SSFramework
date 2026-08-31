using System;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.View;
using UnityEngine.UIElements;

namespace Game.Framework.UI.Toolkit
{
    /// <summary>
    /// UI Toolkit 视图的纯 C# 基类——让 <see cref="VisualElement"/> 视图与 UGUI 的 <see cref="MonoViewBase"/>
    /// 享有完全一致的框架接入：自动注入、<see cref="Bag"/> 生命周期、<c>this.ExecuteCommand</c> /
    /// <c>this.RegisterEvent</c> / <c>this.GetUtility</c> 扩展方法（由 <see cref="IView"/> 权限接口约束）。
    /// </summary>
    /// <remarks>
    /// <b>谁该用：</b>用 UI Toolkit（VisualElement / UXML）实现的窗口、面板、HUD 等可见可交互视图。<br/>
    /// <b>为什么是纯 C#：</b>UI Toolkit 视图不是 GameObject，没有 Transform 父链，无法像 <see cref="MonoViewBase"/>
    /// 那样在 Awake 沿父链自动找 Context——所以由创建方<b>显式</b>把 <see cref="IGameContext"/> 交给它
    /// （<see cref="BindTo"/>）。标准创建方是 UI 框架的 <c>IUIUtility</c>（开窗口时自动绑定）；独立使用时由持有
    /// Context 的引导代码调用 <see cref="BindTo"/>。<br/>
    /// <b>边界（与 <see cref="IView"/> 对齐）：</b>子类拿不到完整 <see cref="IGameContext"/>（显式接口实现），
    /// 只能 ExecuteCommand / RegisterEvent / GetUtility——不能 GetModel / GetSystem / SendEvent。<br/>
    /// <b>生命周期：</b>Context 是借用的作用域能力，不拥有此 View；创建方仍须在自己的生命周期结束时调用
    /// <see cref="Dispose"/>。每个视图一个 <see cref="Bag"/>，释放时统一清理订阅与资源句柄，并把
    /// <see cref="Root"/> 从可视树摘除；Context 取消本身不会代替这次物理清理。
    /// </remarks>
    public abstract class UIToolkitViewBase : IView, IHasGameContext, IDisposable
    {
        private IGameContext _context;
        private DisposableBag _bag;
        private bool _disposed;

        // 显式接口实现：业务子类无法通过 this.Context 拿到完整 IGameContext，只能用受权限约束的扩展方法。
        IGameContext IHasGameContext.Context => _context;

        /// <summary>视图根元素。<see cref="BindTo"/> 后可用；子类在 <see cref="OnCreated"/> 里往这里搭 UI 或查询 UXML 子元素。</summary>
        public VisualElement Root { get; private set; }

        /// <summary>是否已绑定到 Context（<see cref="BindTo"/> 调用过）。</summary>
        public bool IsBound => _context != null;

        /// <summary>视图是否已释放。</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// 视图生命周期容器——订阅（Observable / Framework Event / VisualElement 回调）、资源加载、任意
        /// <see cref="IDisposable"/> 统一登记，<see cref="Dispose"/> 时自动批量清理。绑定 Context 后才可用。
        /// </summary>
        protected DisposableBag Bag => _bag ??= new DisposableBag(_context);

        /// <summary>
        /// 把视图绑定到 <paramref name="context"/> 并完成接线：注入 <c>[Inject]</c> 字段、准备
        /// <see cref="Root"/>、调用一次 <see cref="OnCreated"/>。返回 <see cref="Root"/> 便于创建方直接挂进可视树。
        /// </summary>
        /// <param name="context">视图所属上下文。命令解析、资源加载、事件都走它。</param>
        /// <param name="root">
        /// 视图根。传入已构建的可视树（如 UXML clone 的结果）；留空则新建一个空 <see cref="VisualElement"/>，
        /// 由子类在 <see cref="OnCreated"/> 里往里搭 UI。
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">同一实例重复绑定，或 <see cref="OnCreated"/> 期间自行释放。</exception>
        /// <exception cref="ObjectDisposedException">视图已经释放。</exception>
        /// <remarks>
        /// 绑定是一次事务提交：若字段注入或 <see cref="OnCreated"/> 失败，或创建期间自行释放，本方法会先尽力释放
        /// 已经登记的 <see cref="Bag"/> 内容并摘除 <see cref="Root"/>，再保留原始异常抛出。失败实例已经释放，不可复用。
        /// </remarks>
        public VisualElement BindTo(IGameContext context, VisualElement root = null)
        {
            EnsureCanBind(context);
            try
            {
                BindContextCore(context, root);
                OnCreated();
                if (_disposed)
                    throw new InvalidOperationException(
                        $"[{GetType().Name}] 在 OnCreated 期间释放了自己，BindTo 无法返回可用 Root；" +
                        "需要按初始状态决定是否显示时，请由创建方在绑定前判断，或在绑定成功后再关闭。");
                return Root;
            }
            catch
            {
                try
                {
                    Dispose();
                }
                catch (Exception cleanupException)
                {
                    // 创建异常是根因；清理 hook 的次生异常进入统一日志，不能反过来覆盖根因。
                    Log.Error(
                        $"[{GetType().Name}] BindTo 失败后的回滚清理又抛出异常；" +
                        "视图 Bag 与可视树已继续清理，当前仍抛出最初的绑定异常。",
                        cleanupException,
                        nameof(UIToolkitViewBase));
                }
                throw;
            }
        }

        /// <summary>
        /// 只绑定 Context + 准备 <see cref="Root"/> + 注入字段，<b>不</b>调 <see cref="OnCreated"/>。
        /// 供 UI 框架的窗口路径用——窗口的 <see cref="OnCreated"/> 由框架的 <c>IUIWindow.OnCreate</c> 生命周期 hook 触发，
        /// 避免与 <see cref="BindTo"/> 重复调用。独立视图走 <see cref="BindTo"/>（绑定即建 UI）。
        /// </summary>
        internal void BindContextInternal(IGameContext context, VisualElement root)
        {
            EnsureCanBind(context);
            BindContextCore(context, root);
        }

        private void EnsureCanBind(IGameContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (_disposed) throw new ObjectDisposedException(
                GetType().Name,
                $"[{GetType().Name}] 已释放，不能再绑定 Context；请创建新的 View 实例。");
            if (_context != null) throw new InvalidOperationException(
                $"[{GetType().Name}] 已绑定 Context，不能重复绑定；每个 UI Toolkit View 实例只能绑定一次。");
        }

        private void BindContextCore(IGameContext context, VisualElement root)
        {
            _context = context;
            Root = root ?? new VisualElement();
            context.Inject(this); // [Inject] 受层权限校验：View 注 Model/System 会被拦，注普通服务可以。
        }

        /// <summary>供框架窗口基类在统一生命周期中触发一次性建 UI 与接线。</summary>
        private protected void InvokeCreated() => OnCreated();

        /// <summary>
        /// 视图已绑定 Context、<see cref="Root"/> 就绪后调用一次——子类在这里搭 UI（往 <see cref="Root"/> 加元素，
        /// 或 <c>Root.Q&lt;T&gt;(...)</c> 查询 UXML 子元素）、订阅查询 Command、接按钮事件。
        /// 此时各层已就绪，可直接 <c>this.ExecuteCommand(...)</c>。
        /// </summary>
        protected virtual void OnCreated() { }

        /// <summary>
        /// 释放视图：先跑 <see cref="OnDisposing"/>，再释放 <see cref="Bag"/>（退订 + 释放资源），最后把
        /// <see cref="Root"/> 摘出可视树。幂等；<see cref="OnDisposing"/> 即使抛异常也不会截断后两步，异常在完成清理后原样传播。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                OnDisposing();
            }
            finally
            {
                try
                {
                    _bag?.Dispose();
                }
                finally
                {
                    _bag = null;
                    Root?.RemoveFromHierarchy();
                }
            }
        }

        /// <summary>释放前的子类钩子（在 <see cref="Bag"/> 释放之前调用）。一般无需重写——订阅都进 Bag 自动清理。
        /// <b>注意：</b>独立 View 由创建 owner 显式释放；正式 Window 由 UI Backend 在宿主拆除路径中释放。
        /// 两条路径都可能发生在所属 Context 正在销毁时，因此不要在此 <c>ExecuteCommand</c> / <c>GetUtility</c> / 访问 Context。</summary>
        protected virtual void OnDisposing() { }
    }
}
