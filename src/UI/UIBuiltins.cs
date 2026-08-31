using System;

namespace Game.Framework.UI
{
    /// <summary>
    /// Toast / Loading 内置窗口的类型表（ADR-0020 §4）：由各 adapter 的 Mono 入口在构造 <see cref="UIUtility"/>
    /// 核心时提供自家实现类型——业务经 <see cref="IUIUtility.ShowToast"/> 等调用时核心按此表开窗，
    /// 业务代码对后端零感知（与 <c>Open&lt;T&gt;</c> 同一条「渲染中立」铁律）。
    /// </summary>
    public sealed class UIBuiltinWindows
    {
        /// <summary>Toast 窗口类型（须实现 <see cref="IUIWindow"/>，OnOpen 收 <see cref="UIToastArgs"/>）。</summary>
        public Type Toast;

        /// <summary>Loading 窗口类型（须实现 <see cref="IUIWindow"/>，OnOpen 收 <see cref="UILoadingArgs"/>）。</summary>
        public Type Loading;
    }

    /// <summary>Toast 打开参数：adapter 渲染 <see cref="Text"/>；核心 <see cref="UIUtility"/> 消费 <see cref="Duration"/> 并统一持有自动关闭时序。</summary>
    public sealed class UIToastArgs
    {
        /// <summary>提示文本。</summary>
        public readonly string Text;
        /// <summary>自动关闭前的持续秒数。</summary>
        public readonly float Duration;

        /// <summary>创建 Toast 打开参数。</summary>
        /// <param name="text">提示文本。</param>
        /// <param name="duration">自动关闭前的持续秒数。</param>
        public UIToastArgs(string text, float duration)
        {
            Text = text;
            Duration = duration;
        }
    }

    /// <summary>Loading 打开参数：提示文本（可空 = 只显示指示动画）。</summary>
    public sealed class UILoadingArgs
    {
        /// <summary>提示文本；可为 <c>null</c>。</summary>
        public readonly string Text;

        /// <summary>创建 Loading 打开参数。</summary>
        /// <param name="text">提示文本；传 <c>null</c> 时只显示指示动画。</param>
        public UILoadingArgs(string text) => Text = text;
    }
}
