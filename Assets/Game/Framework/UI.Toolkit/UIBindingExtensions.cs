using System;
using Game.Framework.Localization;
using R3;
using UnityEngine.UIElements;

namespace Game.Framework.UI.Toolkit
{
    /// <summary>
    /// R3 ↔ UI Toolkit 的桥接 helper：把响应式状态绑到 <see cref="VisualElement"/>，把 VisualElement 的交互接成订阅。
    /// 内部一律走 <see cref="DisposableBag.Subscribe{T}(Observable{T}, Action{T})"/>，与 UGUI 订阅
    /// <c>ReadOnlyReactiveProperty</c> 完全同一套心智——<b>刻意不引入 UI Toolkit 原生 DataBinding</b>，保持一套订阅模型。
    /// </summary>
    /// <remarks>
    /// 都是 <see cref="DisposableBag"/> 的扩展方法：在 <see cref="UIToolkitViewBase"/> 子类里 <c>Bag.BindText(...)</c> 即可，
    /// 订阅登记进 Bag，视图 <c>Dispose</c> 时统一退订。订阅状态流时 R3 会立刻推一次当前值（无需手动初始化）。
    /// </remarks>
    public static class UIBindingExtensions
    {
        /// <summary>把任意 <see cref="TextElement"/>（Label / Button 等）的 <c>text</c> 绑定到一个响应式值；<paramref name="format"/> 省略时用 <c>ToString()</c>。</summary>
        public static IDisposable BindText<T>(this DisposableBag bag, TextElement element, ReadOnlyReactiveProperty<T> source, Func<T, string> format = null)
            => bag.Subscribe(source, v => element.text = format != null ? format(v) : v?.ToString() ?? string.Empty);

        /// <summary>把元素的可交互状态（<see cref="VisualElement.SetEnabled(bool)"/>）绑定到一个响应式布尔值。</summary>
        public static IDisposable BindEnabled(this DisposableBag bag, VisualElement element, ReadOnlyReactiveProperty<bool> source)
            => bag.Subscribe(source, element.SetEnabled);

        /// <summary>把元素的显示/隐藏（<c>style.display</c> = Flex/None，隐藏不占布局）绑定到一个响应式布尔值。</summary>
        public static IDisposable BindVisible(this DisposableBag bag, VisualElement element, ReadOnlyReactiveProperty<bool> source)
            => bag.Subscribe(source, visible => element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None);

        /// <summary>通用：把任意 <see cref="Observable{T}"/> 投射到元素上（自定义 onNext）。复杂绑定（Where / CombineLatest 等）先用 R3 操作符组合再传入。</summary>
        public static IDisposable Bind<T>(this DisposableBag bag, Observable<T> source, Action<T> apply)
            => bag.Subscribe(source, apply);

        /// <summary>
        /// 订阅 <see cref="Button"/> 的点击（UI Toolkit 的 <c>Button.clicked</c> 是 C# <see cref="Action"/> 事件）。
        /// 用 Bag 的"订阅 + 反订阅"通道托管，视图 Dispose 时自动解绑。
        /// </summary>
        public static IDisposable SubscribeClick(this DisposableBag bag, Button button, Action handler)
            => bag.Subscribe(() => button.clicked += handler, () => button.clicked -= handler);

        /// <summary>
        /// 在任意 <see cref="VisualElement"/> 上注册点击回调（<see cref="ClickEvent"/>），由 Bag 托管自动反注册。
        /// 适合给非 Button 元素（图标、卡片等）接点击。
        /// </summary>
        public static IDisposable OnClick(this DisposableBag bag, VisualElement element, EventCallback<ClickEvent> handler)
            => bag.Subscribe(() => element.RegisterCallback(handler), () => element.UnregisterCallback(handler));

        /// <summary>
        /// 把文本绑定到本地化 key：立即取当前语言文本，换语言（<c>ILocalizationUtility.Locale</c> 推送）自动刷新。
        /// 经 bag 的 Context 解析 <c>ILocalizationUtility</c>（与 <c>Bag.Load</c> 同心智），bag 需以 ctx 构造。
        /// </summary>
        public static IDisposable BindLocalizedText(this DisposableBag bag, TextElement element, string key)
        {
            var loc = ResolveLocalization(bag);
            return bag.Subscribe(loc.Locale, _ => element.text = loc.Get(key));
        }

        /// <summary>
        /// 带静态格式化参数的本地化绑定（参数在绑定时固定）。参数本身要动态变化的场景不用本方法——
        /// 业务用 <c>bag.Subscribe</c> / R3 <c>CombineLatest</c> 把「语言 × 数据」双源组合后自行赋 <c>text</c>。
        /// </summary>
        public static IDisposable BindLocalizedText(this DisposableBag bag, TextElement element, string key, params object[] args)
        {
            var loc = ResolveLocalization(bag);
            return bag.Subscribe(loc.Locale, _ => element.text = loc.Get(key, args));
        }

        private static ILocalizationUtility ResolveLocalization(DisposableBag bag)
        {
            if (bag.Context == null)
                throw new InvalidOperationException(
                    "[UIBinding] BindLocalizedText 需要 bag 关联 Context（用 new DisposableBag(ctx) / ctx.CreateBag() 创建）才能解析 ILocalizationUtility。");
            return bag.Context.GetUtility<ILocalizationUtility>();
        }
    }
}
