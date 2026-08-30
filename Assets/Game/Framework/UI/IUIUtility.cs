using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Utility;

namespace Game.Framework.UI
{
    /// <summary>
    /// UI 框架主入口（Utility 层，镜像 <c>IAssetUtility</c> / <c>IPoolUtility</c>）。打开/关闭窗口、查询、按层批量关闭、返回导航。
    /// </summary>
    /// <remarks>
    /// <b>谁能用：</b>View（有 <c>ICanGetUtility</c>，<c>this.GetUtility&lt;IUIUtility&gt;().Open&lt;T&gt;()</c>，
    /// 心智同 <c>Bag.Load</c>）、System、Command（经 <c>ctx</c>）。需要被 CommandSystem 拦截（日志/回放）的业务语义流程可另包 Command。<br/>
    /// <b>窗口元数据</b>由窗口类上的 <see cref="UIWindowAttribute"/> 提供（层 / 资源 / 缓存 / 模态）。<br/>
    /// <b>渲染无关：</b>同一套 API 在 UGUI 与 UI Toolkit 后端下行为一致，由 <see cref="IUIBackend"/> 吸收差异。<br/>
    /// <b>所有权：</b><see cref="Open{T}(CancellationToken)"/> / <see cref="Get{T}"/> 返回的是借用窗口引用；
    /// 窗口的物理对象、资源句柄、缓存与销毁均由本 Utility 和后端持有。调用方不得自行 Destroy / Dispose，
    /// 只通过 Close / CloseAll 表达关闭意图。<br/>
    /// <b>异步：</b><see cref="Open{T}(object, CancellationToken)"/> 内部走资源系统加载窗口资源；Adapter 未能创建，
    /// 或 UI 生命周期在创建期间结束而未获得实例时返回 null；
    /// Flow 主页面等不可缺席的路径使用 <see cref="UIUtilityExtensions.OpenRequired{T}(IUIUtility, CancellationToken)"/>，
    /// 让失败阻止上层状态提交。调用方取消保持 <see cref="OperationCanceledException"/>；非预期的后端异常原样传播，
    /// 窗口生命周期 hook 的异常则由核心记录并隔离，避免单个表现回调破坏整个窗口栈。
    /// </remarks>
    public interface IUIUtility : IUtility
    {
        /// <summary>
        /// 打开窗口（无参）。已打开则置顶并返回该实例；未获得窗口实例时返回 null（如 Adapter 创建失败，
        /// 或 UI 生命周期在创建期间结束）。
        /// 不可缺席的窗口使用 <see cref="UIUtilityExtensions.OpenRequired{T}(IUIUtility, CancellationToken)"/>。
        /// </summary>
        UniTask<T> Open<T>(CancellationToken ct = default) where T : class, IUIWindow;

        /// <summary>
        /// 打开窗口并传入打开参数 <paramref name="args"/>（窗口在 <c>OnOpen</c> 里取用）。已打开则置顶并重新
        /// <c>OnOpen</c>；未获得窗口实例时返回 null。不可缺席的窗口使用
        /// <see cref="UIUtilityExtensions.OpenRequired{T}(IUIUtility, object, CancellationToken)"/>。
        /// </summary>
        UniTask<T> Open<T>(object args, CancellationToken ct = default) where T : class, IUIWindow;

        /// <summary>关闭指定类型窗口（未打开则忽略）。按其缓存策略隐藏或销毁。</summary>
        void Close<T>() where T : class, IUIWindow;

        /// <summary>关闭指定窗口实例。</summary>
        void Close(IUIWindow window);

        /// <summary>关闭某层最上方的窗口。</summary>
        void CloseTop(UILayer layer);

        /// <summary>
        /// 返回导航（Android Back / Esc 的目标语义，ADR-0020）：按 Popup → Window → Page 从高到低找第一个非空层，
        /// 关闭其栈顶窗口。返回 true = 返回键已被 UI 消费（关了窗、或栈顶 <c>BackClosable=false</c> 拦截、或过渡动画进行中）；
        /// false = 三层皆空，业务可做「再按一次退出」之类的兜底。物理按键 / Input Action 到本方法的映射由项目输入层负责，
        /// 避免渲染中立的 UI Module 反向依赖某个输入 Package。
        /// </summary>
        bool Back();

        /// <summary>关闭某一层的所有窗口。</summary>
        void CloseAll(UILayer layer);

        /// <summary>关闭所有层的所有窗口。</summary>
        void CloseAll();

        /// <summary>取已打开的窗口实例；未打开返回 null。</summary>
        T Get<T>() where T : class, IUIWindow;

        /// <summary>查询某类型窗口当前是否打开。</summary>
        bool IsOpen<T>() where T : class, IUIWindow;

        /// <summary>
        /// 弹 Toast（Top 层内置件，ADR-0020 §4）：短暂显示 <paramref name="text"/> 后自动关闭，不拦截输入。
        /// 连续调用复用同一窗口（刷新文本、重置计时）。返回的 task 在窗口打开后完成（不含显示时长）；
        /// <paramref name="ct"/> 只管打开过程，调用方生命周期结束时不会留下延迟出现的窗口。
        /// 自动关闭 owner 由渲染中立核心统一持有；显式 Close / CloseAll / Dispose 会让旧计时与创建请求失效。
        /// </summary>
        UniTask ShowToast(string text, float duration = 2f, CancellationToken ct = default);

        /// <summary>
        /// 占用全局 Loading（Top 层内置件，模态挡输入）：<paramref name="text"/> 可空（只显示指示动画）。
        /// 返回的 <see cref="LoadingHandle"/> 代表本次所有权；多个调用方重叠时，释放最后一个有效句柄才真正关闭窗口。
        /// 重复占用复用同一窗口并刷新文本；<paramref name="ct"/> 只管异步打开过程，业务任务结束由调用方释放句柄。
        /// 推荐写法：<c>using var loading = await ui.AcquireLoading(...)</c>。
        /// </summary>
        UniTask<LoadingHandle> AcquireLoading(string text = null, CancellationToken ct = default);

        /// <summary>
        /// 以兼容的单 owner 语义显示全局 Loading。重复调用刷新文本；<paramref name="ct"/> 只管打开过程。
        /// 仅供旧源码迁移；新代码使用 <see cref="AcquireLoading"/>，避免多个异步流程的
        /// <see cref="HideLoading"/> 互相误关。该成员会在未来破坏性版本删除。
        /// </summary>
        [Obsolete("ShowLoading/HideLoading 仅用于旧源码迁移；请改用 using var loading = await AcquireLoading(text, ct)，由句柄表达并发所有权。", false)]
        UniTask ShowLoading(string text = null, CancellationToken ct = default);

        /// <summary>
        /// 释放由 <see cref="ShowLoading"/> 建立的兼容单 owner；未显示则忽略。
        /// 有效的 <see cref="AcquireLoading"/> 句柄仍存在时不会关闭窗口。仅供旧源码迁移，
        /// 该成员会在未来破坏性版本删除。
        /// </summary>
        [Obsolete("ShowLoading/HideLoading 仅用于旧源码迁移；请释放 AcquireLoading 返回的 LoadingHandle，通常使用 using var 自动释放。", false)]
        void HideLoading();
    }
}
