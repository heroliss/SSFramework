# AI 协作方案说明

> 给开发人员看：本项目如何与各种 AI 编程工具（Claude Code、Codex、Cursor 等）协作。
> 看完本文你能掌握：项目级配置布局、用户级配置布局、跨工具差异、添加新规则/Skill/Hook 的方法、设计原理。

---

## 一、30 秒概览

```
项目根/
├── AGENTS.md                              ← 项目级规则（universal，所有 AI 都读）
├── CLAUDE.md                              ← 一行 @AGENTS.md（让 Claude 也读 AGENTS.md）
├── Assets/Game/Framework/
│   ├── AGENTS.md                          ← 框架专属规则（21 条，目录就近性自动加载）
│   └── CLAUDE.md                          ← 一行 @AGENTS.md
└── .claude/                               ← Claude Code 专属（无通用标准）
    ├── settings.json                      ← 团队共享（permissions/hooks/MCP）
    ├── settings.local.json                ← 个人覆盖（gitignored）
    ├── skills/                            ← 项目级 Skill（按需添加）
    ├── agents/                            ← 项目级 Subagent（按需添加）
    └── SETTINGS_LOG.md                    ← 设置变更日志
```

**核心思想：**
- **规则**用嵌套 `AGENTS.md` —— 跨工具通用 + 目录就近自动加载
- **CLAUDE.md** 用一行 `@AGENTS.md` 导入 —— 单一来源、零重复（Claude 不自动读 AGENTS.md，需要 import）
- **配置/Skill/Hook** 没有跨工具标准，只能放 `.claude/`，按 Claude Code 的约定来

---

## 二、设计原则

### 1. 单一来源（Single Source of Truth）
规则只在 `AGENTS.md` 写一次。`CLAUDE.md` 通过 `@AGENTS.md` 导入，绝不复制内容。

### 2. 多 Agent 通用（Tool-Agnostic）
优先用业界开放标准：
- **AGENTS.md**（[agents.md spec](https://agents.md/)）：Codex、Cursor、Copilot、Zed、Warp、Aider、Jules 等都支持
- **Agent Skills**（[agentskills.io](https://agentskills.io)）：Claude Code 内置支持，可通过 `npx skills` 安装
- **MCP**（Model Context Protocol）：Claude、Codex、部分 IDE 通用

### 3. 目录就近性（Proximity-Based Loading）
规则按目录嵌套，只在相关代码被读取时进入上下文，节省 token：
- 改 Match-3 业务代码 → 只加载根 AGENTS.md
- 改 `Assets/Game/Framework/**` 代码 → 自动追加加载嵌套 AGENTS.md（21 条框架规则）

### 4. 工具专属不可避免，集中管理
Hooks、Subagents、Permissions、项目级 Skill 没有跨工具标准，统一放 `.claude/`，**不污染项目根**。

---

## 三、项目级目录详解

### 根目录文件

| 文件 | 作用 | 谁读 |
|---|---|---|
| `AGENTS.md` | 项目级规则唯一来源 | 所有 AI 工具 |
| `CLAUDE.md` | 仅一行 `@AGENTS.md` | Claude Code |
| `docs/ai-collaboration-guide.md`（本文件） | AI 协作方案说明 | 人类开发者 |
| `.mcp.json` | 项目级 MCP server 配置（如有） | Claude / Codex |

### 嵌套 AGENTS.md（路径就近加载）

| 位置 | 作用 |
|---|---|
| `Assets/Game/Framework/AGENTS.md` | 框架编码规则（21 条），仅在改框架代码时加载 |
| `Assets/Game/Framework/CLAUDE.md` | 一行 `@AGENTS.md` |

未来若 Match-3 业务模块（如 `Assets/Game/Match3/`）形成稳定约束，也可以在该目录加 `AGENTS.md` + `CLAUDE.md`。

### `.claude/` 目录（Claude Code 专属）

| 路径 | 作用 | Git |
|---|---|---|
| `settings.json` | 团队共享：permissions allowlist、hooks、MCP servers | ✅ |
| `settings.local.json` | 个人本地：API key、沙箱地址、个人 allow 项 | ❌ gitignored |
| `skills/<name>/SKILL.md` | 项目级 Skill（多步流程，按 `/name` 调用） | ✅ |
| `agents/<name>.md` | 项目级 Subagent 定义 | ✅ |
| `rules/<topic>.md` | 模块化规则（可选，本项目未启用，规则放 AGENTS.md） | ✅ |
| `commands/<name>.md` | 自定义 slash command（已合并到 skills，仍可用） | ✅ |
| `SETTINGS_LOG.md` | 设置变更日志（本项目约定） | ✅ |

> **为什么 rules 没用 `.claude/rules/`？**
> `.claude/rules/` 支持 YAML `paths:` 字段做 glob 路径范围限定，但**只 Claude Code 支持**。本项目优先 universal，用嵌套 AGENTS.md 实现同样的路径就近加载。

---

## 四、跨工具差异

### Rules / Instructions

| 工具 | 自动加载 | 嵌套加载 |
|---|---|---|
| Claude Code | `CLAUDE.md` | 子目录 `CLAUDE.md`（读取该目录文件时加载） |
| Codex CLI | `AGENTS.md` | 子目录 `AGENTS.md`（就近优先） |
| Cursor | `.cursorrules` / `AGENTS.md` | 部分支持 |
| Copilot | `AGENTS.md` / `.github/copilot-instructions.md` | 部分支持 |

**本项目做法：** AGENTS.md 是唯一来源，CLAUDE.md = `@AGENTS.md` 一行导入。

**关键引用：**
> Claude Code reads `CLAUDE.md`, not `AGENTS.md`. If your repository already uses `AGENTS.md` for other coding agents, create a `CLAUDE.md` that imports it so both tools read the same instructions without duplicating them.
>
> —— [Anthropic 官方文档](https://code.claude.com/docs/en/memory#agents-md)

### Skills

| 工具 | 路径 | 标准 |
|---|---|---|
| Claude Code | `~/.claude/skills/` 或 `.claude/skills/` | [Agent Skills 开源标准](https://agentskills.io) |
| `npx skills` CLI | `~/.agents/skills/` | 同上（跨工具 skill 市场） |
| 其他 AI 工具 | 各自实现 | 无统一 |

### Hooks

| 工具 | 位置 |
|---|---|
| Claude Code | `.claude/settings.json` 的 `hooks` 字段 |
| Codex | 不支持 |
| Cursor | 不支持 |

### Permissions

| 工具 | 位置 |
|---|---|
| Claude Code | `.claude/settings.json` / `settings.local.json` 的 `permissions.allow` |
| Codex | 配置文件，但格式不同 |

### MCP Servers

| 工具 | 位置 |
|---|---|
| 通用 | 项目根 `.mcp.json` |
| Claude Code | 也支持 `~/.claude/.mcp.json` 用户级 |

---

## 五、用户级（机器级）配置

### `~/.claude/`（Claude Code 用户配置）

```
~/.claude/
├── settings.json                ← 用户级 Claude Code 设置
├── CLAUDE.md                    ← 跨所有项目的个人指令（可选）
├── skills/                      ← 用户级 Skill（跨所有项目）
│   └── <name> → /c/Users/herol/.agents/skills/<name>   ← 软链到 .agents
├── agents/                      ← 用户级 Subagent
├── plugins/                     ← Claude Code plugin
├── projects/<project>/memory/   ← 自动记忆（每个项目独立）
├── SETTINGS_LOG.md              ← 用户级设置变更日志
└── shell-snapshots/, sessions/, telemetry/ ← Claude Code 内部
```

### `~/.agents/`（跨工具 Skill 仓库）

```
~/.agents/
├── .skill-lock.json             ← npx skills 的 lockfile
└── skills/                      ← 通过 `npx skills add <name>` 安装的 skill
    ├── grill-me/
    ├── refactor/
    ├── mermaid-diagrams/
    └── improve-codebase-architecture/
```

### 推荐：用软链让 Claude Code 复用 `.agents/skills`

`npx skills` 是跨工具的 skill 安装器，把 skill 装到 `~/.agents/skills/<name>/`。但 Claude Code 只读 `~/.claude/skills/`。

**方法 A：单个 skill 软链（当前用法，灵活）**

```bash
ln -s ~/.agents/skills/grill-me ~/.claude/skills/grill-me
ln -s ~/.agents/skills/refactor ~/.claude/skills/refactor
```

优点：可选择性启用，每个 skill 单独控制。
缺点：装新 skill 后要手动加链接。

**方法 B：整个目录软链（一劳永逸）**

```bash
# 备份并删除原目录
rm -rf ~/.claude/skills
# 软链整个目录（Git Bash / WSL / Linux / macOS）
ln -s ~/.agents/skills ~/.claude/skills
```

**Windows 原生命令（管理员或开发者模式）：**

```powershell
# 目录符号链接（需要管理员或开发者模式）
New-Item -ItemType SymbolicLink -Path "$HOME\.claude\skills" -Target "$HOME\.agents\skills"

# 或目录联接（Junction，不需要管理员）
New-Item -ItemType Junction -Path "$HOME\.claude\skills" -Target "$HOME\.agents\skills"
```

> **Junction vs Symlink：**
> - **Junction**（`mklink /J`）：只能链接本地目录，**不需要管理员权限**，最适合此场景
> - **Symlink**（`mklink /D`）：支持远程路径，需要管理员或开发者模式

**优点：** 装新 skill 立即生效，零维护。
**缺点：** 无法选择性禁用单个 skill（但可以用 Claude 的 `skillOverrides` 设置精细控制）。

---

## 六、添加新东西的方法

### 添加项目级规则（通用，所有 AI 读）

1. 编辑 `AGENTS.md`（项目级）或 `Assets/Game/Framework/AGENTS.md`（框架级）
2. 添加新规则，遵循"严格/建议"措辞约定（见 AGENTS.md 顶部）
3. **不需要改 CLAUDE.md**（`@AGENTS.md` 会自动同步）

### 添加项目级 Skill（Claude Code 专属）

```bash
mkdir -p .claude/skills/my-skill
cat > .claude/skills/my-skill/SKILL.md <<'EOF'
---
name: my-skill
description: 一句话描述（让 Claude 知道什么时候用）
---

# 多步流程的具体说明
1. ...
2. ...
EOF
```

调用：在 Claude Code 中输入 `/my-skill`。

### 添加 Hook（Claude Code 专属）

编辑 `.claude/settings.json`：

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit",
        "hooks": [
          {
            "type": "command",
            "command": "echo 'edited' >> .claude/edit.log"
          }
        ]
      }
    ]
  }
}
```

### 添加 MCP Server

编辑项目根 `.mcp.json`（不存在则创建）：

```json
{
  "mcpServers": {
    "my-server": {
      "command": "npx",
      "args": ["-y", "@example/mcp-server"]
    }
  }
}
```

Claude Code 和 Codex 都会识别此文件。

### 添加用户级（跨项目）Skill

```bash
# 推荐：通过 npx skills（跨工具）
npx skills add <skill-name>

# 或手动放到 ~/.claude/skills/（仅 Claude Code）
mkdir -p ~/.claude/skills/my-skill
# ... 写 SKILL.md
```

---

## 七、常见问题（FAQ）

### Q1：首次启动 Claude Code 弹窗"approve external import"是什么？
A：因为 CLAUDE.md 里有 `@AGENTS.md` 导入，Claude Code 首次见到外部 import 会询问。**必须点允许**，否则 AGENTS.md 永远加载不进来。每个项目每个新 import 路径只问一次。

### Q2：为什么不直接放 `~/.claude/skills/` 而要用 `~/.agents/skills/`？
A：`npx skills` 是跨工具开放标准 skill 仓库。装到 `~/.agents/skills/` 后软链到 Claude 目录，未来切换或并行使用其他支持 Agent Skills 标准的工具时无需重装。

### Q3：嵌套 AGENTS.md 什么时候加载？
A：
- **Claude Code**：读取该目录下任意文件时加载（懒加载）
- **Codex CLI**：进入该目录时加载（就近优先）
- 改根目录代码时**不**加载嵌套 AGENTS.md，节省 context

### Q4：修改 AGENTS.md 后要不要同步改 CLAUDE.md？
A：**不需要**。CLAUDE.md 是一行 `@AGENTS.md` 导入，自动同步。

### Q5：项目根 `CLAUDE.md` vs `.claude/CLAUDE.md` 选哪个？
A：两者都被 Claude Code 识别。本项目用根 `CLAUDE.md`，原因是：
- 与 `AGENTS.md` 并排，对人类开发者更显眼
- 与 Codex/Cursor 的 `AGENTS.md` 位置对称

### Q6：`CLAUDE.local.md` 是什么？
A：项目级个人覆盖，应 gitignore。用于：
- 你的本地 API key、沙箱 URL
- 临时实验性指令（不想推到团队的）

### Q7：发现 AGENTS.md 没被加载怎么排查？
A：
1. Claude Code：在会话中运行 `/memory`，会列出加载的所有 instruction 文件
2. 检查 `@AGENTS.md` import 是否被允许（首次弹窗审批）
3. 检查文件是否真的在工作目录树内

### Q8：要不要在 `.claude/rules/` 也放一份规则？
A：本项目**不放**。原因：
- `.claude/rules/` 只 Claude Code 支持，违反 universal 原则
- 嵌套 AGENTS.md 已经实现同样的路径就近加载效果

### Q9：Subagent 怎么做到跨工具？
A：**做不到**。Subagent 是各工具的执行时概念，没有跨工具标准。本项目的 `.claude/agents/` 仅 Claude Code 用。Codex/Cursor 用其他工具时，相应 subagent 需各自重新定义。

---

## 八、相关参考

- **AGENTS.md 标准**：https://agents.md/
- **Anthropic Claude Code 文档**：
  - Memory（CLAUDE.md）：https://code.claude.com/docs/en/memory
  - Skills：https://code.claude.com/docs/en/skills
  - Settings：https://code.claude.com/docs/en/settings
  - Sub-agents：https://code.claude.com/docs/en/sub-agents
  - Hooks：https://code.claude.com/docs/en/hooks
- **Agent Skills 开源标准**：https://agentskills.io
- **MCP 协议**：https://modelcontextprotocol.io
- **`npx skills` CLI**：https://github.com/anthropics/skills

---

## 九、维护责任

- **AI 协作规则**（AGENTS.md / Assets/Game/Framework/AGENTS.md）：由实施代码改动的开发者 + AI 共同维护，遵循 AGENTS.md「持续改进 / Self-Evolution」段
- **本文件**（docs/ai-collaboration-guide.md）：方案有重大调整时同步更新
- **`.claude/settings.json`**：所有团队成员可改，改完记 `.claude/SETTINGS_LOG.md`
- **`~/.claude/`、`~/.agents/`**：每个开发者自己的机器自己管，参考"五、用户级配置"自行搭建
