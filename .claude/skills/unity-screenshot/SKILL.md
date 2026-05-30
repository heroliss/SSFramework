---
name: unity-screenshot
description: Unity 编辑器内截图 / screenshot / scene view 截屏 / 给 UI 拍图给 AI 看。用 ab-unity-mcp 的 unity_screenshot_game（Game 视图，Play 模式能抓 UI Toolkit）或 unity_screenshot_scene（Scene 视口）。当用户要求"截图"、"screenshot"、"看看 UI"、"拍一下场景"、"capture scene"时触发。
---

# Unity 截图（ab-unity-mcp）

- `unity_screenshot_game` — Game 视图（相机 + 运行时 UI，含 UI Toolkit）。**须先进 Play**：Edit 模式 Game 视图只有相机背景。
- `unity_screenshot_scene` — Scene 视口，不依赖运行。

参数：`path` 用项目根 `D:/SSFramework/Screenshots/x.png`（别进 `Assets/`，会被导入为纹理）；`superSize` 文字多用 `2`；`port` 必传当前实例。

要点：
- 运行时 UI：`unity_play_mode play` → 截图 → `unity_play_mode stop`（这些操作不断连，只有重编译才断）。
- 截图下一帧才落盘：调用后先 `unity_editor_ping` 再 Read 文件；没写出就重发一次。
- 用 Read 看 PNG，看完反馈观察，别不看就说"截好了"。
- 截滚动区下方内容：先 `unity_execute_code` 设 `ScrollView.scrollOffset`。execute_code 不能 import，`Q` 要写全静态：`UQueryExtensions.Q<...>(root, null, "类名")`。

实例选择 / 端口见 `docs/unity-mcp-tips.md`。
