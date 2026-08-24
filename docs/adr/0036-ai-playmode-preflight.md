# ADR-0036：AI PlayMode 预检 —— 显式保存有路径脏场景，不以全局 Hook 劫持人工 Play

**Status:** Accepted（2026-08-23）

## Context

交互式 Unity Editor 从有未保存改动的场景启动 PlayMode 测试时，会先显示原生保存弹窗。ab-unity-mcp 的命令经 Unity 主线程执行；弹窗一旦出现，测试 job 停在“运行中、发现 0 条测试”，其后的工具发现、状态查询和菜单命令也会排队阻塞。Unity MCP 可以在弹窗出现前保存场景，却不是独立桌面输入通道，无法可靠点击已经出现的原生模态窗口。

这个问题在真实 Outpost 玩家路径 PlayMode 冒烟测试中连续复现。仅靠 Agent 记住“先保存”不够稳定；把它做成 Claude/Codex 各自的 Hook 又会产生两套配置，并且 Hook 离 Unity 场景状态的事件源更远。

## Decision

在 `Game.Framework.Editor` 增加 `FrameworkAutomationPreflight`：

- 稳定 Interface 是公开静态方法 `PreparePlayModeTests()` 与菜单路径 `SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）`；MCP、其他 Agent 和人工菜单都可调用。
- Implementation 只遍历已加载场景，保存其中“脏且已有资产路径”的场景；输出 `[SSFramework.Automation] READY` 与实际路径，随后由调用方启动原有测试工具。
- 在任何写入前验证所有脏场景都有资产路径。未命名场景、Editor 正在编译/刷新、处于或正在进入 PlayMode、保存失败都 fail-fast，并输出可诊断的 `BLOCKED`；不猜路径、不丢弃改动、不制造另一个弹窗。
- 不注册全局 PlayMode 自动保存回调。人工直接点击 Play 或 Test Runner 时仍保留 Unity 的确认行为；只有自动化显式选择预检才保存。
- 不修改 ab-unity-mcp / Unity Test Framework 第三方包。项目代码是稳定 Seam，工具升级或更换 AI 客户端时仍可复用。
- 根 `AGENTS.md` 只保留“PlayMode MCP 测试先调用预检”的固定时机路由；具体命令、恢复方式与 MCP 限制放在 `docs/unity-mcp-tips.md`。

## Consequences

- PlayMode 自动化从“弹窗出现后用易碎坐标点击恢复”改为“进入前建立无弹窗前置条件”；Windows UI 自动化只作为已阻塞现场的恢复手段。
- 自动保存本身是显式副作用，调用日志列出保存路径，代码审查可追溯；未命名工作不会被擅自命名或覆盖。
- 预检与测试运行仍是两个步骤。这样保留 MCP 测试 job 的过滤、轮询和结果能力，也避免项目重新实现一套 Test Runner。
- `Game.Framework.Editor.Tests` 用真实临时场景覆盖成功保存和未命名场景拒绝，并验证“批次中含未命名场景时，有路径的脏场景也保持未保存”。每个用例创建带 GUID 的独占目录，TearDown 只删除明确持有的目录，不会误删用户预先存在的同名资产。
- 删除 `Game.Framework.Editor` 后只失去 Editor 自动化体验，不影响 Runtime Framework 或玩家构建，符合 Editor Module 的删除测试。
- 验收实测：先经菜单预检，再由 MCP 启动全量测试；2026-08-24 当前基线 EditMode 96/96、PlayMode 418/418，且没有再次出现保存弹窗或 0-test 队列卡死。
