using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using Game.Framework.UI;
using Game.Framework.UI.UGui;
using Game.Framework.Utility;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·UI 框架：窗口 / 层级 / 栈 / 模态 / 缓存 / 生命周期，渲染后端无关（UGUI 与 UI Toolkit 共用一套调度）。
    /// 业务经 <c>this.GetUtility&lt;IUIUtility&gt;().Open&lt;T&gt;()</c> 开窗，落哪层 / 缓存 / 模态由窗口类上的 <c>[UIWindow]</c> 声明。
    /// 本章窗口全部<b>代码搭建</b>（无 prefab/UXML）：前几节是 UI Toolkit 窗口（蓝标），末节是 UGUI 窗口（绿标）——
    /// 同一套开窗 API、共享同一份分数，两套渲染后端可同屏并存，证明核心层渲染无关。
    /// </summary>
    public sealed class UIFrameworkModule : DemoModuleBase
    {
        public override string Id => "ui-framework";
        public override string Title => "UI 框架 · 窗口/层级";
        public override string Category => "能力";
        public override int Order => 70;
        public override string Summary =>
            "View 之上的 UI 调度：打开/关闭窗口、固定有序层级、Page 返回栈、模态遮罩、cover/reveal、缓存复用——渲染后端无关（UGUI/UIToolkit 共用一套核心，IUIBackend 吸收差异）。窗口经 IUIUtility 开，元数据用 [UIWindow] 特性声明。";

        private const string WindowsFile = "Assets/Game/Framework/Demo/Scripts/Modules/DemoUIWindows.cs";
        private const string UGuiWindowsFile = "Assets/Game/Framework/Demo/Scripts/Modules/DemoUGuiWindows.cs";

        public override void Build(DemoModuleHost host)
        {
            if (!TryGetUI(out _))
            {
                host.AddSectionTitle("场景未挂 UI 框架入口");
                host.AddNote("本章需要 demo 根 Context 子树下挂一个 UI 框架入口（`MonoToolkitUI` 带 UIDocument，或 `MonoUGuiUI` 带 Canvas）来注册 `IUIUtility`。当前 Context 解析不到——补好入口后回到本章即可操作。");
                host.AddNote("入口是单个 Mono 组件（镜像 `MonoPoolUtility`），自动注册为 `IUIUtility`；同一 Context 只挂一个（UGUI / Toolkit 二选一）。");
                return;
            }

            host.AddSectionTitle("窗口 = View 的一种 + 层级调度");
            host.AddNote("窗口经 `this.GetUtility<IUIUtility>().Open<T>()` 打开；落哪层 / 缓存 / 模态由窗口类上的 `[UIWindow]` 特性声明。本章窗口都是代码搭建（无 authored 资产），开关即自动注入 + Bag 释放订阅。");
            host.AddNote("**怎么分辨两套 UI**：每个窗口左上角有后端标识药丸——**蓝色「UI Toolkit」**（下面前三节）/ **绿色「UGUI」**（末节）。两套渲染后端可**同屏并存**：开一个 Toolkit 窗口再开 UGUI 窗口，蓝、绿两张卡片会同时出现在屏幕上（UGUI 窗口刻意偏右下错开），各自能点，改的是**同一份分数**。");
            host.AddTip("怎么操作：按下面从浮到顶逐个试，注意「可点性」——这些控制按钮在 demo 内容区里。"
                + "Window 层浮窗不挡它们，能接着点；但 Page（全屏盖住整个 demo）和模态弹窗（遮罩拦截下层）打开后，这些按钮就点不到了——那是层级/模态的正常效果，改用窗口自带的「返回/关闭」导航。"
                + "所以「关闭所有窗口」只在没有 Page/模态盖住时才够得着。");

            host.AddSectionTitle("Window 层 · 浮动窗口（UI Toolkit · 蓝标）");
            host.AddActionRow("打开计数窗口", () => Open<DemoCounterWindow>(),
                new CodeRef(WindowsFile, "class DemoCounterWindow", "DemoCounterWindow"));
            host.AddNote("**预期**：屏幕中央弹出一张浮动卡片，「+1」改分数、「关闭」关掉它；它不挡 demo 控制区，可继续点这里的其它按钮。"
                + "窗口复用「Model」章的 `MonoScoreModel`——和 UGUI / UIToolkit View 章是**同一份分数**。窗口只是 View 的一种载体，核心层无感。");

            host.AddSectionTitle("Popup 层 · 模态弹窗（UI Toolkit · 蓝标，遮罩拦截下层）");
            host.AddActionRow("弹确认框（模态 +1）", OpenConfirmDialog,
                new CodeRef(WindowsFile, "class DemoConfirmDialog", "DemoConfirmDialog"));
            host.AddNote("**预期**：背景压暗、下层（含 demo 控制区）点不动；点「确认」给分数 +1 并关闭，点「取消」直接关闭。"
                + "`OnOpen(args)` 收到消息 + 确认回调——遮罩是 `Modal=true` 自动铺的。");

            host.AddSectionTitle("Page 层 · 页面栈 + 返回 + cover/reveal（UI Toolkit · 蓝标）");
            host.AddActionRow("打开主页", () => Open<DemoPageHome>(),
                new CodeRef(WindowsFile, "class DemoPageHome", "DemoPageHome"));
            host.AddNote("**预期**：整个 demo 被全屏页盖住（控制区也看不见了，正常）——用页面**自带**按钮导航：主页「进入详情页」把详情页压栈、主页变暗（`OnCover`）；详情页「返回」`Back()` 弹栈、主页复原（`OnReveal`）；主页「关闭主页」回到 demo。");

            host.AddSectionTitle("UGUI 后端 · 同一套 API，不同渲染（UGUI · 绿标）");
            BuildUGuiSection(host);

            host.AddSectionTitle("批量关闭");
            host.AddActionRow("关闭所有窗口（仅未被 Page/模态盖住时可点）", CloseAllWindows,
                CodeRef.Here("void CloseAllWindows", "CloseAll 用法"));
            host.AddNote("**预期**：一键关掉两套后端所有层的窗口。前提是**点得到它**——Page/模态盖住时这个按钮够不着，先用窗口自带按钮关掉 Page/弹窗，露出 demo 控制区再点。");

            host.AddSectionTitle("小结");
            host.AddConcept("层级", "Background / Page / Window / Popup / Top / System 固定有序，后者盖前者，窗口经 `[UIWindow(Layer=…)]` 落层。");
            host.AddConcept("缓存", "`[UIWindow(Cache=Cache)]` 关闭只隐藏、再开秒显；默认 `Destroy` 关即销毁、释放资源句柄。");
            host.AddConcept("生命周期", "`OnCreate → OnOpen(args) → OnCover/OnReveal → OnClose`，由框架按栈调度（非 Unity 生命周期）。");
            host.AddConcept("接入", "窗口就是 View：享自动注入、`Bag`、`ExecuteCommand` / `GetUtility`；只读订阅查询 Command、只写经 Command。");
        }

        // UGUI 后端入口挂在另一个子 Context 上（同一 Context 只能挂一个 UI 入口），用 FindFirstObjectByType 直接拿。
        private void BuildUGuiSection(DemoModuleHost host)
        {
            var ugui = Object.FindFirstObjectByType<MonoUGuiUI>();
            if (ugui == null)
            {
                host.AddNote("场景里没找到 `MonoUGuiUI`——UGUI 后端入口未挂，跳过本段。它需要挂在**另一个子 Context** 下（同 Context 只能注册一个 `IUIUtility`，UGUI/Toolkit 要分两个 Context）。");
                return;
            }

            host.AddActionRow("打开 UGUI 计数窗口", () => ugui.Open<UGuiCounterWindow>().Forget(),
                new CodeRef(UGuiWindowsFile, "class UGuiCounterWindow", "UGuiCounterWindow"));
            host.AddNote("**预期**：屏幕**偏右下**弹出一张**绿标 UGUI**（Canvas + Image + Text + Button，代码搭建）计数卡片。"
                + "先开一个上面的蓝标 Toolkit 窗口、再开这个——两张卡片**同屏并存**（一蓝一绿、各自能点），分数却**完全一致**（共用同一份 `MonoScoreModel`）。这就是「两套 UI 融合同屏」最直观的样子。");
            host.AddNote("开窗代码 `Open<T>()` 与 Toolkit **一字不差**——只是入口换成 `MonoUGuiUI`；`IUIBackend` 吸收了 Canvas(ScreenSpaceOverlay) vs VisualElement 的全部差异。这就是「核心渲染后端无关」的活证。");
        }

        // UI 打开是 fire-and-forget：代码搭建窗口同步完成；UniTask.Forget() 会观测并记录异常。
        private void Open<T>() where T : class, IUIWindow
            => this.GetUtility<IUIUtility>().Open<T>().Forget();

        // 关掉两套后端的所有窗口：Toolkit 入口在本 Context，UGUI 入口在子 Context（各自一个 IUIUtility）。
        private void CloseAllWindows()
        {
            this.GetUtility<IUIUtility>().CloseAll();
            var ugui = Object.FindFirstObjectByType<MonoUGuiUI>();
            if (ugui != null) ugui.CloseAll();
        }

        private void OpenConfirmDialog()
            => this.GetUtility<IUIUtility>().Open<DemoConfirmDialog>(new DemoDialogArgs
            {
                Message = "确认给计数 +1？",
                OnConfirm = () => this.ExecuteCommand(new RaiseMonoScoreCommand()),
            }).Forget();

        // 检测当前 Context 是否注册了 UI 框架入口（场景里挂了 MonoToolkitUI / MonoUGuiUI 才有）。
        private bool TryGetUI(out IUIUtility ui)
        {
            try { ui = this.GetUtility<IUIUtility>(); return true; }
            catch { ui = null; return false; }
        }
    }
}
