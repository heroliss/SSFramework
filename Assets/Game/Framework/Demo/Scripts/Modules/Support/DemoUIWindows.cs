using System;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 「UI 框架 · 窗口/层级」章用的几个代码搭建（无 prefab/UXML）UI Toolkit 窗口。
    /// 每个都标 <see cref="UIWindowAttribute"/> 声明层 / 缓存 / 模态，由 <c>IUIUtility</c> 调度。
    /// 窗口需<b>无参构造</b>（框架用 Activator 实例化）；接线放 <c>OnCreated</c>、取参数放 <c>OnOpen</c>。
    /// </summary>
    internal static class DemoWindowKit
    {
        // 后端标识色：UI Toolkit 用蓝，与 UGUI 章的绿（见 DemoUGuiWindows.UGuiKit）成对，肉眼区分两套渲染。
        private static readonly Color ToolkitBlue = new(0.20f, 0.45f, 0.78f);

        // 后端标识药丸：贴在窗口标题上方，文字 + 配色双重提示「这是哪套 UI 技术搭的」。
        public static void Badge(VisualElement parent, string backend, Color bg)
        {
            var badge = new Label(backend) { enableRichText = false };
            badge.style.alignSelf = Align.FlexStart;
            badge.style.fontSize = 10;
            badge.style.color = Color.white;
            badge.style.backgroundColor = bg;
            badge.style.paddingLeft = 6; badge.style.paddingRight = 6;
            badge.style.paddingTop = 1; badge.style.paddingBottom = 1;
            badge.style.marginBottom = 8;
            badge.style.borderTopLeftRadius = 6; badge.style.borderTopRightRadius = 6;
            badge.style.borderBottomLeftRadius = 6; badge.style.borderBottomRightRadius = 6;
            parent.Add(badge);
        }

        // 居中卡片：root 透传点击（窗口外可点到下层/遮罩），卡片本身拦截。
        public static VisualElement Card(VisualElement root, string title, Color accent)
        {
            var overlay = new VisualElement();
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0; overlay.style.top = 0; overlay.style.right = 0; overlay.style.bottom = 0;
            overlay.style.justifyContent = Justify.Center;
            overlay.style.alignItems = Align.Center;
            overlay.pickingMode = PickingMode.Ignore;
            root.Add(overlay);

            var card = new VisualElement();
            card.style.minWidth = 320;
            card.style.paddingTop = 16; card.style.paddingBottom = 16; card.style.paddingLeft = 20; card.style.paddingRight = 20;
            card.style.backgroundColor = new Color(0.16f, 0.18f, 0.22f, 0.98f);
            SetBorder(card, accent, 2);
            Round(card, 10);
            overlay.Add(card);

            // 后端标识药丸：一眼看出这是 UI Toolkit（蓝）搭的窗口，与 UGUI（绿）章对照。
            Badge(card, "UI Toolkit", ToolkitBlue);

            var head = new Label(title) { enableRichText = false };
            head.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.style.fontSize = 16;
            head.style.color = accent;
            head.style.marginBottom = 10;
            card.Add(head);
            return card;
        }

        // 全屏页：不透传（盖住下层），自带标题。
        public static VisualElement FullPage(VisualElement root, string title, Color bg)
        {
            var page = new VisualElement();
            page.style.position = Position.Absolute;
            page.style.left = 0; page.style.top = 0; page.style.right = 0; page.style.bottom = 0;
            page.style.backgroundColor = bg;
            page.style.justifyContent = Justify.Center;
            page.style.alignItems = Align.Center;
            root.Add(page);

            Badge(page, "UI Toolkit", ToolkitBlue);

            var head = new Label(title) { enableRichText = false };
            head.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.style.fontSize = 22;
            head.style.color = Color.white;
            head.style.marginBottom = 16;
            page.Add(head);
            return page;
        }

        public static Button Btn(VisualElement parent, string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.marginTop = 6;
            b.style.minWidth = 160;
            b.style.height = 30;
            parent.Add(b);
            return b;
        }

        public static Label Lbl(VisualElement parent, string text)
        {
            var l = new Label(text);
            l.style.color = new Color(0.85f, 0.88f, 0.95f);
            l.style.marginBottom = 6;
            parent.Add(l);
            return l;
        }

        private static void SetBorder(VisualElement e, Color c, float w)
        {
            e.style.borderTopWidth = w; e.style.borderBottomWidth = w; e.style.borderLeftWidth = w; e.style.borderRightWidth = w;
            e.style.borderTopColor = c; e.style.borderBottomColor = c; e.style.borderLeftColor = c; e.style.borderRightColor = c;
        }

        private static void Round(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r; e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }
    }

    /// <summary>浮动窗口（Window 层）：复用「Model」章的 <c>MonoScoreModel</c>——演示窗口自动注入、Bag、读写分离、开关。</summary>
    [UIWindow(Layer = UILayer.Window)]
    public sealed class DemoCounterWindow : UIToolkitWindowBase
    {
        private static readonly Color Accent = new(0.45f, 0.75f, 1f);

        protected override void OnCreated()
        {
            var card = DemoWindowKit.Card(Root, "计数窗口 · Window 层", Accent);
            var score = DemoWindowKit.Lbl(card, "");
            // 只读订阅查询 Command（与 UGUI / UIToolkit View 章共用同一份 MonoScoreModel）。
            Bag.BindText(score, this.ExecuteCommand(new GetMonoScoreCommand()), v => $"Score: {v}");
            DemoWindowKit.Btn(card, "+1（ExecuteCommand 写）", () => this.ExecuteCommand(new RaiseMonoScoreCommand()));
            DemoWindowKit.Btn(card, "关闭", () => this.GetUtility<IUIUtility>().Close(this));
        }
    }

    /// <summary>打开确认弹窗的参数：消息 + 确认回调。</summary>
    public sealed class DemoDialogArgs
    {
        public string Message;
        public Action OnConfirm;
    }

    /// <summary>模态弹窗（Popup 层 · Modal）：演示遮罩拦截下层输入 + <c>OnOpen(args)</c> 参数传递 + 回调。</summary>
    [UIWindow(Layer = UILayer.Popup, Modal = true)]
    public sealed class DemoConfirmDialog : UIToolkitWindowBase
    {
        private static readonly Color Accent = new(1f, 0.7f, 0.4f);
        private Label _message;
        private Action _onConfirm;

        protected override void OnCreated()
        {
            var card = DemoWindowKit.Card(Root, "确认弹窗 · Popup 层（模态）", Accent);
            _message = DemoWindowKit.Lbl(card, "");
            DemoWindowKit.Btn(card, "确认", () => { _onConfirm?.Invoke(); this.GetUtility<IUIUtility>().Close(this); });
            DemoWindowKit.Btn(card, "取消", () => this.GetUtility<IUIUtility>().Close(this));
        }

        // 每次打开收参数：模态期间下层（计数窗口/页面）点不动，证明遮罩生效。
        protected override void OnOpen(object args)
        {
            var a = args as DemoDialogArgs;
            _message.text = a?.Message ?? "确定要执行该操作吗？";
            _onConfirm = a?.OnConfirm;
        }
    }

    /// <summary>主页（Page 层）：演示页面栈——「进入详情页」会盖住本页（OnCover 变暗）。</summary>
    [UIWindow(Layer = UILayer.Page)]
    public sealed class DemoPageHome : UIToolkitWindowBase
    {
        private VisualElement _page;
        private Label _state;

        protected override void OnCreated()
        {
            _page = DemoWindowKit.FullPage(Root, "主页 · Page 层", new Color(0.12f, 0.20f, 0.28f, 1f));
            _state = DemoWindowKit.Lbl(_page, "（当前在最上层）");
            DemoWindowKit.Btn(_page, "进入详情页（盖住本页）", () => this.GetUtility<IUIUtility>().Open<DemoPageDetail>().Forget());
            DemoWindowKit.Btn(_page, "关闭主页", () => this.GetUtility<IUIUtility>().Close(this));
        }

        protected override void OnCover() { _state.text = "（已被详情页盖住——OnCover）"; _page.style.opacity = 0.35f; }
        protected override void OnReveal() { _state.text = "（详情页已返回，重新露出——OnReveal）"; _page.style.opacity = 1f; }
    }

    /// <summary>详情页（Page 层）：演示返回导航——「返回」= <c>Back()</c> 关本页、露出主页。</summary>
    [UIWindow(Layer = UILayer.Page)]
    public sealed class DemoPageDetail : UIToolkitWindowBase
    {
        protected override void OnCreated()
        {
            var page = DemoWindowKit.FullPage(Root, "详情页 · Page 层", new Color(0.20f, 0.14f, 0.24f, 1f));
            DemoWindowKit.Lbl(page, "返回栈顶——点「返回」会 Back() 到主页。");
            DemoWindowKit.Btn(page, "返回（Back）", () => this.GetUtility<IUIUtility>().Back());
        }
    }
}
