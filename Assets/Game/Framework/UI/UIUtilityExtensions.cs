using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.UI
{
    /// <summary>
    /// 为 <see cref="IUIUtility"/> 补充带业务失败语义的开窗入口；不改变 <c>Open&lt;T&gt;()</c>
    /// “可选窗口允许返回 null”的兼容契约。
    /// </summary>
    public static class UIUtilityExtensions
    {
        /// <summary>
        /// 打开不可缺席的窗口。适合 Flow 主页面、启动门禁等只有窗口成功建立后才能提交状态的路径。
        /// </summary>
        /// <remarks>
        /// <see cref="IUIUtility.Open{T}(CancellationToken)"/> 未获得窗口实例时抛出包含窗口类型与资源位置的
        /// <see cref="InvalidOperationException"/>；调用方取消仍保持 <see cref="OperationCanceledException"/>。
        /// 允许窗口缺席、准备就地降级的路径继续使用 <c>Open&lt;T&gt;()</c> 并显式处理 null。
        /// 本方法只收紧 null 失败策略；窗口生命周期 hook 仍沿用 <see cref="UIUtility"/> 的异常隔离契约，
        /// 不把一次开窗改成事务提交。
        /// </remarks>
        public static async UniTask<T> OpenRequired<T>(
            this IUIUtility ui,
            CancellationToken ct = default)
            where T : class, IUIWindow
        {
            if (ui == null) throw new ArgumentNullException(nameof(ui));
            var window = await ui.Open<T>(ct);
            return RequireOpened(window, ct);
        }

        /// <summary>
        /// 带 <paramref name="args"/> 打开不可缺席的窗口；窗口仍在 <c>OnOpen(args)</c> 中读取参数。
        /// </summary>
        /// <remarks>
        /// 失败与取消语义同 <see cref="OpenRequired{T}(IUIUtility, CancellationToken)"/>。
        /// </remarks>
        public static async UniTask<T> OpenRequired<T>(
            this IUIUtility ui,
            object args,
            CancellationToken ct = default)
            where T : class, IUIWindow
        {
            if (ui == null) throw new ArgumentNullException(nameof(ui));
            var window = await ui.Open<T>(args, ct);
            return RequireOpened(window, ct);
        }

        private static T RequireOpened<T>(T window, CancellationToken ct)
            where T : class, IUIWindow
        {
            if (window != null) return window;

            // Open 的物理失败与调用方取消可能在同一帧竞速；业务显式取消应继续保持 OCE，而不是被改写成开窗失败。
            ct.ThrowIfCancellationRequested();

            Type windowType = typeof(T);
            UIWindowMeta meta = UIWindowMeta.Of(windowType);
            string source = string.IsNullOrWhiteSpace(meta.Asset)
                ? "代码构建（UIWindow.Asset 为空）"
                : $"资源 location='{meta.Asset}'";
            throw new InvalidOperationException(
                $"必需窗口 '{windowType.FullName ?? windowType.Name}' 打开失败：IUIUtility.Open<T>() 未获得窗口实例（{source}）。" +
                "可能原因是 UI Adapter 创建失败，或 UI 生命周期在创建期间结束；请检查相关日志、窗口配置与资源初始化。" +
                "若该窗口允许缺席，请使用 Open<T>() 并显式处理 null。");
        }
    }
}
