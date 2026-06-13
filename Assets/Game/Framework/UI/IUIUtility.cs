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
    /// <b>异步：</b><see cref="Open{T}(object, CancellationToken)"/> 内部走资源系统加载窗口资源，失败返回 null。
    /// </remarks>
    public interface IUIUtility : IUtility
    {
        /// <summary>打开窗口（无参）。已打开则置顶并返回该实例。资源加载失败返回 null。</summary>
        UniTask<T> Open<T>(CancellationToken ct = default) where T : class, IUIWindow;

        /// <summary>打开窗口并传入打开参数 <paramref name="args"/>（窗口在 <c>OnOpen</c> 里取用）。已打开则置顶并重新 <c>OnOpen</c>。</summary>
        UniTask<T> Open<T>(object args, CancellationToken ct = default) where T : class, IUIWindow;

        /// <summary>关闭指定类型窗口（未打开则忽略）。按其缓存策略隐藏或销毁。</summary>
        void Close<T>() where T : class, IUIWindow;

        /// <summary>关闭指定窗口实例。</summary>
        void Close(IUIWindow window);

        /// <summary>关闭某层最上方的窗口。</summary>
        void CloseTop(UILayer layer);

        /// <summary>返回导航：关闭 <see cref="UILayer.Page"/> 层最上方的页，露出上一页。</summary>
        void Back();

        /// <summary>关闭某一层的所有窗口。</summary>
        void CloseAll(UILayer layer);

        /// <summary>关闭所有层的所有窗口。</summary>
        void CloseAll();

        /// <summary>取已打开的窗口实例；未打开返回 null。</summary>
        T Get<T>() where T : class, IUIWindow;

        /// <summary>查询某类型窗口当前是否打开。</summary>
        bool IsOpen<T>() where T : class, IUIWindow;
    }
}
