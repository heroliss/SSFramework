---
name: unity-background-automation
description: 在不抢用户焦点的前提下运行或监控 Unity MCP 测试、PlayMode 与编辑器自动化，并诊断 editor_unfocused、后台停顿或必须前台交互的例外。用于用户希望 Unity 后台工作、测试 job 看似卡住，或需要判断是否应升级到 Windows UI 控制时。
---

# Unity 后台自动化

默认让 Unity 留在后台。实例选择、PlayMode 保存预检和工具限制见 `docs/unity-mcp-tips.md`。

## 测试 job

1. 确认 Editor 不在 Play / 编译中；PlayMode 测试先执行菜单
   `SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）`。
2. 创建一次测试 job，保存 job id；用 30–60 秒服务端等待轮询同一个 job。不要因为启动阶段
   `total=0` 就重跑或 `clearStuck`。
3. `blockedReason: editor_unfocused` 在当前 AnkleBreaker Unity MCP 中只是
   `InternalEditorUtility.isApplicationActive == false` 的状态标签，不是 Test Runner 门禁。只要 total、completed、
   currentTest、Console 里程碑或编译/域重载状态在变化，就继续后台等待，不激活 Unity。
4. Unity Test Framework 会在 EditMode / PlayMode 测试事务中临时启用
   `Application.runInBackground`，并把 Editor Interaction Mode 临时切为 `NoThrottling`；结束后均恢复。测试不需要持续占用
   前台；需要真实输入焦点语义的用例除外。不要把 `NoThrottling` 永久写进用户设置，那只会增加后台 CPU 与功耗。
5. 连续约 120 秒没有任何进度时，先查编译、域重载、当前测试耗时、Console 与场景保存状态。只有这些证据都不解释停顿，
   才把焦点切换当作一次诊断，不把它当成固定流程；仍轮询原 job，确认真实失败后才清理或重跑。

## 普通 Play、构建与观察

- 场景/组件/资产/菜单/Console/构建、Game 或 Scene 捕获、指定 EditorWindow 的 `PrintWindow` 截图通常都不依赖前台。
- 普通 Play 的 PlayerLoop 是否后台继续由项目设置与运行时 `Application.runInBackground` 决定；不要为通用框架静默改写产品设置。
- 同一工程已由交互式 Editor 打开时，不能再用 `Unity -batchmode -projectPath` 启动第二实例。需要完全隔离时使用独立工程/
  worktree/CI，但要明确它测试的是哪份提交或同步快照。

## 只有这些场景升级到前台

- Windows 原生模态框、文件选择器、凭据/授权窗口或崩溃对话框；优先通过预检避免它们出现。
- 明确验证真实键盘、鼠标、Game View 输入焦点、拖拽、Docking、Tooltip、右键/下拉临时弹层的交互。
- MCP 主线程队列已被原生模态框阻塞，语义工具无法再执行。

若确实只需让 Unity 获得一次 OS 焦点，优先使用“按 `unity_editor_ping` 返回的 PID 精确激活一次”的窄 Windows Adapter；
不要移动鼠标、按坐标点击、循环抢焦点或在整个测试期间保持前台。任何 CLI/小程序最终仍调用操作系统前台窗口 API，
它只能让 fallback 更窄、更可诊断，不能把焦点切换变成无干扰操作。

插件升级后若 job 语义变化，先检查已安装 Package 的 `MCPTestRunnerCommands.SerializeJob`，再更新本 Skill；不得直接修改
`Library/PackageCache`。
