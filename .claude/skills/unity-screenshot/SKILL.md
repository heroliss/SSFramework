---
name: unity-screenshot
description: Unity 编辑器内截图 / screenshot / scene view 截屏 / 给 UI 拍图给 AI 看。用 MCP manage_camera 截图，输出到项目根 Screenshots/。当用户要求"截图"、"screenshot"、"看看 UI"、"拍一下场景"、"capture scene"时触发。
---

# Unity 截图（MCP `manage_camera`）

截图工具是 **`manage_camera action="screenshot"`**（批量用 `screenshot_multiview`）。关键参数：

| 参数 | 说明 |
|---|---|
| `capture_source` | `"scene_view"`（Scene 视口）或 `"game_view"`（默认，游戏/相机路径） |
| `output_folder` | 输出目录，**项目相对**或项目内绝对路径。本项目固定用 `"Screenshots"`（= 项目根 `Screenshots/`，已 gitignore，不污染 Assets/版本控制） |
| `include_image` | `true` 时直接内联返回 base64 PNG（多模态），无需再用 Read 读文件 |
| `max_resolution` | 内联图最长边像素，默认 640 |
| `screenshot_file_name` | 文件名，默认时间戳 |
| `view_target` / `view_position` / `view_rotation` | 定位截取：game_view 下对准目标；scene_view 下把 Scene 视口框到目标 |
| `batch` | `"surround"`（6 角）/ `"orbit"`（网格）多视角接触表 |

## 关键约束（edit 模式实测，2026-05 仍成立）

- **Editor 非播放模式下 `game_view` 全白/全黑**：URP 项目常见全白（实测当前 Demo 场景 edit 模式 game_view 返回纯白；工具即便不指定 camera 也会自动选 Main Camera）。**edit 模式截 UI/场景一律用 `capture_source="scene_view"`。**
- **截 UI 前先 `scene_view_frame`**：`manage_scene action=scene_view_frame scene_view_target=Canvas` 把 Scene 相机框到 UI 根，再截图；否则可能截到空区域。也可用 screenshot 的 `view_target` 一步到位。
- **输出路径固定 `output_folder="Screenshots"`**（项目根，已 gitignore）。不要再写 `Assets/Screenshots/`（已移出 Assets）。
- **截图前确认目标**：用户没说截哪里就先问（整个 Scene / 某 GameObject / 某相机视角）。
- **Editor 模式手动改 UI 后不立即重绘**：execute_code 改 TMP.text 或调 `Render()` 后 UI 可能"看起来没变"，是 LayoutGroup 刷新延迟。末尾补 `Canvas.ForceUpdateCanvases()` + `LayoutRebuilder.ForceRebuildLayoutImmediate(...)` + `SceneView.RepaintAll()`，或进 PlayMode（PlayMode 自动刷新）。

## 待验证（更新后的 MCP 新行为，尚未 PlayMode 实测）

`manage_camera` 文档称：**不指定 `camera` 时默认走 ScreenCapture API，能截到 Screen Space-Overlay UI**；指定 camera 才排除 Overlay。这与"edit 模式 game_view 截不到 UI"的旧结论可能在 **PlayMode** 下不同——**真正需要带 UI 的 game_view 截图时，进 PlayMode 实测一次**：若 PlayMode + game_view + 不指定 camera 能拿到含 Overlay UI 的彩图，则更新本节、把 UI 截图主路径从 scene_view 切到 game_view。在确认前，UI 截图仍以 scene_view 为准。

## 流程

1. 必要时先 `find_gameobjects` / `manage_scene scene_view_frame` 定位、聚焦目标
2. `manage_camera action="screenshot" capture_source="scene_view" output_folder="Screenshots" include_image=true`
3. 看内联返回的图确认结果（或 Read 项目根 `Screenshots/<file>`）
4. 把观察反馈给用户

## 常见错误

- ❌ edit 模式用 `game_view` → 全白/全黑，看不到 Overlay UI（PlayMode 可能不同，见"待验证"）
- ❌ Scene 相机不在目标位置就直接截 → 空场景，先 `scene_view_frame` 或传 `view_target`
- ❌ edit 模式手动 `Render()` 后立刻截 → UI 未刷新
- ❌ 输出到 `Assets/` 内 → 被 Unity 导入为纹理且可能入库；用 `output_folder="Screenshots"`（项目根）
- ❌ 不看回图就汇报"截好了" → 内容不对没发现

## 触发关键词

截图 / 截屏 / 拍图 / 看一下 UI / 看看场景 / screenshot / capture / show me the scene
