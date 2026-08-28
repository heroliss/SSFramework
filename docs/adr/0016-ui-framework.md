# ADR-0016：UI 框架 —— 渲染后端无关的窗口/层级调度 + UGUI/UIToolkit 双 adapter

**Status:** Accepted（2026-06-13 落地：核心 + 两 adapter + UIToolkit View 接入 + Demo 章 + 单测）

## Context

`docs/roadmap.md` 把两件事写进规划但未落地：**Phase 2「UI Toolkit 接入」**与**「UI 框架」模块**（窗口/栈/层级管理）。当前框架：

- 核心层（Context / Command / Model / System / Utility / Event / Bag / 权限接口）**范式无关、纯 C#**，唯一绑 UI 的是 `MonoViewBase`（UGUI/Mono）。
- View 权限 = `ICanSendCommand + ICanRegisterEvent + ICanGetUtility`——**View 可 `GetUtility`**，这是开窗 API 的合法入口（同 `Bag.Load` 心智）。
- 已有可镜像的范式：`Game.Framework.Asset.Yoo` / `Game.Framework.Config`（core 后端无关 + adapter 分 asmdef）、`MonoPoolUtility`（单 Mono 组件 = 一套能力）、`DemoModuleBase`（纯 C# View 模板）。

需求是一套**同时吃 UGUI 与 UI Toolkit** 的 UI 调度框架，且核心零渲染依赖。

## Decision

### 1. 分层放 Utility：`IUIUtility`（镜像 `IAssetUtility` / `IPoolUtility`）

主入口 `IUIUtility`（`Open<T>` / `Close` / `Back` / `CloseAll` / `Get` / `IsOpen`）。View 经 `this.GetUtility<IUIUtility>().Open<T>()` 开窗——开窗是 UI 资源调度、不是业务状态变更，与资源加载同款心智。运行态（开着哪些窗口 / 栈）随 Utility，静态配置进入口 Mono 的 Inspector，**不单开 Model**（避免过度拆分）。需要被 `CommandSystem` 装饰器拦截（日志/回放）的业务语义流程可另包 Command。

### 2. Ports-and-adapters：核心渲染中立，UGUI/UIToolkit 各一个 adapter

```
Game.Framework.UI        (核心，渲染中立)  IUIUtility / UIUtility 编排 / IUIWindow / UILayer / [UIWindow] / IUIBackend(port)
  ├─ Game.Framework.UI.UGui     (adapter)  Canvas/RectTransform + UGuiWindowBase + MonoUGuiUI
  └─ Game.Framework.UI.Toolkit  (adapter)  UIDocument/VisualElement + UIToolkitWindowBase + ToolkitBackend + MonoToolkitUI
```

`UIUtility` 编排栈/层/缓存/cover-reveal/模态调度/参数传递；`IUIBackend` 吸收「加载资源 → 实例化 → 挂层根 → 排序 → 显隐 → 销毁」的渲染差异（Canvas sortingOrder vs VisualElement 顺序、UGUI 父链自动注入 vs UIToolkit 显式注入）。**换后端 = 换 adapter，业务开窗代码与核心零改**（同 `IAssetProvider` 隔离 YooAsset）。adapter 分 asmdef → 只用一种 UI 技术的项目可整目录删另一个。

### 3. 窗口元数据用 `[UIWindow]` 特性；层用枚举

`[UIWindow(Layer=…, Asset="ui/x", Cache=…, Modal=…)]` 类型驱动（贴框架"用类型代替字符串"理念），`UIWindowMeta.Of(type)` 按类型缓存读取。层 `UILayer`（`Background/Page/Window/Popup/Top/System`）固定有序——枚举值从下到上即堆叠顺序，backend 按序建层根。`Asset` 语义由 backend 解释：UGUI=prefab location、UIToolkit=UXML location（留空=纯代码搭建）。

### 4. 窗口生命周期 hook 由核心调度

`IUIWindow`：`OnCreate`（建后一次）→ `OnOpen(object args)`（每次打开，收参数）→ `OnCover`/`OnReveal`（被盖/露出，**按层内计算**）→ `OnClose`（每次关闭）。销毁由 backend 负责。cover/reveal 是做「被盖暂停 / 露出恢复」的关键。

### 5. 数据绑定统一走 R3 订阅，不引入 UIToolkit 原生 DataBinding

`UIBindingExtensions`（`Bag.BindText` / `BindEnabled` / `BindVisible` / `SubscribeClick` / `SubscribeClickAsync`）把 Toolkit 的元素事件接进 Bag 所有权——与 UGUI 订阅 `ReadOnlyReactiveProperty` / `UnityEvent` 完全同一套心智。异步点击由 Adapter 交付生命周期 token 并观察未处理异常，但不替业务决定禁按钮、去抖或单飞。**保持一套订阅模型**比迁就 UIToolkit 的 binding 系统值钱。

`SubscribeClickAsync` 留在 Toolkit Adapter，不下沉 Core：它解决的是 `Button.clicked` 的重复接线与异步所有权，删除后业务窗口会重复“解绑 + 生命周期取消 + 异常终点”；Core `DisposableBag` 不应因某个渲染后端增加按钮语义。UGUI 已能经通用 `Bag.Subscribe(UnityEvent, ...)` 接线，待出现同等真实重复再在对应 Adapter 增加对称 Interface。

### 6. 入口为单个 Mono 组件（镜像 `MonoPoolUtility`）

`MonoUGuiUI` / `MonoToolkitUI : MonoUtilityBase, IUIUtility`——自动注册为 `IUIUtility`，懒建 backend + 核心（首次开窗，避开「Awake 调框架服务」），经 `((IHasGameContext)this).Context`（框架适配层合法逃逸口）取自身 Context 用于资源加载 + 注入 UIToolkit 窗口。**同一 Context 只挂一个 UI 入口**（UGUI/Toolkit 二选一，重复注册 `IUIUtility` 会报错）。

### 7. UI Toolkit View 接入（Phase 2）

`UIToolkitViewBase : IView, IHasGameContext`——纯 C# View 基类（照 `DemoModuleBase`）：持 `IGameContext` + `VisualElement Root` + `Bag`，`BindTo(ctx, root)` 注入并建 UI。让 UIToolkit 视图与 `MonoViewBase` 同享自动注入 / Bag / `ExecuteCommand` / `RegisterEvent` / `GetUtility`。UIToolkit 视图不在 GameObject 父链上，故由创建方（`IUIUtility` 或引导代码）**显式**交 Context（区别于 UGUI 沿父链自动找）。

### 8. 程序集与热更归属（ADR-0008）

三个 asmdef 均 `autoReferenced:false`；因引用热更内核 `Game.Framework`，按 0008 铁律**必在热更列表**（AOT 不能引用热更）。已登记并经 `HotUpdateAssemblyGraph` 校验通过，拓扑序：`Framework → Asset.Yoo → Config → UI → UI.Toolkit → UI.UGui → Game.Main`。

## Consequences

- ✅ 换渲染后端零业务改动：开窗代码 `Open<T>()` 与核心对 UGUI/UIToolkit 一无所知。
- ✅ 核心可单测：`UIUtility` 只依赖注入的 `IUIBackend` + `IGameContext`，fake backend 脱离场景验证栈/层/cover-reveal/模态/缓存/重开置顶/hook 异常隔离（12 个用例，随框架 PlayMode 测试全绿）。
- ✅ 按需可删：不用某后端整目录删其 adapter asmdef，核心零感知。
- ✅ 真实场景验证：Toolkit backend 在 demo 场景端到端跑通（注册 → 懒初始化 → 建窗 → 层放置 → 模态遮罩渲染）。
- ⚠ **同一 Context 只能挂一个 UI 入口**（UGUI/Toolkit 二选一）；多后端并存需多 Context。
- ⚠ cover/reveal **按层内计算**（同层栈语义）；跨层覆盖（如 Popup 盖 Page）不触发下层 cover，需要时业务自行处理。
- ⚠ UI Toolkit 窗口需**无参构造**（框架用 `Activator` 实例化），数据经 `OnOpen(args)` 传入、不走构造函数。
- ⚠ UI Toolkit 窗口 Context 由框架**显式注入**（非 GameObject 父链）；独立使用 `UIToolkitViewBase` 时由持有 Context 的引导方调 `BindTo`。

用法手册见 `docs/framework-guide.md` §17；活样例见 demo「View · UIToolkit」+「UI 框架 · 窗口/层级」章。

**2026-08-24 验证补充：**Demo 新增 Destroy / Cache 两个现场对照窗，以稳定实例号和 `OnCreate / OnOpen / OnClose` 计数展示真实生命周期；PlayMode 契约穿过 DemoScene 的 `MonoToolkitUI` Adapter，锁定 Destroy 重开换实例、Cache 重开复用同一实例。这样核心 fake backend 测试与真实 Adapter 证据形成两层验证，也明确 Cache 是“常驻内存与状态管理复杂度换创建速度”，不是默认更优。

**2026-08-26 Adapter 契约补强：**Toolkit 原本会在加载 UXML 前验证 `UIToolkitWindowBase`，UGUI 却只检查最终对象能否转成 `IUIWindow`，使普通 `MonoBehaviour + IUIWindow` 能绕过 `MonoViewBase` 注入与 Bag 所有权。两个 Adapter 现统一在创建层级或加载资源前验证各自窗口基类并 fail-fast；窗口类型、prefab 根组件、节点绑定与生命周期 hook 错误统一进入 `Log` Seam，category、异常和 Unity context 可同时被 Console、文件与测试 sink 消费。`UIRuntimeLoggingTests` 锁定“失败前无层级副作用”和 context 透传。

**2026-08-26 异步交互所有权补强：**Toolkit Adapter 新增 `Bag.SubscribeClickAsync`，把按钮解绑、View 生命周期取消与异常观察收成一个窄而深的接缝；5 项 PlayMode 契约锁定异常日志、Bag 释放取消、单订阅释放、已释放 Bag 不接线，以及物理操作忽略 View token 后仍走到终态并被观察。Outpost 实战验证了两种边界：榜单刷新跟随窗口取消；已启动的扩展包下载由包级物理操作拥有、窗口关闭后继续，但安装标记保存被纳入下载的完成终点。Adapter 刻意不自动实现 single-flight，也不把 UI 按钮语义推进 Core。

**2026-08-28 必需窗口失败边界：**`Open<T>` 继续保留“未获得实例时返回 null”的宽松 Interface，供可选窗口在调用点隐藏、替代或重试；null 可能来自 Adapter 创建失败，也可能来自创建期间 UI 生命周期结束。新增非破坏性的 `OpenRequired<T>` 扩展，把同一个 null 提升为带窗口类型与资源位置的异常，调用方取消仍保持 `OperationCanceledException`。没有把严格模式做成 `IUIUtility` 新成员或布尔参数：两种路径共享全部创建 Implementation，差异只是调用处的业务不变量，扩展方法既提高错误 Locality，也不迫使自定义 Adapter 重复实现；它不改变 hook 异常隔离，也不把开窗定义为事务提交。Flow 主页面与承诺打开可见窗口的动作使用严格入口；真实 `GameFlow` 契约锁定创建失败后 `Current` 仍为 null。
