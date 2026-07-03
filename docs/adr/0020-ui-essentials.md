# ADR-0020：UI 刚需补齐 —— 异步过渡 + Back 键 + 安全区 + Top 层常用件

**Status:** Accepted（§1 过渡 / §2 Back 键已实现，2026-07-03；§3 安全区 / §4 Toast·Loading 待实现）

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

### 2. Back / Esc：`Back()` 升级为逐层返回导航 + `BackClosable` opt-out + 驱动组件

- **`Back()` 语义升级**（`void` → `bool`）：按 **Popup → Window → Page** 从高到低找第一个非空层，检查栈顶窗口：
  - `BackClosable`（默认 true）→ 关闭它，返回 `true`；
  - `BackClosable = false` → 不动作但返回 `true`（back 已被 UI 消费——强引导/不可跳过流程用它拦住返回键）；
  - 三层全空 → 返回 `false`（业务据此做「再按一次退出」之类的兜底）。
  - `Top` / `System` 不参与（Toast/系统提示不是导航单元）、`Background` 不参与（底景）。
  - **过渡进行中 `Back()` 直接吞掉**（返回 `true` 不动作）——与「动画期间挡输入」同一语义，键盘路径不绕过挡板。
- **`[UIWindow(BackClosable = false)]`** 进窗口元数据。
- **驱动 = `MonoUIBackKeyDriver`**（核心 UI asmdef，渲染中立）：挂在 UI 入口（`MonoUGuiUI` / `MonoToolkitUI`）同一节点，Update 检测 Esc（Android 硬件返回键在 Unity 即 Escape）→ 调同节点 `IUIUtility.Back()`。做成独立组件而非内置进入口：要不要接返回键是项目决策（挂上即启用），两个入口也不必各写一份。
- **输入系统兼容**：代码 `#if ENABLE_INPUT_SYSTEM`（新输入 `Keyboard.current`）/ `#else`（旧 `Input.GetKeyDown`）双路径；asmdef 引用 `Unity.InputSystem`。复用到未装 Input System 包的项目时删这条引用即可（组件自动走旧输入分支）。

### 3. 安全区：opt-in 内容避让组件，层根保持全屏（待实现）

层根**不**做安全区——背景/模态遮罩就该铺满整屏（含刘海区），只有交互内容需要避让（行业常规「背景出血、内容避让」）。各 adapter 提供 opt-in 组件：UGUI `UGuiSafeArea`（把所挂 RectTransform 锚进 `Screen.safeArea`，挂窗口内容根）；Toolkit `SafeAreaContainer`（按 safeArea 与屏幕差值设 padding）。窗口 prefab / UXML 自行决定哪层节点避让。

### 4. Toast / Loading：Top 层内置件（待实现）

每 adapter 一个纯代码搭建（`Asset` 留空）的内置窗口 + `IUIUtility` 扩展方法（`ShowToast(text, duration)` / `ShowLoading(text)` / `HideLoading()`）。落 `UILayer.Top`；Loading 模态挡输入；Toast 不吃事件、自动超时关闭。具体接口形态实现时定。

## Consequences

- ✅ 「动画期间挡输入」由框架一处保障，业务窗口只写动画本身（重写两个 hook），不用自己管防连点。
- ✅ 同步路径零变化：不用过渡的窗口行为与从前逐帧一致；既有 12 个窗口栈测试不改一行仍绿。
- ✅ 返回键语义符合手游直觉（先弹窗后页面），且可被强引导窗口拦截；接入 = 挂一个组件。
- ⚠ **逻辑关闭先于表现**：出场动画期间 `IsOpen<T>()` 已是 false、重开同类型会新建实例——动画中的旧实例只是视觉残影。依赖「关完才算关」的业务应改在 `OnClose` 里做收尾。
- ⚠ 过渡动画自己要响应传入的 ct（Context 销毁时取消）；不响应也只是白播几帧，物理对象已由 Teardown 拆除。
- ⚠ `Back()` 返回值从无到有（`void`→`bool`）：纯源码级变更，既有调用方不接返回值照常编译。
- ⚠ 核心 UI asmdef 新增 `Unity.InputSystem` 引用；未装该包的复用项目删引用即可（`#if` 已把代码门住）。
- §3 / §4 的实现跟进在 roadmap「UI 刚需补齐」项下追踪。
