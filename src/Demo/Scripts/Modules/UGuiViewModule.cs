using Game.Framework.Demo.Core;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·View：View 是 MVCS 的一层（UI 接缝），不是单独门类。本章实例化一个真正的 <c>MonoViewBase</c>（UGUI）——
    /// 运行时挂到 demo Context 下自动注入 + 绑 Bag，只读订阅查询 Command、只写经 ExecuteCommand，关闭即自动释放订阅。
    /// </summary>
    public sealed class UGuiViewModule : DemoModuleBase
    {
        public override string Id => "ugui-view";
        public override string Title => "界面（View）· MonoViewBase";
        public override string Category => "核心";
        public override int Order => 35;
        public override string Summary =>
            "View 是 MVCS 的一层（UI 接缝），核心层对 UI 技术无关。这里弹出一个真实的 MonoViewBase（UGUI）：Instantiate 到 Context 下即自动注入 + 绑 Bag；只读订阅查询 Command、只写经 Command，关闭时 Bag 自动释放订阅。";

        public override void Build(DemoModuleHost host)
        {
            var assets = Object.FindFirstObjectByType<DemoUGuiAssets>();
            if (assets == null || assets.ViewPrefab == null)
            {
                host.AddUnavailable(
                    "当前场景没有可用的 `DemoUGuiAssets.ViewPrefab`，因此无法实例化本章要操作的真实 UGUI View。",
                    "在 Demo 根 Context 子树下挂 `DemoUGuiAssets`，并给 `ViewPrefab` 指定带 `UGuiDemoView` 的 prefab。",
                    "接线恢复后重新进入本章即可操作；恢复前可先看“UI Toolkit View”理解同一 View 契约在另一种载体上的写法。",
                    new CodeRef(
                        "Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoUGuiAssets.cs",
                        "class DemoUGuiAssets",
                        "场景资源接线组件"));
                return;
            }

            // ── 定位 ──
            host.AddPositioning("View 是 MVCS 的一层，不是单独门类");
            host.AddNote("View 是 UI 接缝，核心层对它用什么 UI 技术一无所知。这里弹出一个真实的 `MonoViewBase`（UGUI）：`Instantiate` 到 Context 下即自动注入 + 绑 `Bag`，只读订阅查询 Command、只写经 `ExecuteCommand`，关闭时 Bag 自动退订。");

            // ── 动手试 ──
            host.AddSectionTitle("动手试：弹出一个真实 UGUI View");
            host.AddActionRow("弹出 UGUI View（自动注入）", () =>
            {
                if (Object.FindFirstObjectByType<UGuiDemoView>() != null) return; // 已经开着就不重复弹
                // Instantiate 到 Context 节点下：prefab 里的 MonoViewBase 在 Awake 自动 GetComponentInParent 找到 Context，完成注入 + 绑 Bag。
                var go = Object.Instantiate(assets.ViewPrefab, assets.transform);
#if UNITY_EDITOR
                // 弹出即选中它，方便立刻在 Inspector 看 MonoViewBase 的 Resolved Context（它绑定到的 Context）。
                DemoEditorNav.PingSceneObject(go);
#endif
            }, new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/UGuiDemoView.cs", "class UGuiDemoView", "UGuiDemoView · 真实 View"));
            host.AddNote("弹窗里：「+1」经 `ExecuteCommand` 写、文字只读订阅查询 Command；「Close」销毁自己——`Bag` 随之 `Dispose`，订阅退订。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/UGuiDemoView.cs", "protected override void Awake", "View 内部接线（Awake）"));

            host.AddSectionTitle("它绑定到哪个 Context");
            host.AddNote("View 实例化在 demo 根子树下，`Awake` 沿父链找到最近的 Context = `MonoDemoContext`（demo 的根 Context）并绑定。注意：View 不“注册”进容器（它不被别人依赖），只是把自己注入 + 绑定 `Bag`。"
                + "「多上下文（Context）」章会把**同一个 prefab** 弹进子 Context 子树——绑定随挂载位置换成子级，读写的就是子作用域那份状态，零代码切换。");
#if UNITY_EDITOR
            host.AddActionRow("选中它绑定的 Context（demo 根）", () =>
            {
                var ctx = Object.FindFirstObjectByType<MonoDemoContext>();
                if (ctx != null) DemoEditorNav.PingSceneObject(ctx.gameObject);
            }, new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "class MonoDemoContext", "demo 根 Context 定义"));
            host.AddTip("弹出后会自动选中这个 View——在 Inspector 顶部看 MonoViewBase 的 “Resolved Context”，就是它绑定到的 Context（正是 demo 根的 MonoDemoContext）。");
#endif

            host.AddSectionTitle("这个 View 做对了什么");
            host.AddConcept("自动绑定", "`Instantiate` 到 Context 下，`Awake` 自动找最近的 Context 完成注入、绑定 `Bag`——不写一行接线代码。");
            host.AddConcept("读写分离", "显示状态：订阅查询 Command 返回的只读流；改状态：`ExecuteCommand`。View 拿不到 `GetModel` / `SendEvent` 权限（编译期挡住）。");
            host.AddConcept("Bag 自动释放", "订阅进 `Bag`；关闭 View → `OnDestroy` → `Bag.Dispose` 一次性清干净，不漏订阅。");
            host.AddConcept("Awake 即可接线", "View 执行顺序 -100（最晚），`Awake` 时各层都已就绪，可直接 `ExecuteCommand` 订阅状态；覆写 `Awake` 切记先调 `base.Awake()`（基类负责注入 + 绑定 Context），漏了会 NPE。");

            host.AddNote("弹窗的状态与读写 Command 与「Model」章**共用**（`MonoScoreModel` + 它的查询/自增命令）——切到 Model 章看到的是同一个分数，同一份场景状态贯穿多章：");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/MonoScoreModel.cs", "class MonoScoreModel", "MonoScoreModel · 状态"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/ModelReactiveModule.cs", "struct GetMonoScoreCommand", "只读查询 Command"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/ModelReactiveModule.cs", "struct RaiseMonoScoreCommand", "写操作 Command"));

            host.AddSectionTitle("View 是一层，不是一类");
            host.AddNote("demo 其它章节扮演的“view 角色”是纯 C#（`DemoModuleBase`），这章是 Mono（UGUI）真实 View——两者享有相同的 View 权限，差别只是载体。核心层（Model / Command / System）对用 UGUI 还是 UI Toolkit 一无所知。");

            host.AddSectionTitle("authored 资产 vs 代码搭 UI");
            host.AddNote("这个弹窗是编辑器里搭好的 UGUI prefab（所见即所得、策划可改）。而本 demo 的外壳是命令式代码搭的——因为它是“按数据动态生成的目录”，不是固定布局。"
                + "真实业务界面通常用 authored 资产（UGUI prefab，或 UI Toolkit 的 UXML + UI Builder），代码只负责接线；命令式搭 UI 适合这种动态生成的特例，别把外壳的写法当成框架推荐。");
            host.AddTip("本章先固定 View 的职责，再比较载体。下一章换成纯 C# UI Toolkit View，但仍复用同一份 Model 与 Command；如果分数能保持一致，就证明核心层没有绑死在 UGUI 上。");
        }

    }

}
