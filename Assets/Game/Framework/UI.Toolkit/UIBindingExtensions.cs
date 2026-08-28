using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Localization;
using Game.Framework.Logging;
using ObservableCollections;
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
        /// 订阅异步点击：每次点击把一个与本订阅生命周期绑定的 <see cref="CancellationToken"/> 交给
        /// <paramref name="handler"/>；Bag 或返回的订阅释放时取消所有在途处理，并自动解绑后续点击。
        /// 生命周期取消静默收口，未处理的其它异常进入统一 <see cref="Log"/> Seam，不会变成无人观察的 UniTask。
        /// </summary>
        /// <remarks>
        /// 本方法只负责生命周期与异常所有权，<b>不</b>擅自禁用按钮、去抖或单飞；是否允许并发点击是业务交互策略，
        /// 需要单飞时由调用方在 handler 内禁用按钮或使用自己的 gate。可预期且需要界面反馈的失败也应由 handler
        /// 就近捕获，本方法的日志是未预期失败的最后防线。<br/>
        /// 若物理操作必须在 View 关闭后继续到终态（如已启动的包下载），handler 可以刻意不把 token 传给 owner；
        /// 绑定仍会持续观察任务，直到它自行完成。
        /// </remarks>
        public static IDisposable SubscribeClickAsync(
            this DisposableBag bag,
            Button button,
            Func<CancellationToken, UniTask> handler)
        {
            if (bag == null) throw new ArgumentNullException(nameof(bag));
            if (button == null) throw new ArgumentNullException(nameof(button));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (bag.IsDisposed) return Disposable.Empty;

            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(bag.DisposeToken);
            Action onClick = () =>
            {
                string identity = !string.IsNullOrWhiteSpace(button.name)
                    ? button.name
                    : !string.IsNullOrWhiteSpace(button.text) ? button.text : nameof(Button);
                ObserveClick(handler, lifetime.Token, identity).Forget();
            };

            button.clicked += onClick;
            return bag.Add(Disposable.Create(() =>
            {
                button.clicked -= onClick;
                try { lifetime.Cancel(); }
                catch (Exception e)
                {
                    Log.Error(
                        "异步点击绑定的取消回调执行失败；绑定仍会继续释放。",
                        e,
                        "UIBinding");
                }
                finally { lifetime.Dispose(); }
            }));
        }

        private static async UniTask ObserveClick(
            Func<CancellationToken, UniTask> handler,
            CancellationToken cancellationToken,
            string buttonIdentity)
        {
            try { await handler(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception e)
            {
                Log.Error(
                    $"Button '{buttonIdentity}' 的异步点击处理器执行失败。",
                    e,
                    "UIBinding");
            }
        }

        /// <summary>
        /// 在任意 <see cref="VisualElement"/> 上注册点击回调（<see cref="ClickEvent"/>），由 Bag 托管自动反注册。
        /// 适合给非 Button 元素（图标、卡片等）接点击。
        /// </summary>
        public static IDisposable OnClick(this DisposableBag bag, VisualElement element, EventCallback<ClickEvent> handler)
            => bag.Subscribe(() => element.RegisterCallback(handler), () => element.UnregisterCallback(handler));

        /// <summary>
        /// 把文本绑定到本地化 key：立即查询；换语言或延迟文本源就绪（<c>ILocalizationUtility.TextRevision</c> 推送）自动刷新。
        /// 经 bag 的 Context 解析 <c>ILocalizationUtility</c>（与 <c>Bag.Load</c> 同心智），bag 需以 ctx 构造。
        /// </summary>
        public static IDisposable BindLocalizedText(this DisposableBag bag, TextElement element, string key)
        {
            var loc = ResolveLocalization(bag);
            return bag.Subscribe(loc.TextRevision, _ => element.text = loc.Get(key));
        }

        /// <summary>
        /// 带静态格式化参数的本地化绑定（参数在绑定时固定）。参数本身要动态变化的场景不用本方法——
        /// 业务用 <c>bag.Subscribe</c> / R3 <c>CombineLatest</c> 把「语言 × 数据」双源组合后自行赋 <c>text</c>。
        /// </summary>
        public static IDisposable BindLocalizedText(this DisposableBag bag, TextElement element, string key, params object[] args)
        {
            var loc = ResolveLocalization(bag);
            return bag.Subscribe(loc.TextRevision, _ => element.text = loc.Get(key, args));
        }

        /// <summary>
        /// 把一个响应式集合（<see cref="IReadOnlyObservableList{T}"/>）<b>增量</b>绑定到 <paramref name="container"/>：
        /// 为每个元素造一个子 <see cref="VisualElement"/>，集合增删移换时只动对应子元素，不整表重建
        /// （保住滚动 / 选中 / 焦点，不抖 GC）。补 <c>BindText</c> 等单值绑定覆盖不到的列表空缺。
        /// </summary>
        /// <param name="container">承载子元素的容器（如一个竖排 <see cref="VisualElement"/>）。子元素按源集合顺序排列。</param>
        /// <param name="source">响应式源集合（一般是 Model 持有、经只读查询 Command 以 <see cref="IReadOnlyObservableList{T}"/> 暴露）。</param>
        /// <param name="itemFactory">
        /// 为一个元素造子视图；第二参是<b>该行专属</b>的子 <see cref="DisposableBag"/>——行内订阅（如「这一行随 RP 刷新」）挂它，
        /// 该行离开列表时自动退订。无行内订阅就忽略它。行内可照常 <c>rowBag.BindText(...)</c> / <c>rowBag.SubscribeClick(...)</c>；
        /// 不得返回 <c>null</c>，也不要在工厂内同步修改同一个 <paramref name="source"/>。
        /// </param>
        /// <remarks>
        /// 目标是项数适中的 UI 列表（背包 / 聊天 / 设置项）。上万项要虚拟化滚动复用用 UI Toolkit 原生 <c>ListView</c>（见 guide §24）。
        /// 初始化失败会回滚已建行；增量回调失败会终止本次绑定并清理所有行，避免后续索引在半损坏容器上继续漂移。
        /// </remarks>
        public static IDisposable BindList<T>(
            this DisposableBag bag, VisualElement container,
            IReadOnlyObservableList<T> source,
            Func<T, DisposableBag, VisualElement> itemFactory)
        {
            if (container == null)
                throw new ArgumentNullException(nameof(container), "UI Toolkit BindList 需要一个有效的 VisualElement 容器。");
            if (itemFactory == null) throw new ArgumentNullException(nameof(itemFactory));

            return ReactiveListBinding.Bind(
                bag,
                source,
                createItem: (value, rowBag) => itemFactory(value, rowBag)
                    ?? throw new InvalidOperationException(
                        "UI Toolkit BindList 的 itemFactory 返回了 null；请返回一个有效的 VisualElement。"),
                attach: (index, view) => container.Insert(index, view),
                detach: view => view.RemoveFromHierarchy(),
                // Toolkit 无「移到某兄弟位」直接 API：摘下再插回目标位（其余子元素已在正确相对序）。
                reorder: (index, view) => { view.RemoveFromHierarchy(); container.Insert(index, view); });
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
