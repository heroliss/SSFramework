using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
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

        private const string WindowsFile = "Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoUIWindows.cs";
        private const string UGuiWindowsFile = "Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoUGuiWindows.cs";

        public override void Build(DemoModuleHost host)
        {
            if (!TryGetUI(out _))
            {
                host.AddUnavailable(
                    "当前 Demo 根 Context 解析不到 `IUIUtility`，窗口调度没有可用的 UI 后端入口。",
                    "在该 Context 子树下挂一个 `MonoToolkitUI`（带 UIDocument）或 `MonoUGuiUI`（带 Canvas）；同一 Context 只选一个后端。",
                    "接好入口后重新进入本章即可操作；恢复前可先看“UGUI View / UI Toolkit View”，理解窗口背后的 View 契约。",
                    new CodeRef(
                        "Assets/Game/Framework/UI.Toolkit/MonoToolkitUI.cs",
                        "class MonoToolkitUI",
                        "Toolkit UI 入口"));
                return;
            }

            host.AddPositioning("窗口 = View 的一种，框架管层级/栈/模态调度");
            host.AddNote("窗口经 `IUIUtility` 打开；落哪层 / 缓存 / 模态由窗口类上的 `[UIWindow]` 特性声明。本章窗口都是代码搭建（无 authored 资产），开关即自动注入 + Bag 释放订阅。");
            host.AddSubNote("`Open<T>()` 是宽松入口：未获得窗口实例时返回 `null`，适合允许缺席并准备就地降级的提示窗；`OpenRequired<T>()` 是严格入口：把 `null` 变成带窗口类型与资源位置的异常，适合主页面、Flow 状态等业务不变量。本章按钮承诺打开可见窗口，因此使用严格入口。",
                new CodeRef("Assets/Game/Framework/UI/UIUtilityExtensions.cs", "var window = await ui.Open<T>(ct);", "OpenRequired · 必需窗口入口"));
            host.AddNote("渲染后端无关：UGUI 与 UI Toolkit 共用同一套调度、可同屏并存；严格或宽松只决定失败策略，不绑定某种渲染技术。");
            host.AddNote("**怎么分辨两套 UI**：每个窗口左上角有后端标识药丸——**蓝色「UI Toolkit」**（下面前三节）/ **绿色「UGUI」**（末节）。两套渲染后端可**同屏并存**：开一个 Toolkit 窗口再开 UGUI 窗口，蓝、绿两张卡片会同时出现在屏幕上（UGUI 窗口刻意偏右下错开），各自能点，改的是**同一份分数**。");
            host.AddTip("怎么操作：按下面从浮到顶逐个试，注意「可点性」——这些控制按钮在 demo 内容区里。"
                + "Window 层浮窗不挡它们，能接着点；但 Page（全屏盖住整个 demo）和模态弹窗（遮罩拦截下层）打开后，这些按钮就点不到了——那是层级/模态的正常效果，改用窗口自带的「返回/关闭」导航。"
                + "所以「关闭所有窗口」只在没有 Page/模态盖住时才够得着。");
#if UNITY_EDITOR
            host.AddActionRow("选中 Toolkit UI 入口 MonoToolkitUI（本章窗口的默认后端）", () =>
            {
                var tk = UnityEngine.Object.FindFirstObjectByType<MonoToolkitUI>();
                if (tk != null) DemoEditorNav.PingSceneObject(tk.gameObject);
            });
#endif

            host.AddSectionTitle("Window 层 · 浮动窗口（UI Toolkit · 蓝标）");
            host.AddAsyncActionRow("打开计数窗口", ct => Open<DemoCounterWindow>(ct),
                new CodeRef(WindowsFile, "class DemoCounterWindow", "DemoCounterWindow"));
            host.AddNote("**预期**：屏幕中央弹出一张浮动卡片，「+1」改分数、「关闭」关掉它；它不挡 demo 控制区，可继续点这里的其它按钮。"
                + "窗口复用「Model」章的 `MonoScoreModel`——和 UGUI / UIToolkit View 章是**同一份分数**。窗口只是 View 的一种载体，核心层无感。");

            host.AddSectionTitle("缓存策略 · Destroy 与 Cache 的实例身份对照");
            host.AddAsyncActionRow("打开 Destroy 对照窗", ct => Open<DemoDestroyPolicyWindow>(ct),
                new CodeRef(WindowsFile, "class DemoDestroyPolicyWindow", "Destroy 策略窗口"));
            host.AddAsyncActionRow("打开 Cache 对照窗", ct => Open<DemoCachedPolicyWindow>(ct),
                new CodeRef(WindowsFile, "class DemoCachedPolicyWindow", "Cache 策略窗口"));
            host.AddNote("分别按「打开 → 窗内关闭 → 再打开」：Destroy 会换一个 `实例 #N`，且生命周期计数从 `OnCreate 1 / OnOpen 1` 重新开始；Cache 保持同一实例号，`OnCreate` 仍是 1、`OnOpen` 递增，重开时还能看到上次 `OnClose`。这是真实窗口实例的现场证据，不是计时快慢造成的错觉。");
            host.AddSubNote("选择标准：频繁开关、构造或资源加载昂贵、且能明确重置临时状态的窗口适合 Cache；低频窗口或持有大资源、关闭后应彻底释放的窗口用 Destroy。Cache 用空间和更复杂的重开状态换速度，不是无条件更优。");

            host.AddSectionTitle("Popup 层 · 模态弹窗（UI Toolkit · 蓝标，遮罩拦截下层）");
            host.AddAsyncActionRow("弹确认框（模态 +1）", OpenConfirmDialog,
                new CodeRef(WindowsFile, "class DemoConfirmDialog", "DemoConfirmDialog"));
            host.AddNote("**预期**：背景压暗、下层（含 demo 控制区）点不动；点「确认」给分数 +1 并关闭，点「取消」直接关闭。"
                + "`OnOpen(args)` 收到消息 + 确认回调——遮罩是 `Modal=true` 自动铺的。");

            host.AddSectionTitle("Page 层 · 页面栈 + 返回 + cover/reveal（UI Toolkit · 蓝标）");
            host.AddAsyncActionRow("打开主页", ct => Open<DemoPageHome>(ct),
                new CodeRef(WindowsFile, "class DemoPageHome", "DemoPageHome"));
            host.AddNote("**预期**：整个 demo 被全屏页盖住（控制区也看不见了，正常）——用页面**自带**按钮导航：主页「进入详情页」把详情页压栈、主页变暗（`OnCover`）；详情页「返回」`Back()` 弹栈、主页复原（`OnReveal`）；主页「关闭主页」回到 demo。");

            host.AddSectionTitle("UGUI 后端 · 同一套 API，不同渲染（UGUI · 绿标）");
            BuildUGuiSection(host);

            host.AddSectionTitle("刚需件 · 过渡 / Toast / Loading / 返回键（ADR-0020，UI Toolkit · 蓝标）");
            host.AddAsyncActionRow("打开带过渡的窗口（0.6s 淡入）", ct => Open<DemoTransitionWindow>(ct),
                new CodeRef(WindowsFile, "class DemoTransitionWindow", "DemoTransitionWindow"));
            host.AddNote("**预期**：卡片 0.6 秒淡入，期间**整屏点不动**（框架全屏挡输入——防连点/动画中操作）；"
                + "窗口自带「关闭」按 0.6 秒淡出后才真正消失。窗口只重写 `OnOpenTransition/OnCloseTransition` 两个 hook，挡输入是框架统一做的。"
                + "**逻辑关闭先于表现**：淡出期间它已不在栈里——立即再点「打开」会新建一个实例（旧卡片还在淡出，短暂两张同屏是预期）。");

            host.AddAsyncActionRow("弹 Toast（2 秒自动关）",
                ct => this.GetUtility<IUIUtility>().ShowToast("操作成功 · 我是 Toast", ct: ct),
                CodeRef.Here("ShowToast", "ShowToast 调用"));
            host.AddNote("**预期**：屏幕底部弹半透明文字条，2 秒自动消失、**不拦任何输入**（弹着的时候其它按钮照点）；"
                + "连点几次是复用同一条（刷新文本、重置计时，刻意不做队列）。`ShowToast/AcquireLoading` 是 `IUIUtility` **一等方法**——"
                + "业务调用点对后端零感知，内置窗口类型由入口（MonoToolkitUI/MonoUGuiUI）注册。计时与 Close/CloseAll/Dispose 的竞态由 UI 核心统一收口，两个 adapter 只负责画出各自风格。");

            host.AddAsyncActionRow("Acquire Loading（2.5 秒后自动释放）", ShowLoadingThenHide,
                CodeRef.Here("async UniTask ShowLoadingThenHide", "AcquireLoading 用法"));
            host.AddNote("**预期**：中央旋转指示块 + 提示文本，**模态挡输入**（demo 按钮点不动）且返回键被拦（`BackClosable=false`）；"
                + "2.5 秒后 `LoadingHandle.Dispose()` 自动恢复。真实用法是 `using var loading = await AcquireLoading → await 干活`。");
            host.AddSubNote("为什么返回 lease：多个异步任务可能同时需要全局 Loading，较早任务结束时不能把仍在使用的提示误关；框架只在最后一个有效 handle 释放后关闭。"
                + "内置件是无美术资源的默认表现，正式项目可自写 Top 层窗口替代。");

            var backResult = host.AddValueDisplay("Back() 的返回值会显示在这里", CodeRef.Here("bool consumed", "Back 返回值判断"));
            host.AddActionRow("Back()（模拟 Esc / Android 返回键）", () =>
            {
                bool consumed = this.GetUtility<IUIUtility>().Back();
                backResult.text = consumed
                    ? "Back() → true：返回键被 UI 消费（关了栈顶、被 BackClosable 拦截、或过渡中被吞）"
                    : "Back() → false：Popup/Window/Page 三层皆空——业务可做「再按一次退出」兜底";
            }, CodeRef.Here("this.GetUtility<IUIUtility>().Back()", "返回导航语义"));
            host.AddNote("**预期**：先开几个窗口再点——按 **Popup → Window → Page** 从高到低关第一个非空层的栈顶（弹窗优先于浮窗、浮窗优先于页面）。"
                + "真机/键盘接线属于项目输入层：本 Demo 用 `DemoInputSystemBackKeyDriver` 把新 Input System 的 Esc / Android Back 映射到 `Back()`，"
                + "不让 UI Core 绑定某个输入 Package（**本场景已挂**，Play 中直接按 **Esc** 试）。");
#if UNITY_EDITOR
            host.AddActionRow("选中 Demo 的 Input System 返回键接线样板", () =>
            {
                var drv = UnityEngine.Object.FindFirstObjectByType<DemoInputSystemBackKeyDriver>();
                if (drv != null) DemoEditorNav.PingSceneObject(drv.gameObject);
            }, new CodeRef(
                "Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoInputSystemBackKeyDriver.cs",
                "class DemoInputSystemBackKeyDriver",
                "项目输入 → UI Back 的 composition 样板"));
#endif

            host.AddNote("**安全区**（刘海/挖孔屏）：UGUI 内容根挂 `UGuiSafeArea`、Toolkit 内容放进 `SafeAreaContainer`（UXML 可摆）——"
                + "编辑器全屏看不出差异，用 **Device Simulator** 选带刘海机型可见内容避让；层根/背景刻意保持全屏出血。");

            host.AddSectionTitle("批量关闭");
            host.AddActionRow("关闭所有窗口（仅未被 Page/模态盖住时可点）", CloseAllWindows,
                CodeRef.Here("void CloseAllWindows", "CloseAll 用法"));
            host.AddNote("**预期**：一键关掉两套后端所有层的窗口。前提是**点得到它**——Page/模态盖住时这个按钮够不着，先用窗口自带按钮关掉 Page/弹窗，露出 demo 控制区再点。");

            host.AddSectionTitle("小结");
            host.AddConcept("层级", "Background / Page / Window / Popup / Top / System 固定有序，后者盖前者，窗口经 `[UIWindow(Layer=…)]` 落层。");
            host.AddConcept("缓存", "`[UIWindow(Cache=Cache)]` 关闭只隐藏并复用同一实例；默认 `Destroy` 关即销毁、释放资源句柄。上方实例号与 hook 计数可现场核对。");
            host.AddConcept("生命周期", "`OnCreate → OnOpen(args) → OnCover/OnReveal → OnClose`，由框架按栈调度（非 Unity 生命周期）。");
            host.AddConcept("接入", "窗口就是 View：享自动注入、`Bag`、`ExecuteCommand` / `GetUtility`；只读订阅查询 Command、只写经 Command。");
        }

        // UGUI 后端入口挂在另一个子 Context 上（同一 Context 只能挂一个 UI 入口），用 FindFirstObjectByType 直接拿。
        private void BuildUGuiSection(DemoModuleHost host)
        {
            var ugui = UnityEngine.Object.FindFirstObjectByType<MonoUGuiUI>();
            if (ugui == null)
            {
                host.AddNote("场景里没找到 `MonoUGuiUI`——UGUI 后端入口未挂，跳过本段。它需要挂在**另一个子 Context** 下（同 Context 只能注册一个 `IUIUtility`，UGUI/Toolkit 要分两个 Context）。");
                return;
            }

#if UNITY_EDITOR
            host.AddActionRow("选中 UGUI 后端入口 MonoUGuiUI（挂在另一个子 Context）",
                () => DemoEditorNav.PingSceneObject(ugui.gameObject));
#endif

            host.AddAsyncActionRow("打开 UGUI 计数窗口", async ct => await ugui.OpenRequired<UGuiCounterWindow>(ct),
                new CodeRef(UGuiWindowsFile, "class UGuiCounterWindow", "UGuiCounterWindow"));
            host.AddNote("**预期**：屏幕**偏右下**弹出一张**绿标 UGUI**（Canvas + Image + Text + Button，代码搭建）计数卡片。"
                + "先开一个上面的蓝标 Toolkit 窗口、再开这个——两张卡片**同屏并存**（一蓝一绿、各自能点），分数却**完全一致**（共用同一份 `MonoScoreModel`）。这就是「两套 UI 融合同屏」最直观的样子。");
            host.AddNote("开窗代码 `OpenRequired<T>()` 与 Toolkit **一字不差**——只是入口换成 `MonoUGuiUI`；`IUIBackend` 吸收了 Canvas(ScreenSpaceOverlay) vs VisualElement 的全部差异。这就是「核心渲染后端无关」的活证。");

            host.AddAsyncActionRow("打开 UGUI 计数窗口（prefab + 生成绑定）", async ct => await ugui.OpenRequired<DemoUGuiPrefabCounterWindow>(ct),
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/PrefabBinding/DemoUGuiPrefabCounterWindow.cs", "class DemoUGuiPrefabCounterWindow", "DemoUGuiPrefabCounterWindow"));
            host.AddNote("**预期**：屏幕**偏左下**再弹一张绿标 UGUI 计数卡片。这张不是代码搭建——控件在 **prefab 上摆好、脚本挂根上**，"
                + "`ScoreText/AddButton/CloseButton` 三个字段由右键 prefab「生成 UI 绑定代码」产出的 `DemoUGuiPrefabCounterWindow.nodes.g.cs` 自动绑定（`transform.Find`），本窗口只在 `OnCreated` 写逻辑。");
            host.AddNote("**同一窗口、三种接法**：代码搭建（`UGuiCounterWindow`）／ 手工 `[SerializeField]` 拖引用（`UGuiDemoView`）／ prefab + 生成绑定（本窗口）——同一框架、同一份分数，只是节点引用怎么来不同。绑定代码全自动生成、改完 prefab 重新生成即可，省掉手接引用的重复劳动。");
            host.AddSubNote("⚠ prefab 窗口经资源系统按 location 加载——若点了没出现，先去「资源加载」章点「初始化」让默认包就绪（代码搭建窗口不读资源、不受此影响）。");

            host.AddAsyncActionRow("打开 UGUI 计数窗口（变体）", async ct => await ugui.OpenRequired<DemoUGuiPrefabCounterWindowVariant>(ct),
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/PrefabBinding/DemoUGuiPrefabCounterWindowVariant.cs", "class DemoUGuiPrefabCounterWindowVariant", "DemoUGuiPrefabCounterWindowVariant"));
            host.AddNote("上一张窗口的**预制体变体**：变体 prefab 只多加了个「归零」按钮。变体窗口类**继承**基窗口类、绑定**只生成净新增字段**（`ResetButton`）——"
                + "基类的 Score / +1 / 关闭由 `base.OnCreated()` 复用，变体 `OnCreated` 里只接自己多出来的归零按钮。改基窗口，变体自动跟随。");
            host.AddSubNote("生成器靠 Unity 原生变体关系识别变体 → 子类 `: 基窗口类` + 只补新字段（删基节点 / 改基组件会在生成期警告）；本变体的增量绑定见生成文件。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/PrefabBinding/Generated/DemoUGuiPrefabCounterWindowVariant.nodes.g.cs", "protected global::UnityEngine.UI.Button ResetButton", "变体增量绑定（nodes.g.cs）"));

            host.AddSectionTitle("生成产物落点 · 目录级生成配置");
            host.AddNote("注意上面两张 prefab 窗口的绑定代码并没有落到全工程默认目录（正式游戏的 `Assets/Game/Main/UI`），而是和窗口逻辑待在同一个 `Modules/PrefabBinding/` 文件夹里——"
                + "这是「目录级生成配置」做的：在被管 prefab 的**非收集祖先目录**（demo 放在模块根 `Demo/`，资产名 `DemoUICodeGenDirConfig`；放 `Res/` 会被打进资源包）放一个 `UICodeGenDirConfig` 资产，把这两个 prefab 的命名空间 / 逻辑目录 / 生成目录 / 文件名整套重定向到 demo 自己的位置。");
            host.AddSubNote("配置按 **prefab 所在目录向上**生效，逐字段覆盖：命名空间 / 逻辑目录 / 生成目录 / 文件名，每项「留空 = 继承上层」，一路回退到全工程 `UICodeGenProfile`（现为正式游戏默认值）。"
                + "本 demo 那份**四项全填、完全自洽**——所以全局默认改成正式游戏设置后，demo 的绑定代码依旧落在 demo 目录、命名空间也仍是 `Game.Framework.Demo.Modules`，不受影响。"
                + "于是「按模块分目录 / 分命名空间」既不用改生成器、也不用每个 prefab 单独设——把一个配置丢进目录，该目录（及子目录）下的 prefab 重新生成时就自动跟着走。",
                new CodeRef("Assets/Game/Framework/UI.UGui/Editor/UICodeGenDirConfig.cs", "class UICodeGenDirConfig", "UICodeGenDirConfig · 目录级生成配置"));
        }

        // 即使代码搭建窗口通常同步完成，也保留 UniTask 给 Host 统一治理异常、重入与切章取消。
        private async UniTask Open<T>(CancellationToken ct) where T : class, IUIWindow
            => await this.GetUtility<IUIUtility>().OpenRequired<T>(ct);

        // Loading 的真实用法形态：Acquire 得到所有权句柄；using 确保成功、异常、取消三条路径都会释放。
        private async UniTask ShowLoadingThenHide(CancellationToken ct)
        {
            var ui = this.GetUtility<IUIUtility>();
            using var loading = await ui.AcquireLoading("正在加载…（2.5 秒后自动关闭）", ct);
            await UniTask.Delay(TimeSpan.FromSeconds(2.5), ignoreTimeScale: true, cancellationToken: ct);
        }

        // 关掉两套后端的所有窗口：Toolkit 入口在本 Context，UGUI 入口在子 Context（各自一个 IUIUtility）。
        private void CloseAllWindows()
        {
            this.GetUtility<IUIUtility>().CloseAll();
            var ugui = UnityEngine.Object.FindFirstObjectByType<MonoUGuiUI>();
            if (ugui != null) ugui.CloseAll();
        }

        private async UniTask OpenConfirmDialog(CancellationToken ct)
            => await this.GetUtility<IUIUtility>().OpenRequired<DemoConfirmDialog>(new DemoDialogArgs
            {
                Message = "确认给计数 +1？",
                OnConfirm = () => this.ExecuteCommand(new RaiseMonoScoreCommand()),
            }, ct);

        // 检测当前 Context 是否注册了 UI 框架入口（场景里挂了 MonoToolkitUI / MonoUGuiUI 才有）。
        private bool TryGetUI(out IUIUtility ui)
        {
            try { ui = this.GetUtility<IUIUtility>(); return true; }
            catch { ui = null; return false; }
        }
    }
}
