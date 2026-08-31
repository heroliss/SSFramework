---
name: unity-screenshot
description: 通过项目的 Unity MCP 捕获 Game、Scene 或指定 EditorWindow 并实际检查图片。用于截图、查看运行时 UI、检查自定义编辑器窗口或为 UI/Scene 调试提供视觉证据；它负责取证，不把单帧截图当作自动视觉回归。
---

# Unity 截图

使用 AnkleBreaker Unity MCP；实例选择、端口与项目注意事项见 `docs/unity-mcp-tips.md`。

## 选择视图

- `unity_screenshot_editor_window`：按窗口标题或类型捕获 Inspector、Console 和自定义 EditorWindow；底层使用
  Win32 `PrintWindow`，窗口被遮挡时也可截取，且不抢焦点。检查框架诊断/配置窗口时优先使用它，而不是先切到
  Windows 界面控制。
- `unity_screenshot_game`：相机与运行时 UI（含 UI Toolkit）。需要先进入 Play；Edit 模式通常只有相机背景。
- `unity_screenshot_scene`：Scene 视口，不要求 Play。

`port` 必须传当前 SSFramework 实例。输出放项目根 `Screenshots/<meaningful-name>.png`，不要放进 `Assets/` 触发纹理导入；文字密集时可用 `superSize: 2`。

`unity_screenshot_editor_window` 负责观察，不提供通用点击、拖动或缩放。优先用 Unity MCP 菜单与
`unity_execute_code` 完成可表达的 Editor 操作；确需对任意原生控件做坐标交互时，再使用 Windows 界面控制。

## 流程约束

1. 查询 Editor 状态和实例；需要 Game 视图时进入 Play。捕获 EditorWindow 时先确认目标窗口已打开，并传可唯一匹配的窗口标题或类型。
2. 调用截图工具。截图可能下一帧才落盘，先 ping/查询 Editor 再检查文件；未生成时最多重试一次并报告工具状态。
3. 用当前环境的本地图像查看能力实际打开 PNG，依据画面反馈观察；不能只说“已截图”。
4. 如果本次由 Skill 临时进入 Play，检查完后退出 Play；用户明确要求保持运行时除外。

滚动区下方内容可经 `unity_execute_code` 调整 `ScrollView.scrollOffset`。execute code 不能写 import；查询元素时使用完整静态调用，例如 `UQueryExtensions.Q<...>(root, null, "class-name")`。

不得为了截图手改 `.unity` / `.prefab` YAML。需要搭场景或调整组件时遵循根 `AGENTS.md`，先确认不在 Play，再用 Unity MCP 修改并保存。
