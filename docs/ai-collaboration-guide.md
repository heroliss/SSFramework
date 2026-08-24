# AI 协作方案

> 面向项目维护者：说明 SSFramework 如何让 Claude Code、Codex 等编码 Agent 共享项目规则，又如何隔离各工具自己的 Skill、Hook、Subagent 与权限配置。能力信息最后核验于 **2026-08-25**。

## 1. 当前布局

```text
SSFramework/
├── AGENTS.md                         # 项目常驻约束，Codex 等工具直接读
├── CLAUDE.md                         # @AGENTS.md，Claude 的同源入口
├── Assets/Game/
│   ├── AGENTS.md                     # Framework 业务使用约束
│   ├── CLAUDE.md                     # @AGENTS.md
│   └── Framework/
│       ├── AGENTS.md                 # Framework 内部实现约束
│       ├── CLAUDE.md                 # @AGENTS.md
│       └── Demo/Scripts/Modules/
│           ├── AGENTS.md             # Demo 教学章节约束
│           └── CLAUDE.md             # @AGENTS.md
├── .agents/skills/                   # 跨工具 Skill 的权威正文（当前 2 个）
├── .claude/skills/                   # Claude 发现入口，路由到 .agents 正文
├── .mcp.json                         # Claude MCP 项目配置（当前为空）
└── docs/
    ├── framework-guide.md            # 完整教程与 API 心智模型
    ├── framework-module-map.md       # Module / Interface / Seam / 删除测试
    └── adr/                          # 关键设计决策与演进历史
```

当前没有团队共享的 `.claude/settings.json`、`.claude/agents/` 或 `.codex/`。不要把“工具支持某能力”误写成“本项目已经配置该能力”。

## 2. 设计原则

### 2.1 同一条项目规则只维护一次

项目规则的权威内容写在 `AGENTS.md`；同目录 `CLAUDE.md` 只做 `@AGENTS.md` 导入。不要复制两份自然语言规则，否则它们会独立漂移。

### 2.2 常驻约束与按需知识分离

| 内容 | 最佳载体 | 原因 |
|---|---|---|
| 每次都影响安全/架构判断的短规则 | `AGENTS.md` | 自动进入当前目录的指令链 |
| 完整教程、原理、历史取舍 | guide / ADR / 专题文档 | 人和 Agent 按需读取，不挤占每次请求 |
| 可识别触发条件的多步流程 | Skill | 只在命中任务时加载完整正文 |
| 必须确定执行、不能依赖模型记得 | 测试 / Hook / 编译器约束 | 机器门禁比自然语言可靠 |
| 大范围只读探索或独立并行任务 | Subagent | 隔离搜索噪音与上下文 |

`AGENTS.md` 不是知识库。规则能被类型系统、测试或工具自然约束后，应从常驻上下文删除或缩成一句路由。

### 2.3 目录就近、越近越具体

根规则处理整个项目；`Assets/Game` 处理框架调用者；`Framework` 处理内核维护；Demo Modules 再追加教学规范。子目录规则可以收紧或覆盖父规则，但不要重复父文件全文。

### 2.4 工具能力可相似，配置格式不假装通用

Claude 与 Codex 都支持 Skill、Hook、Subagent，但发现路径和配置格式不同。共享的是设计意图和 `SKILL.md` 内容格式，不是 `.claude/` 与 `.codex/` 文件本身。

## 3. 自动指令预算

Codex 从项目根沿当前工作目录向下拼接 `AGENTS.md`，越近的文件越晚出现、优先级越高；合计默认上限为 **32 KiB**。超过上限后，靠后的就近规则可能无法加入上下文。官方说明见 [Codex AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md)。

2026-08-25 实测的 UTF-8 预算：

| 最深工作位置 | 合计 |
|---|---:|
| 项目根 | 6.52 KiB |
| `Assets/Game` | 15.47 KiB |
| `Assets/Game/Framework` | 22.17 KiB |
| Demo Modules | 26.34 KiB |

最深链保留约 5.66 KiB 给后续规则和换行差异。维护时不要只看单文件行数；要测量**最深链的 UTF-8 合计**。

Claude Code 从工作目录向上读取 `CLAUDE.md`，并在访问子目录文件时发现嵌套 `CLAUDE.md`；`@path` 可导入同源规则。可用 `/memory` 查看实际加载项。官方说明见 [Claude Code memory](https://docs.anthropic.com/zh-CN/docs/claude-code/memory)。

## 4. 能力与配置差异

| 能力 | Claude Code | Codex | 本项目策略 |
|---|---|---|---|
| 项目规则 | `CLAUDE.md`，支持嵌套与 import | `AGENTS.md`，根→cwd 指令链 | `AGENTS.md` 为正文，同目录 `CLAUDE.md` 导入 |
| Project Skill | `.claude/skills/<name>/SKILL.md` | 从 cwd→repo root 扫描 `.agents/skills/<name>/SKILL.md` | Skill 正文可共享，但发现入口需分别提供 |
| User Skill | `~/.claude/skills/` | `~/.agents/skills/` | 个人可用 symlink/junction 复用，不写死机器路径进仓库 |
| Hook | `.claude/settings.json` 的 `hooks` | `.codex/hooks.json` 或 `.codex/config.toml` | 两边都支持，但事件/handler 能力不同，分别配置与测试 |
| Project Subagent | `.claude/agents/*.md` | `.codex/agents/*.toml` | 任务描述可同源，定义格式分别维护 |
| 权限/沙箱 | `.claude/settings*.json` | `.codex/config.toml` / 客户端 Profile | 不互相复制字段；最小权限 |
| MCP | `.mcp.json` / Claude 配置 | `.codex/config.toml` 或客户端 Connector | 协议可共享，注册位置以当前客户端官方文档为准 |

### Skills

两边都使用 `SKILL.md` 和按需加载思路。Claude 官方说明见 [Extend Claude with skills](https://code.claude.com/docs/en/slash-commands)，Codex 的发现路径见 [Build skills](https://learn.chatgpt.com/docs/build-skills)。

本项目以 `.agents/skills/<name>/SKILL.md` 为两个 Skill 的权威正文，Codex 可直接发现；`.claude/skills/<name>/SKILL.md` 只保留同名 frontmatter 和相对路径路由，Claude 命中后读取权威正文。这样两边都有稳定入口，长流程只有一份。

### Hooks

旧文档曾写“Codex 不支持 Hook”，现已不成立。Codex 支持 user/repo `hooks.json` 或 `config.toml`，项目 Hook 需经过 trust review；见 [Codex Hooks](https://learn.chatgpt.com/docs/hooks)。Claude Hook 配在 settings，见 [Claude Hooks](https://code.claude.com/docs/en/hooks-guide)。

Hook 适合格式化、配置审计、阻止危险命令、结束前确定性验证。需要复杂架构判断时，不要强塞进 shell Hook；让测试或人工/Agent review 承担。

Unity 交互式 Editor 还有一类更适合“项目内代码门禁”的固定时机：PlayMode 测试前若存在脏场景，原生保存弹窗会同时阻塞 Unity 主线程与 MCP 队列。项目没有为此复制 Claude/Codex Hook，而是在 `Game.Framework.Editor` 提供跨工具菜单 `SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）`。Agent 先显式调用它，再调用各自的 Unity 测试工具；详细失败语义与恢复流程见 `docs/unity-mcp-tips.md`。这种实现比某个客户端的 Hook 更靠近事件源，也不会把人工 Play 变成全局静默保存。

分钟级 Module 体积矩阵采用同一原则：项目内 `SSFramework/诊断/真实构建体积证据` 把隔离工程、最小依赖、Unity 子进程、BuildReport 解析和 Domain Reload 恢复集中在 `Game.Framework.Editor`，Claude / Codex 不各写一套临时脚本。Agent 只选择组合并观察 `Library/SSFramework/BuildSizeProbe/<run>/report.json`；主 Unity 重载后按落盘 PID 自动重新附着，避免工具调用超时后盲目重跑。证据口径和刻意不做见 ADR-0038，操作要点见 `docs/unity-mcp-tips.md` §12。

### Subagents

两边都支持隔离上下文的 Subagent。Claude 项目定义位于 `.claude/agents/`，见 [Claude subagents](https://code.claude.com/docs/en/sub-agents)；Codex 项目定义位于 `.codex/agents/*.toml`，见 [Codex subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents)。

“跨工具复用”指复用角色意图（如只读 Explorer / Reviewer）和关注点，不指同一个配置文件能被两边直接读取。是否启动 Subagent 仍遵循根 `AGENTS.md` 的协作门槛：默认单 Agent；跨目录只读探索、模糊大设计或真正独立的并行子任务可由主 Agent 自主启动，并在启动时告知用户边界与预期产出，不再要求逐次等待批准。

## 5. 添加或修改协作能力

### 添加项目规则

1. 判断它是否每次都需要；若是流程，优先 Skill；若必须执行，优先测试/Hook。
2. 放到覆盖范围最小的 `AGENTS.md`。
3. 若该目录需要 Claude 懒加载，确认有仅含 `@AGENTS.md` 的 `CLAUDE.md`。
4. 测量根→该目录的 UTF-8 合计，保持低于 32 KiB 并留余量。
5. 架构/目录方案改变时同步本文。

### 添加跨工具 Project Skill

1. 用同一份 `SKILL.md` 设计触发条件、输入、步骤、验证和失败处理。
2. Claude 入口放 `.claude/skills/<name>/SKILL.md`；Codex 入口放 `.agents/skills/<name>/SKILL.md`。
3. 避免复制长正文：可选一个权威正文，让另一入口只做稳定相对路径路由；或者提供生成/一致性检查。
4. 分别在 Claude 和 Codex 中验证“能发现、该触发时触发、不该触发时不触发”。

### 添加 Hook

1. 先写成可独立运行、幂等、有明确退出码的脚本；不要把大段 shell 内嵌在 JSON/TOML。
2. 为 Claude 和 Codex 分别写最薄的事件配置。
3. 项目 Hook 默认最小权限；对修改/新增的 Codex Hook 完成 trust review。
4. 修改 `.claude/settings.json` 时按根规则追加 `.claude/SETTINGS_LOG.md`。
5. 验证成功、失败、超时和从子目录启动四条路径。

### 添加自定义 Subagent

只有角色会重复使用时才落配置；一次性探索直接临时委派即可。定义至少包含：明确触发条件、读写边界、预期输出、禁止事项和验证责任。Reviewer 默认只读，优先报告 correctness/边界/测试缺口，而非纯风格意见。

### 添加 MCP Server

MCP 是通用协议，但客户端注册方式会变化：Claude 项目配置通常使用 `.mcp.json`；Codex 使用 `.codex/config.toml` 或桌面客户端 Connector。先查当前官方文档，再分别注册；不要再写“一个 `.mcp.json` 两边必然自动识别”。凭据不提交仓库。

## 6. 用户级配置

- Claude：`~/.claude/CLAUDE.md`、`~/.claude/skills/`、`~/.claude/agents/`、`~/.claude/settings.json`。
- Codex：`~/.codex/AGENTS.md`、`~/.agents/skills/`、`~/.codex/agents/`、`~/.codex/config.toml` / `hooks.json`。
- 用户级配置只保存个人偏好和跨项目能力；项目架构约束必须提交到仓库。
- 若用 symlink/junction 复用 Skill，先备份并验证目标路径。不要在文档里给无校验的递归删除命令。
- 用户级路径和已安装 Skill 是机器状态，不应在项目文档声称“每个人当前都有”。

## 7. 排查清单

### 规则没有生效

1. 确认当前工作目录和目标文件所在目录；Codex 的链构建与 cwd 有关。
2. Codex 检查最深链是否触及 `project_doc_max_bytes`；Claude 用 `/memory` 查看导入。
3. 检查同目录 `CLAUDE.md` 是否存在、`@AGENTS.md` 路径是否正确。
4. 检查更近的规则是否覆盖父规则。

### Skill 没出现或误触发

1. 确认放在当前工具会扫描的目录，不只检查 `SKILL.md` 格式。
2. 核对 frontmatter 的 name/description 与触发边界。
3. 新增后若客户端未热更新，重启会话再验证。
4. 同名 Skill 不假定会自动合并；明确权威来源。

### Hook 没运行

1. 用客户端的 Hook 检查界面/命令查看发现来源。
2. Codex 项目 Hook 确认已 trust；Claude 检查 settings 层级与 matcher。
3. 直接运行脚本并检查退出码、工作目录、路径与超时。

## 8. 维护责任

- 改代码的人同步相关 guide/ADR/AGENTS/Demo/Test，避免文档与行为分叉。
- 改协作拓扑、Skill/Hook/Subagent 方案的人同步本文。
- `.claude/settings.json` 的每次变更同步 `.claude/SETTINGS_LOG.md`。
- 每次大版本审查重新测量指令链、检查空入口/断链、核对官方能力链接与仓库实际目录。

## 9. 官方参考

- [AGENTS.md 约定](https://agents.md/)
- [Codex：AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md)
- [Codex：Skills](https://learn.chatgpt.com/docs/build-skills)
- [Codex：Hooks](https://learn.chatgpt.com/docs/hooks)
- [Codex：Subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents)
- [Claude：Memory / CLAUDE.md](https://docs.anthropic.com/zh-CN/docs/claude-code/memory)
- [Claude：Skills](https://code.claude.com/docs/en/slash-commands)
- [Claude：Hooks](https://code.claude.com/docs/en/hooks-guide)
- [Claude：Subagents](https://code.claude.com/docs/en/sub-agents)
- [Agent Skills 标准](https://agentskills.io)
- [MCP](https://modelcontextprotocol.io)
