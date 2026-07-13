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

- **`RenderTextureElement : VisualElement`**（`[UxmlElement]`）：显示一张 RenderTexture，`GeometryChangedEvent` 里按「内容框 × 面板→屏幕缩放」算清晰所需设备像素、经 `DesiredPixelSizeChanged` 上报，**不拥有**纹理 / 相机（照 `SafeAreaContainer` 约定，无逐帧轮询）。供 UGUI 嵌入、3D 预览、小地图共用。
- **`CameraTextureRenderer`**（纯 C#）：相机 → RenderTexture 生命周期，`Resize` 幂等（同尺寸不重建）、`Render` 按需、`Dispose` 释放显存 + 对象。后端无关（相机是引擎类型）。
- 尺寸换算与重建判定抽成纯静态函数，`UIEmbedTests` 覆盖（不触 GPU）。

### 3. 一键 UGUI 嵌入落新模块 `Game.Framework.UI.Bridge`（可整块删）

`MonoUGuiEmbed`：给一个 UGUI 面板 prefab，自建一台**隔离层**透明背景相机 + `ScreenSpaceCamera` Canvas（`worldCamera` 指向该相机），把面板渲进 RT，`Bind` 到 `RenderTextureElement` 显示；纹理尺寸随元素布局自动同步；`EveryFrame` / `OnDemand` 两档刷新；解绑 / 销毁释放。

- 模块 `references` = `Game.Framework.UI.Toolkit` + **引擎 UGUI**（`UnityEngine.UI`，`overrideReferences:false` 自动可见）——它桥的是**原生 UGUI → Toolkit**，不耦合框架的 `UI.UGui` 模块，故不引用它。`autoReferenced:false`、可整块删除，同 `Game.Framework.Network.Proto` / `Game.Framework.Asset.Yoo` 先例（第三方 / 后端特化接缝单独开 asmdef 隔离）。
- **隔离层**：托管 Canvas + 内容置于一个专用 layer（demo 用 `UGuiEmbed`），专用相机只拍此层、主相机剔除此层——否则嵌入内容会同时漏进游戏画面。这是接入方要在工程 Tags & Layers 预留的一步。

### 4. v1 只读显示，输入穿透显式推迟

事件不从 Toolkit 传进 RT 里的 UGUI。v1 定位「只读显示」——覆盖 TMP 富文本、3D 道具预览、小地图等绝大多数场景。要交互的 UI 仍用 Toolkit / UGUI 各自原生事件。输入转发（坐标翻译 + 假 raycaster）留待真有需求再上，不猜测性预留复杂接缝（[[feedback-no-over-engineering]]）。

### 5. 刻意不做

- **输入穿透**（见上）。
- **CanvasScaler / 复杂缩放策略**：托管 Canvas 走 `ScreenSpaceCamera` 贴合 RT 像素，内容 prefab 自己定锚点填充即可；不引入 CanvasScaler 档位。
- **自动预留隔离层 / 自动配主相机**：layer 是工程级共享资源（32 个上限），由接入方显式预留 + 配相机剔除，工具只在缺层时告警，不擅改工程 layer 表。

## Consequences

- 有了与 overlay-align 对照的「真嵌入」通路：纹理是 Toolkit 真内容，能被 `ScrollView` 裁剪 / 滚动、被后续元素遮挡。
- 后端无关件（`RenderTextureElement` + `CameraTextureRenderer`）在 `UI.Toolkit`，也能拍 3D 道具预览 / 小地图；UGUI 特化的一键装配在可删的 `UI.Bridge`，删掉不影响两个 UI 后端（它们互不引用、也不引用 Bridge）。
- 核心逻辑（尺寸换算、重建判定）纯函数可测，`UIEmbedTests` 8 例绿，不依赖场景 / 帧推进 / GPU；渲染管线经 demo Play 头less验证（相机把 UGUI 渲进 RT、整幅非透明、中心像素 = 面板底色）。
- 五件套齐：本 ADR / 接缝（`UI.Toolkit/RenderTextureElement.cs` + `CameraTextureRenderer.cs`）+ 模块（`UI.Bridge/MonoUGuiEmbed.cs`）/ 测试（`UIEmbedTests`）/ demo「UI 融合 · UGUI 嵌进 Toolkit」章（`Modules/UIEmbedModule.cs`）/ guide §27 + AGENTS #33。
- RenderTexture 从「项目零使用」变成「收口在 UI 嵌入桥后」的一等接缝，延续 `IAssetProvider` 隔离 YooAsset 的一贯做法。
