# AI Agent 接入 SSFramework

> 目标：让新的编码 Agent 能在不复制项目知识、不预埋易过期产品配置的前提下，快速承接 SSFramework 的开发、Unity 自动化与验证工作。本文是接入契约，不承诺任意 Agent 已经完成运行验证。

## 1. 稳定核心

无论使用哪个 AI，项目真值都在这些位置：

| 层 | 权威入口 | 用途 |
|---|---|---|
| 常驻规则 | 根与目标目录的 `AGENTS.md` | 安全边界、代码约束、目录就近规则 |
| 领域上下文 | `CONTEXT.md`、`docs/adr/` | 领域语言、已接受取舍、所有权与兼容边界 |
| 按需流程 | `.agents/skills/<name>/SKILL.md` | 架构、规则演进、Unity 后台自动化、截图与验证闭环 |
| Unity 工具契约 | `docs/unity-mcp-tips.md`、`docs/unity-cli-automation.md` | Editor、MCP、CLI、PlayMode 预检、超时与恢复语义 |
| 能力路线 | `docs/ai-game-development-capability-map.md` | 游戏开发能力缺口、成熟度、候选工具与采用条件 |
| 确定性门禁 | 测试、编译器约束、项目脚本和 Editor Seam | 不依赖 Agent 是否“记得”的行为契约 |

`AGENTS.md`、[Agent Skills](https://agentskills.io) 和 [MCP](https://modelcontextprotocol.io) 是公共结构；产品专属规则、Skill 副本或工具配置不是项目真值。

## 2. 最小接入能力

一个 Agent 不必拥有和 Codex 完全相同的 UI、Subagent、Hook 或权限系统。能稳定完成以下动作，就可以开始接入：

1. 读取、搜索和修改仓库文本文件，并显示可审查 diff。
2. 运行项目允许的 PowerShell / CLI 命令并返回完整退出码与关键输出。
3. 能显式读取根规则、目标目录规则和匹配的 Project Skill；原生自动发现不是硬要求。
4. 修改场景、Prefab 或运行 Unity 时，能连接项目认可的 Unity MCP / CLI；没有该能力时明确缩小任务范围。
5. 视觉任务能查看实际截图；否则不得声称已完成视觉验收。
6. 尊重用户授权、现有脏工作区、第三方 Package 和外部账号边界。

## 3. 通用启动提示

当新 Agent 第一次进入仓库，可以直接给它下面这段提示，不需要先为该产品提交配置：

```text
在修改任何内容前：
1. 完整读取根 AGENTS.md，以及本次目标文件路径上的所有嵌套 AGENTS.md。
2. 读取 CONTEXT.md，并只读取与任务相关的 ADR / guide。
3. 枚举 .agents/skills/*/SKILL.md 的 name 和 description；若任务命中某个 Skill，先完整读取其正文与正文要求的相关资源。
4. 说明你当前具备的文件、Shell、Unity、图像查看和外部服务能力；不要把格式支持当成工具已经接通。
5. 先报告任务范围、现有脏文件和最小验证方案，再在已授权范围内执行。
```

如果该 Agent 原生支持分层 `AGENTS.md` 或 `.agents/skills`，可以依赖原生发现；仍应在第一次真实任务中核对实际加载结果。

## 4. 接入顺序

### A. 规则与知识

1. 让 Agent 复述一条根规则和一条目标目录规则，并给出来源路径。
2. 让 Agent 找到与任务匹配的 Project Skill，读取完整正文，而不是只看到 name / description。
3. 让 Agent 区分 `CONTEXT.md` 的稳定词义、ADR 的取舍和代码/测试的当前事实；冲突时报告，不自行挑选方便的一份。

### B. 工具

1. 先做文件搜索、Git 状态和编译入口等只读检查。
2. 若需要 Unity，连接当时可用的项目 MCP / CLI，并按 `docs/unity-mcp-tips.md` 核对工具名和返回语义。
3. 第一次 Unity smoke 只读取 Editor 状态、当前场景和测试列表，不修改场景。
4. 只有在只读 smoke 成立后，才进行用户已授权的小范围修改与验证。

### C. 验证

1. 使用 `unity-validation-harness` 按改动风险选择证据，不另造一套 Runner。
2. PlayMode 前执行项目预检；测试结果必须确认集合非空，不能把 `total = 0` 当成功。
3. UI、Scene、Shader 或 EditorWindow 改动调用 `unity-screenshot`，并实际检查图片。
4. 记录编译、测试、运行状态、截图/性能证据、清理结果和未覆盖风险。

完成一条代表性小任务后，才能说该 Agent 对这一类任务“已接入”；不能一次 smoke 后宣称支持所有 Unity 开发。

## 5. 产品薄适配策略

只有确定要实际使用某个 Agent 时，才按它**当时的官方文档**增加适配：

- 优先让产品直接读取 `AGENTS.md`、`.agents/skills` 和 MCP。
- 不原生支持时，用 import、link、启动提示或极薄路由指向公共真值；不复制完整正文。
- 产品配置只解决发现、工具连接或权限映射，不引入第二套架构规则。
- 机器路径、Token、登录态、付费套餐和个人权限保留在用户级；仓库只提交无秘密且团队确实要复用的配置。
- 记录产品版本、接入日期、已验证任务类型和卸载方法。
- 产品升级后若已原生支持公共入口，删除旧适配；停用产品时也删除无实际消费者的配置。

不要为了目录对称同时维护 `.codex`、`.claude`、`.cursor`、`.gemini` 和 `.github` 副本，也不要要求每次变更在所有 Agent 上重复验证。

## 6. Handoff 最小包

从一个 Agent 转交给另一个 Agent 时，至少提供：

```text
目标：本次要达到的可观察结果
范围：允许修改 / 不得修改的目录、资产和外部系统
真值：相关 AGENTS、CONTEXT 条目、ADR、Skill 和 guide
工作区：分支、已有脏文件、哪些改动属于用户
当前状态：已完成、进行中、阻塞点和失败证据
验证：已跑命令、测试数、结果、截图/性能产物和未覆盖风险
下一步：最小可继续动作与停止条件
```

不要只转交聊天摘要；关键决定应已经落在 ADR、guide、测试或代码中，临时状态才放 Handoff。

## 7. 当前项目状态

- Codex 是当前主要且完成 Unity 工作流验证的 Agent。
- 根 `CLAUDE.md` 仅保留 `@AGENTS.md` 导入，作为低成本兼容入口；Claude 的 Project Skill 自动发现等到实际启用时再按届时能力适配。
- 其他 Agent 没有仓库级预配置，也不宣称已接入；可以从本文和通用启动提示开始承接。
- 新 Agent 的接入结果和长期策略更新到 `docs/ai-collaboration-guide.md`，不写进多份产品说明。
