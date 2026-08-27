# ADR-0020：UI 刚需补齐 —— 异步过渡 + Back 键 + 安全区 + Top 层常用件

**Status:** Accepted（四项全部实现，2026-07-03；Loading 的并发所有权由 ADR-0037 深化；输入接线边界于 2026-08-27 收紧）

## Context

ADR-0016 落地了渲染中立的窗口/层级调度核心，但四件「所有手游都要」的刚需还缺：

1. **打开/关闭过渡**：窗口生命周期全同步，动画只能业务自己在 OnOpen 里播——没有「动画期间挡输入」的统一保障，连点开两个窗、动画中点按钮是常见事故。
2. **Android Back / Esc**：`Back()` API 已有但没人喂输入；且它只看 Page 层——弹窗开着时按返回键关掉底下的页，不符合手游直觉。
3. **安全区**：刘海/挖孔屏下窗口内容会被遮挡，UGUI / UI Toolkit 各需一个避让手段。
4. **Top 层常用件**：Toast / Loading 全局提示每个项目都要，不该每次重写。

约束基线：核心零渲染依赖（ports & adapters，ADR-0016）；现有同步路径的行为与测试不能被破坏；项目输入为**新 Input System**（`activeInputHandler=1`），但框架可复用性要求不硬绑。

## Decision

### 1. 异步过渡：hook 进 `IUIWindow` 本体，框架统一编排 + 全屏挡输入

`IUIWindow` 增加两个异步 hook（基类默认返回已完成 task，业务只重写需要的——与既有 5 个同步 hook 同构；接口实现全在仓库内，无外部破坏面）：

```csharp
UniTask OnOpenTransition(CancellationToken ct);   // 入场动画：OnOpen 之后播
UniTask OnCloseTransition(CancellationToken ct);  // 出场动画：OnClose 之前播（窗口仍可见）
```

**时序：**

- **打开**：可见/置顶 → `OnOpen(args)` → `OnOpenTransition`。`Open<T>` 在 OnOpen 后即返回，**不等过渡**——过渡是表现层的事，不拖业务续体；动画期间的防护由框架挡输入承担。
- **关闭**：逻辑摘栈（`IsOpen` 立即 false）+ 撤模态遮罩 + 下方窗口 `OnReveal`（立即）→ `OnCloseTransition`（窗口仍可见，播出场动画）→ `OnClose` → 隐藏/销毁。`Close` 保持 `void`（内部 fire-and-forget 编排）。

**关键取舍：**

- **逻辑关闭立即生效**：动画期间窗口已不在栈里——`Back()`/`CloseTop` 不会再次命中它，同类型可立即重新 `Open`（新实例）。「表现滞后于逻辑」比「动画期间世界卡住」简单且可预期。
- **挡输入 = 全屏计数挡板**：backend 新增 `SetInputBlocked(bool)`（UGUI：canvas 顶层透明 raycast Image；Toolkit：root 顶层 `PickingMode.Position` 透明元素）。核心持计数器，任一过渡进行中即挡——防连点、防动画中操作，一处保障全局。
- **默认零开销**：hook 默认返回已完成 task → 框架同步走完，行为与改动前逐帧一致（既有测试不动就得全绿）。
- **异常/取消**：过渡抛异常 → `LogException` + 视为完成（一个窗口的动画 bug 不能挡死全屏输入）；ct 为 Context 令牌，Dispose 级联取消。
- **`CloseAll` / `Dispose` 不播过渡**：批量关闭多用于场景切换，要的是立刻干净，不是 N 个出场动画。
- 缓存复用的重开**播**入场过渡（用户看到的是「打开」）；已打开置顶刷新**不播**（本就在屏上）。

### 2. Back / Esc：`Back()` 拥有导航语义，物理输入留在项目 composition layer

- **`Back()` 语义升级**（`void` → `bool`）：按 **Popup → Window → Page** 从高到低找第一个非空层，检查栈顶窗口：
  - `BackClosable`（默认 true）→ 关闭它，返回 `true`；
  - `BackClosable = false` → 不动作但返回 `true`（back 已被 UI 消费——强引导/不可跳过流程用它拦住返回键）；
  - 三层全空 → 返回 `false`（业务据此做「再按一次退出」之类的兜底）。
  - `Top` / `System` 不参与（Toast/系统提示不是导航单元）、`Background` 不参与（底景）。
  - **过渡进行中 `Back()` 直接吞掉**（返回 `true` 不动作）——与「动画期间挡输入」同一语义，键盘路径不绕过挡板。
- **`[UIWindow(BackClosable = false)]`** 进窗口元数据。
- **UI Core 到此为止**：它提供稳定的 `IUIUtility.Back()`，不轮询键盘、不引用 Input System，也不猜项目使用 Input Action、旧 Input Manager、平台 SDK 还是统一输入路由。
- **物理输入由 composition layer 显式接线**：输入回调取得同节点 / Context 的 `IUIUtility` 后调用 `Back()`；`false` 时是否退出、二次确认或交给玩法仍由项目决定。这样替换输入方案只改项目边缘，窗口调度与两个渲染后端都不变。
- Demo 提供 `DemoInputSystemBackKeyDriver` 活样板：用新 Input System 检测 Esc（Android 硬件返回键在 Unity 中同样表现为 Escape）并调用 `Back()`。它位于 `Game.Framework.Demo`，是教学用 composition 代码，不是 Framework Runtime Module。

> **边界深化（2026-08-27）**：初版曾把双路径 `MonoUIBackKeyDriver` 放进 `Game.Framework.UI`，并让 asmdef 无条件引用 `Unity.InputSystem`。`#if ENABLE_INPUT_SYSTEM` 只能裁 C# 分支，不能让缺少 Package 的 asmdef 引用自动消失；所谓“未安装时删引用即可”也会在启用新输入分支时使类型不可见。这既不是真正可选依赖，也把单个浅胶水抬成了 Core 成本。删除测试显示唯一真实消费者是 Demo，故保留深导航 Interface、把物理输入 Implementation 下沉，而不为一个 50 行实现新建假想 Adapter Module。

> **升级边界**：`MonoUIBackKeyDriver` 是已删除的 Runtime API；新的 Demo 样板使用独立脚本 GUID，不复用旧序列化身份。既有项目应显式移除旧组件，并从自己的 Input Action / 平台输入路由调用 `IUIUtility.Back()`；若仍需要逐帧 Esc 样板，再自行复制 Demo Implementation。宁可让旧场景暴露清晰迁移点，也不让公共 Runtime 组件静默变成 Demo-only 类型。

### 3. 安全区：opt-in 内容避让组件，层根保持全屏

层根**不**做安全区——背景/模态遮罩就该铺满整屏（含刘海区），只有交互内容需要避让（行业常规「背景出血、内容避让」）。各 adapter 提供 opt-in 组件：UGUI `UGuiSafeArea`（把所挂 RectTransform 锚进 `Screen.safeArea`，挂窗口内容根，逐帧值比较响应转屏）；Toolkit `SafeAreaContainer`（`[UxmlElement]`，按 safeArea 与屏幕差值经 `RuntimePanelUtils.ScreenToPanel` 换算设 padding，挂面板/几何变化事件重算）。窗口 prefab / UXML 自行决定哪层节点避让。

### 4. Toast / Loading：Top 层内置件，走「类型表注册」保持后端无关

`ShowToast(text, duration, ct)` / Loading 入口做成 **`IUIUtility` 一等方法**（不是 adapter 命名空间的扩展方法）——业务调用点完全后端无关，与 `Open<T>` 同一条铁律。实现机制：核心 `UIUtility` 构造时接收 `UIBuiltinWindows` **类型表**（Toast / Loading 的窗口 Type，由各 Mono 入口提供自家实现），内置件按表走非泛型 `OpenCore(Type, args, ct)` 开窗，Toolkit / UGUI 入口原样透传可选生命周期令牌，取消后未完成创建的内置件不得延迟出现；未注册类型表（如测试裸核心）报错提示不抛异常。Loading 最初的 `ShowLoading/HideLoading` 单 owner 开关仍保留兼容，推荐的并发安全入口与迁移见 ADR-0037。

内置窗口本体（每 adapter 一对，纯代码搭建、`Cache` 复用、落 `UILayer.Top`）：

- **Toast**（`UGuiToastWindow` / `ToolkitToastWindow`）：底部居中半透明文字条，整棵树不吃输入；超时自关（令牌链接 Context，销毁级联取消）；连续 Toast 复用同一实例——刷新文本、重置计时，**不做队列**（no-over-engineering，要队列的项目自包一层）。
- **Loading**（`UGuiLoadingWindow` / `ToolkitLoadingWindow`）：`Modal = true` 遮罩挡输入 + `BackClosable = false` 拦返回键；中央文本 + 旋转指示块（无美术资源的默认表现，正式项目通常用带资产的自定义 Loading 替代）；重复占用复用同一窗口并刷新文本，窗口所有权由 ADR-0037 的 lease 管理。

## Consequences

- ✅ 「动画期间挡输入」由框架一处保障，业务窗口只写动画本身（重写两个 hook），不用自己管防连点。
- ✅ 同步路径零变化：不用过渡的窗口行为与从前逐帧一致；既有 12 个窗口栈测试不改一行仍绿。
- ✅ 返回键语义符合手游直觉（先弹窗后页面），且可被强引导窗口拦截；项目只需把自己的返回 Input Action 映射到 `Back()`。
- ✅ `Game.Framework.UI` 不再引用 `Unity.InputSystem`；只用 UI Core 或任一渲染后端的项目不会因教学接线被迫安装输入 Package。
- ⚠ **逻辑关闭先于表现**：出场动画期间 `IsOpen<T>()` 已是 false、重开同类型会新建实例——动画中的旧实例只是视觉残影。依赖「关完才算关」的业务应改在 `OnClose` 里做收尾。
- ⚠ 过渡动画自己要响应传入的 ct（Context 销毁时取消）；不响应也只是白播几帧，物理对象已由 Teardown 拆除。
- ⚠ `Back()` 返回值从无到有（`void`→`bool`）：纯源码级变更，既有调用方不接返回值照常编译。
- ⚠ 输入接线不是 Framework Runtime 自动安装项；复制 Demo 样板或在项目既有输入路由里调用 `Back()`，不要在 UI Core 里重新轮询平台按键。
- §3 / §4 的实现跟进在 roadmap「UI 刚需补齐」项下追踪。
