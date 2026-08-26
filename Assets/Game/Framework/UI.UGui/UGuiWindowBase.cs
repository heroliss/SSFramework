using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
using Game.Framework.View;
using UnityEngine;

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
        // 建后第一拍先 BindNodes（生成的 partial 覆写它取节点字段），再 OnCreated——OnCreated 里字段已就绪。
        void IUIWindow.OnCreate() { BindNodes(); OnCreated(); }
        void IUIWindow.OnOpen(object args) => OnOpen(args);
        void IUIWindow.OnClose() => OnClose();
        void IUIWindow.OnCover() => OnCover();
        void IUIWindow.OnReveal() => OnReveal();
        UniTask IUIWindow.OnOpenTransition(CancellationToken ct) => OnOpenTransition(ct);
        UniTask IUIWindow.OnCloseTransition(CancellationToken ct) => OnCloseTransition(ct);

        /// <summary>
        /// 由生成的 partial（<c>*.nodes.g.cs</c>）覆写：把窗口 prefab 子节点上的组件取到字段。
        /// 框架在 <see cref="OnCreated"/> 之前调用，故 <c>OnCreated</c> 里这些字段已可用。
        /// 覆写实现须先 <c>base.BindNodes()</c>（变体继承时先绑父类字段）。手写窗口不需要覆写。
        /// </summary>
        protected virtual void BindNodes() { }

        /// <summary>
        /// 生成的 <see cref="BindNodes"/> 取节点用的助手：按相对窗口根的路径取子节点上的组件（空路径 = 窗口根自身）。
        /// 路径找不到节点、或节点上没有该组件时打 error 并返回 null——fail-fast，提示 prefab 结构与生成代码已不一致（需重新生成）。
        /// </summary>
        protected T BindNode<T>(string path) where T : Component
        {
            var target = string.IsNullOrEmpty(path) ? transform : transform.Find(path);
            if (target == null)
            {
                Log.Error($"绑定失败：找不到节点路径 \"{path}\"（prefab 结构与生成的绑定代码不一致，请重新生成）。",
                    category: GetType().Name, context: this);
                return null;
            }
            var component = target.GetComponent<T>();
            if (component == null)
                Log.Error($"绑定失败：节点 \"{path}\" 上没有组件 {typeof(T).Name}。",
                    category: GetType().Name, context: this);
            return component;
        }

        /// <summary>
        /// 生成的 <see cref="BindNodes"/> 取节点用的助手：按相对窗口根的路径取子节点的 <see cref="GameObject"/> 本身（空路径 = 窗口根自身）。
        /// 用于绑定整个节点（<c>SetActive</c> / 销毁 / 换父等）——<see cref="BindNode{T}"/> 受 <c>where T : Component</c> 约束接不了 GameObject，故单列一个。
        /// 路径找不到节点时打 error 并返回 null（fail-fast，提示 prefab 结构与生成代码已不一致，需重新生成）。
        /// </summary>
        protected GameObject BindGameObject(string path)
        {
            var target = string.IsNullOrEmpty(path) ? transform : transform.Find(path);
            if (target == null)
            {
                Log.Error($"绑定失败：找不到节点路径 \"{path}\"（prefab 结构与生成的绑定代码不一致，请重新生成）。",
                    category: GetType().Name, context: this);
                return null;
            }
            return target.gameObject;
        }

        /// <summary>窗口建好、Context 已注入后调用一次——接线（订阅查询 Command、接按钮）。此时各层就绪且节点字段已绑好，可直接 <c>this.ExecuteCommand(...)</c>。</summary>
        protected virtual void OnCreated() { }

        /// <summary>每次打开（显示）调用，<paramref name="args"/> 为打开参数（可空）。</summary>
        protected virtual void OnOpen(object args) { }

        /// <summary>每次关闭调用——停动画、提交临时态等。之后窗口被隐藏（缓存）或销毁。</summary>
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
