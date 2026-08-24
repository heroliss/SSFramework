using System;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using Game.Framework.Internal;
using Game.Framework.UI.Toolkit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·View（UI Toolkit）：与「View · MonoViewBase」(UGUI) 是<b>同一层、不同载体</b>。
    /// 这里弹出一个纯 C# 的 <see cref="UIToolkitViewBase"/>——无需 prefab、代码即可搭；自动注入 + 绑 Bag、
    /// 只读订阅查询 Command、只写经 ExecuteCommand，关闭即 Dispose 退订。状态与 UGUI 章<b>共用同一份</b>
    /// <c>MonoScoreModel</c>，直观证明：核心层（Model / Command / System）对用 UGUI 还是 UI Toolkit 一无所知。
    /// </summary>
    public sealed class UIToolkitViewModule : DemoModuleBase
    {
        public override string Id => "uitoolkit-view";
        public override string Title => "View · UIToolkit";
        public override string Category => "核心";
        public override int Order => 36; // 紧跟「View · MonoViewBase」(35)
        public override string Summary =>
            "纯 C# 的 UIToolkitViewBase 与 UGUI 的 MonoViewBase 共用 IView 权限、Command 和 Bag，只是 Context 绑定方式不同。" +
            "两章共用同一份 Model，证明核心层不依赖 UI 技术。";

        private UIToolkitDemoView _view;

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("同一层、换个载体——UI Toolkit");
            host.AddNote("与「View · MonoViewBase」是**同一层、不同载体**：纯 C# 的 `UIToolkitViewBase` 无需 prefab、代码即可搭；同享 `IView` 权限、自动注入、`Bag`、`ExecuteCommand`。状态与 UGUI 章**共用同一份** `MonoScoreModel`——直观证明核心层对 UI 技术无感。");

            // ── 动手试 ──
            host.AddSectionTitle("动手试：弹出一个纯 C# UIToolkit View");
            host.AddActionRow("弹出 UIToolkit View（无 prefab）", () =>
            {
                if (_view != null && !_view.IsDisposed) return; // 已经开着就不重复弹

                // demo 模块在这里充当"持有 Context 的引导方"：通过框架内部的合法逃逸口拿到自己的 Context，
                // 交给纯 C# 视图（UIToolkit 视图不在 GameObject 父链上，必须显式绑定）。
                // 业务里这一步通常由 UI 框架的 IUIUtility 代劳（开窗时自动绑定），无需手写。
                var ctx = ((IHasGameContext)this).Context;
                _view = new UIToolkitDemoView(CloseView);
                host.Content.Add(_view.BindTo(ctx));
                Bag.Add(_view); // 切走本章（Teardown → Bag.Dispose）时自动释放视图，订阅随之退订
            }, CodeRef.Here("class UIToolkitDemoView", "UIToolkitDemoView · 纯 C# View"));

            host.AddNote("卡片里：「+1」经 `ExecuteCommand` 写、文字用 `Bag.BindText` 订阅查询 Command；「关闭」`Dispose` 自己——`Bag` 随之释放、`Root` 摘出可视树。",
                CodeRef.Here("protected override void OnCreated", "View 内部接线（OnCreated）"));

            host.AddSectionTitle("和 UGUI View 比，差在哪");
            host.AddConcept("载体不同", "UGUI 是 MonoBehaviour + prefab（所见即所得、可拖引用）；UIToolkit 视图是纯 C# + VisualElement，本例直接代码搭，无需 authored 资产。");
            host.AddConcept("接入相同", "两者都实现 `IView`：自动注入、`Bag` 生命周期、`ExecuteCommand` / `RegisterEvent` / `GetUtility` 完全一致——只是 UGUI 走 `MonoViewBase`（Awake 沿父链找 Context），UIToolkit 走 `BindTo`（创建方显式交 Context）。");
            host.AddConcept("绑定相同", "都用 R3 订阅：UGUI `Bag.Subscribe(rop, …)`，UIToolkit `Bag.BindText(label, rop)`——一套心智，没有第二套数据绑定。");

            host.AddSectionTitle("核心层对 UI 技术无感");
            host.AddNote("这张卡片读写的分数，和「View · MonoViewBase」(UGUI) 章是**同一个** `MonoScoreModel`、同一对查询/写命令。切到那一章，分数一致——证明 Model / Command / System 根本不知道上层用的是 UGUI 还是 UI Toolkit。");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/MonoScoreModel.cs", "class MonoScoreModel", "MonoScoreModel · 共用状态"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/ModelReactiveModule.cs", "struct GetMonoScoreCommand", "只读查询 Command"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/ModelReactiveModule.cs", "struct RaiseMonoScoreCommand", "写操作 Command"));
#if UNITY_EDITOR
            host.AddActionRow("选中共享分数 Model（运行时 Inspector 看 Score 值跳变）", () =>
            {
                var model = UnityEngine.Object.FindFirstObjectByType<MonoScoreModel>();
                if (model != null) DemoEditorNav.PingSceneObject(model.gameObject);
            });
#endif

            host.AddTip("本章只演示「UIToolkit 视图怎么接入框架」这一层；真正的窗口/层级/模态/栈管理在「UI 框架 · 窗口/层级」章——那里 UGUI 与 UIToolkit 共用同一套调度，按界面选载体。");
        }

        // 关闭视图：Dispose 幂等，释放 Bag + 摘出可视树；置空让下次可重新弹。
        private void CloseView()
        {
            _view?.Dispose();
            _view = null;
        }
    }

    /// <summary>
    /// 「View · UIToolkit」章用的真实纯 C# 视图：代码搭一张小卡片（分数 + 「+1」+「关闭」）。
    /// 继承 <see cref="UIToolkitViewBase"/>——<c>BindTo</c> 后在 <see cref="OnCreated"/> 里接线，
    /// 与 UGUI 的 <c>UGuiDemoView</c> 一一对应，只是载体从 prefab 换成 VisualElement。
    /// </summary>
    public sealed class UIToolkitDemoView : UIToolkitViewBase
    {
        private readonly Action _onCloseRequested;

        public UIToolkitDemoView(Action onCloseRequested) => _onCloseRequested = onCloseRequested;

        // BindTo 之后调用一次：此时 Context 已绑定、各层就绪，可直接 ExecuteCommand 订阅状态。
        protected override void OnCreated()
        {
            // 内联样式把 Root 渲染成一张卡片（demo 主题里没有现成 card class，少量内联即可，不污染 USS）。
            Root.style.marginTop = 8;
            Root.style.marginBottom = 8;
            Root.style.paddingTop = 10;
            Root.style.paddingBottom = 10;
            Root.style.paddingLeft = 12;
            Root.style.paddingRight = 12;
            Root.style.borderTopWidth = 1;
            Root.style.borderBottomWidth = 1;
            Root.style.borderLeftWidth = 1;
            Root.style.borderRightWidth = 1;
            var border = new Color(0.45f, 0.55f, 0.75f, 0.8f);
            Root.style.borderTopColor = border;
            Root.style.borderBottomColor = border;
            Root.style.borderLeftColor = border;
            Root.style.borderRightColor = border;
            Root.style.borderTopLeftRadius = 6;
            Root.style.borderTopRightRadius = 6;
            Root.style.borderBottomLeftRadius = 6;
            Root.style.borderBottomRightRadius = 6;
            Root.style.backgroundColor = new Color(1f, 1f, 1f, 0.04f);

            var title = new Label("UIToolkit View（纯 C#）") { enableRichText = false };
            title.AddToClassList("demo-section-title");
            Root.Add(title);

            var score = new Label();
            score.AddToClassList("demo-value");
            Root.Add(score);

            var addBtn = new Button { text = "+1" };
            addBtn.AddToClassList("demo-btn");
            Root.Add(addBtn);

            var closeBtn = new Button { text = "关闭" };
            closeBtn.AddToClassList("demo-btn");
            Root.Add(closeBtn);

            // 只读：订阅查询 Command 返回的状态流（订阅即得当前值）。与 UGUI 章共用 MonoScoreModel。
            Bag.BindText(score, this.ExecuteCommand(new GetMonoScoreCommand()), v => $"Score: {v}");
            // 只写：所有外发动作只能 ExecuteCommand（View 拿不到 GetModel / SendEvent 权限，编译期挡住）。
            Bag.SubscribeClick(addBtn, () => this.ExecuteCommand(new RaiseMonoScoreCommand()));
            // 关闭：请求宿主销毁自己 → Dispose → Bag 释放 + 退订。
            Bag.SubscribeClick(closeBtn, () => _onCloseRequested());
        }
    }
}
