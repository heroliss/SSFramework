# ADR-0033：把 UGUI / 相机内容嵌进 UI Toolkit —— RenderTexture 桥

**Status:** Accepted（2026-07-13）

## Context

UGUI 与 UI Toolkit 是两套独立渲染系统，谁都不能当对方 hierarchy 里的子节点。可实际开发常要把一段 UGUI/TMP（或 3D 道具预览、小地图、相机画面）放进一张 UI Toolkit 面板的**内容流**里。

demo 里现有两处「UGUI 嵌在 Toolkit」其实都是**伪嵌入**——`DemoPoolAssets.BindAnchor`（对象池右栏）、`FontsDemoModule` 的 TMP 浮层：用一个 `ScreenSpaceOverlay` 的 UGUI Canvas，每帧把 `RectTransform` 对齐到 Toolkit 占位元素的 `worldBound`。它简单，但**浮在整个面板之上**：

- 不能被后来的 Toolkit 内容裁剪 / 遮挡、不能随 `ScrollView` 滚动（它不在 Toolkit 的绘制流里）。
- `worldBound` 在面板拖到极窄时会退化成 `NaN`，得专门防（灌进 `RectTransform.sizeDelta` 会让布局反复重算卡死）。

调研结论（2026-07）：**没有现成成熟第三方包**做「把活的 UGUI/TMP 嵌进 UI Toolkit 内容流」。业界公认且唯一的做法是 **RenderTexture 桥**——一台专用相机把 UGUI 渲进 `RenderTexture`，纹理经 `Background.FromRenderTexture` 当 Toolkit 元素背景显示。第三方只封装了「设 RT 当背景」这半（`Background.FromRenderTexture` 的便捷包装），**难的一半**（专用相机 + 隔离层 Canvas + RT 生命周期 + 随布局同步尺寸 / DPI + 刷新策略）人人手搓。而且 **事件不穿透 RenderTexture** 是公认硬骨头（要坐标翻译 + 假 raycaster），没人打包。项目至今零 `RenderTexture` 使用，是全新接缝。

## Decision

### 1. RenderTexture 桥是把 UGUI/相机内容放进 Toolkit 内容流的正法；overlay-align 伪嵌入保留给「就要盖在最上层」

「真嵌入」（纹理是 Toolkit 真内容，能被裁剪 / 滚动 / 遮挡）走 RenderTexture 桥；overlay-align 那套不废弃——它对「一直盖在最上层、不需要被 Toolkit 裁剪」的场景更省（无相机 / RT 开销）。按「要不要被 Toolkit 裁剪」二选一。

### 2. 分层：后端无关的显示 + 相机接缝，落 `Game.Framework.UI.Toolkit`

- **`RenderTextureElement : VisualElement`**（`[UxmlElement]`）：显示一张 RenderTexture，`GeometryChangedEvent` 里按「内容框 × 面板→屏幕缩放」算清晰所需设备像素；超过最长边预算时统一比例降采样，低清只损失清晰度、不改变宽高比；经 `DesiredPixelSizeChanged` 上报，**不拥有**纹理 / 相机（照 `SafeAreaContainer` 约定，无逐帧轮询）。供 UGUI 嵌入、3D 预览、小地图共用。
- **`CameraTextureRenderer`**（纯 C#）：相机 → RenderTexture 生命周期，`Resize` 幂等（同尺寸不重建）、`Render` 按需、`Dispose` 释放显存 + 对象。后端无关（相机是引擎类型）。
- 尺寸换算与重建判定抽成纯静态函数，`UIEmbedTests` 覆盖（不触 GPU）。

### 3. 一键 UGUI 嵌入落新模块 `Game.Framework.UI.Bridge`（可整块删）

`MonoUGuiEmbed`：给一个 UGUI 面板 prefab，自建一台**隔离层**透明背景相机 + `ScreenSpaceCamera` Canvas（`worldCamera` 指向该相机），把面板渲进 RT，`Bind` 到 `RenderTextureElement` 显示；纹理尺寸随元素布局自动同步；托管 `CanvasScaler` 以 Toolkit 内容框为稳定逻辑分辨率，让 RT 尺寸只控制采样清晰度、不会触发低像素重新排版；`EveryFrame` / `OnDemand` 两档刷新；解绑 / 销毁释放。

- 模块 `references` = `Game.Framework.UI.Toolkit` + **引擎 UGUI**（`UnityEngine.UI`，`overrideReferences:false` 自动可见）——它桥的是**原生 UGUI → Toolkit**，不耦合框架的 `UI.UGui` 模块，故不引用它。`autoReferenced:false`、可整块删除，同 `Game.Framework.Network.Proto` / `Game.Framework.Asset.Yoo` 先例（第三方 / 后端特化接缝单独开 asmdef 隔离）。
- **隔离层**：托管 Canvas + 内容置于一个专用 layer（demo 用 `UGuiEmbed`），专用相机只拍此层、主相机剔除此层——否则嵌入内容会同时漏进游戏画面。这是接入方要在工程 Tags & Layers 预留的一步。

### 4. 输入穿透：v1 只读、v2 加指针（受控场景可解，见文末增补）

v1 只读显示——覆盖 TMP 富文本、3D 道具预览、小地图等绝大多数场景。v2 补上**指针输入穿透**：「事件不穿透 RT」是**通用**方案的硬骨头（任意 raycaster / world-space…没人打包），但**自控相机 + 画布的受控场景**里可解（详见文末「增补 · v2」）。

### 5. 刻意不做

- **文本输入 / IME、多点触控**：成本陡增、嵌入场景罕见——要在嵌入 UGUI 里打字直接用原生 UGUI 层（v2 输入穿透只做指针）。
- **可配置的 CanvasScaler 档位**：桥内部固定使用 `ScaleWithScreenSize + MatchWidthOrHeight(0.5)`，以 Toolkit 内容框作为参考分辨率，专门隔离逻辑布局与 RT 采样密度；不再向业务暴露 match 模式、参考分辨率等第二套布局策略，避免 Bridge 和 Toolkit 同时争夺构图规则。
- **自动预留隔离层 / 自动配主相机**：layer 是工程级共享资源（32 个上限），由接入方显式预留 + 配相机剔除，工具只在缺层时告警，不擅改工程 layer 表。

## Consequences

- 有了与 overlay-align 对照的「真嵌入」通路：纹理是 Toolkit 真内容，能被 `ScrollView` 裁剪 / 滚动、被后续元素遮挡。
- 后端无关件（`RenderTextureElement` + `CameraTextureRenderer`）在 `UI.Toolkit`，也能拍 3D 道具预览 / 小地图；UGUI 特化的一键装配在可删的 `UI.Bridge`，删掉不影响两个 UI 后端（它们互不引用、也不引用 Bridge）。
- 核心逻辑（尺寸换算、重建判定、低预算等比降采样、输入坐标）由 `UIEmbedTests` 13 例覆盖；渲染管线经 demo Play 实测：`908×190` 内容在 128px 最长边预算下得到 `128×27` RT，Canvas 仍以约 `908×190` 的逻辑尺寸排版，画面只变糊不变形；低清交互 RT 的手动 Raycast 仍命中目标按钮。
- 五件套齐：本 ADR / 接缝（`UI.Toolkit/RenderTextureElement.cs` + `CameraTextureRenderer.cs`）+ 模块（`UI.Bridge/MonoUGuiEmbed.cs`）/ 测试（`UIEmbedTests`）/ demo「UI 融合 · UGUI 嵌进 Toolkit」章（`Modules/UIEmbedModule.cs`）/ guide §27 + AGENTS #33。
- RenderTexture 从「项目零使用」变成「收口在 UI 嵌入桥后」的一等接缝，延续 `IAssetProvider` 隔离 YooAsset 的一贯做法。

## 增补 · v2（2026-07-13）：输入穿透 + 内容泛化

v1 落地后做了两处增强（用户驱动）：

### 输入穿透（指针）
`MonoUGuiEmbed.Interactive` 开关 + `UGuiEmbedInputForwarder`（`UI.Bridge`，因需 UGUI + Toolkit 双依赖）。手动驱动，不走全局 EventSystem 的屏幕路由（它按真实鼠标位置走、够不到离屏 RT）：
- 托管 Canvas 挂一个 **`enabled=false` 的 `GraphicRaycaster`**——`enabled=false` 让全局 `InputSystemUIInputModule`（本项目新输入系统）**不发现它**、不会拿真实鼠标坐标误射这块离屏画布；但 `Raycast()` 只停自动注册、仍可手动调（**已实测**：禁用的 raycaster 手动 Raycast 命中正常）。
- `RenderTextureElement` 交互时 `pickingMode=Position`，转发器把元素内指针坐标翻成 **RT 空间屏幕点**（`x=u·rtW`、`y=(1-v)·rtH` 翻 y），构造 `PointerEventData`（复用场景 EventSystem）手动 `Raycast` + `ExecuteEvents` 分发。
- 全指针状态机：enter/exit、down/up + 同目标判 click、**拖拽**（超 `pixelDragThreshold` 触发 beginDrag→drag→endDrag，拖拽期捕获指针）、**滚轮**。文本输入 / IME、多点触控不做。
- 坐标换算与拖拽阈值抽纯静态函数进 `UIEmbedTests`；渲染 + 输入经 demo Play **头less实测**（向按钮 RT 位置手动 Raycast 命中 → click 计数 0→1；Slider 拖拽 0→1）。

### 内容泛化（不止 prefab）
`EnsureContentRoot()` 暴露托管 Canvas 供 **code-built / 动态** UGUI 内容挂入（如运行时搭的 TMP 样本）；`Bind` 时对托管 Canvas 子树重跑 `SetLayerRecursive`，解决「后加内容不在隔离层」。

### demo 消费
UI 融合章加**可交互嵌入**（UGUI 计数 + 按钮 + Slider，点 / 拖穿透 RT 生效）；**字体章** TMP 样本卡从 ScreenSpaceOverlay 浮层 retrofit 为**内联嵌入**（经 `EnsureContentRoot`，随章滚动、化解「TMP 塞不进 Toolkit 只能作浮层」的张力）。对象池的 overlay-align 伪嵌入**保留**（那章刻意教它），仅加一句指路本桥。
