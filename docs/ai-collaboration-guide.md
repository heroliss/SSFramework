# Codex 协作方案

> 面向项目维护者：说明 SSFramework 当前如何向 Codex 提供常驻规则、按需流程与确定性门禁。能力信息最后核验于 **2026-08-28**；Claude 相关文件仅作为停用工具的遗留适配，不再驱动本项目设计。

## 1. 当前真值

```text
SSFramework/
├── AGENTS.md                         # 全项目常驻协作边界
├── Assets/Game/AGENTS.md             # Framework 业务调用约束
├── Assets/Game/Framework/AGENTS.md   # Framework 内部实现约束
├── Assets/Game/Framework/Demo/Scripts/Modules/AGENTS.md
│                                      # Demo 教学章节约束
├── .agents/skills/                   # Project Skill 权威正文（当前 3 个）
├── docs/                             # guide / ADR / 专题文档，按需读取
├── CLAUDE.md / .claude/ / .mcp.json # 停用工具的遗留适配，不参与当前完成定义
└── .codex/                           # 当前未创建；没有仓库级 Codex 配置或自定义 Agent
```

不要把“Codex 支持某能力”写成“本项目已经配置该能力”。当前项目有分层 `AGENTS.md` 与 `.agents/skills`，但没有仓库级 Hook、custom agent 或 Codex MCP 配置。

## 2. 设计原则

### 2.1 Codex-first，而不是客户端快捷键优先

项目规则描述目标、边界和完成条件，不规定某个 UI 操作。Codex 当前官方实践确实提供 Plan mode，并在支持的交互界面给出 `/plan` 或 Shift+Tab 入口；这说明 Plan mode 并非 Claude 专属，但快捷键仍是客户端细节，不应成为仓库门禁。

复杂任务需要的是“先调查、形成可检查计划、执行中更新、完成后验证”。当前客户端有计划工具就直接使用；没有也可在任务内维护计划。只有缺少会显著改变结果的用户决策时才暂停，不要求用户为了计划切换模式。

### 2.2 一条规则只有一个权威来源

- 项目常驻规则写在覆盖范围最小的 `AGENTS.md`。
- 多步流程写在 `.agents/skills/<name>/SKILL.md`。
- 完整原理和历史取舍写入 guide / ADR。
- 确定性契约用测试、编译器约束或项目内工具门禁实现。

遗留 `CLAUDE.md` 仍可导入同目录 `AGENTS.md`，`.claude/skills` 仍可路由到 `.agents/skills`，但这些适配不再要求同步扩展、验证或修复。若未来重新启用 Claude，先按届时官方格式重做一次兼容审计。

### 2.3 常驻上下文只保留主动判断

`AGENTS.md` 不是模块百科。只有无需关键词也必须改变每次判断的内容才常驻；某个可选模块的详细 API、故障处理和示例在任务命中后读取对应 guide / ADR。这样在 Framework 深目录工作时，不会被当前任务无关的十几个模块契约占满上下文。

Demo 教学的内容组织规则放在最窄的 `Assets/Game/Framework/Demo/Scripts/Modules/AGENTS.md`：总览只说明一次阅读路线，顺读主线靠各章正文承接、对比与边界形成递进，不由 Shell 给每章重复叠加“新手导览”。这是作者判断，不适合用 Hook 或锁死具体文案的测试强制。

### 2.4 机器门禁优于“请记得”

能写成行为契约的规则优先落到代码和测试：

- PlayMode 测试前的脏场景处理由 `SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）` 承担；
- Editor 副作用动作由 `FrameworkEditorOperationGate` 在 UI 与 Implementation 两层校验；
- Module 依赖、菜单契约、Demo CodeRef 与真实构建体积都有项目内验证入口；Demo 教学内容还由 `DemoTeachingContract` 区分 Tip / Caution / Experiment 等实际 Build 语义；
- 工程外 Unity 发现与 headless 自动化经 `Tools/UnityAutomation.psm1` 统一，避免每个 Agent 重写机器路径。

这些 Seam 比某个客户端 Hook 更靠近事件源，也能被人工、MCP 与 CI 共同复用。具体流程见 `docs/unity-mcp-tips.md`、`docs/unity-cli-automation.md` 和相关 ADR。

## 3. Codex 能力与本项目策略

| 能力 | Codex 当前入口 | 本项目当前策略 |
|---|---|---|
| 项目规则 | 根到 cwd 的 `AGENTS.md` 指令链 | 分四层就近约束，越近越具体 |
| Project Skill | cwd 到 repo root 的 `.agents/skills/<name>/SKILL.md` | `.agents/skills` 是唯一权威正文 |
| Hook | `<repo>/.codex/hooks.json` 或 `.codex/config.toml` 内联 hooks，需项目 trust | 当前未配置；只有固定事件的确定性检查才新增 |
| Subagent | 内置 Agent；可选 `.codex/agents/*.toml` | 默认单 Agent；仅用户或已触发 Skill 明确要求时委派 |
| 权限/沙箱 | 客户端 permission profile / `.codex/config.toml` | 不在仓库规则里承诺或扩大权限 |
| MCP / Connector | 客户端连接器或对应 Codex 配置层 | Unity 使用现有 MCP；凭据和机器状态不提交仓库 |

配置格式和能力会随 Codex 更新。新增 `.codex/*` 前先查当前官方文档，不从旧项目文案反推 schema。

### Skills

Codex 会从当前目录向仓库根扫描 `.agents/skills`，并按 name/description 发现任务。当前三个 Project Skill：

- `propose-rule-evolution`：选择 AGENTS、Skill、Hook、测试或配置等正确载体；
- `unity-background-automation`：后台运行/监控 Unity 测试与 Editor 自动化；
- `unity-screenshot`：捕获并实际检查 Unity 视图。

Skill 只收口有稳定触发条件的多步流程。一次性判断、已经由测试兜底的契约或与项目无关的个人偏好，不再创建 Skill。

### Hooks

Hook 适合格式化、配置审计、危险命令拦截或固定事件验证，要求脚本可独立运行、幂等、超时和失败可诊断。主观架构判断不塞入 Hook；已有项目内测试/工具能更靠近事件源时，不重复做客户端 Hook。

当前仓库没有 `.codex/`，因此也没有团队 Hook。未来新增时选择 `hooks.json` 或内联 TOML 之一，避免同一配置层双份加载，并完成 trust 与成功/失败/超时验证。

### Subagents

Subagent 能隔离大范围搜索噪音，也会增加 token 与协调成本。项目不把“任务复杂”自动等同于“必须并行”：只有用户明确要求委派/并行，或已触发的 Skill 明确要求时才使用；子任务还必须真正独立、不会争写同一文件。一次性 Explorer/Reviewer 不必落 `.codex/agents`，只有角色反复使用且边界稳定时才配置 custom agent。

## 4. 自动指令预算

Codex 从项目根沿当前工作目录向下拼接 `AGENTS.md`，越近的文件越晚出现、优先级越高；默认合计上限为 **32 KiB**。超过上限时，最重要的就近规则反而可能无法加入。

2026-08-28 按 UTF-8 实测：

| 最深工作位置 | 累计字节 | 约合 |
|---|---:|---:|
| 项目根 | 7,134 | 6.97 KiB |
| `Assets/Game` | 15,082 | 14.73 KiB |
| `Assets/Game/Framework` | 24,687 | 24.11 KiB |
| Demo Modules | 29,518 | 28.83 KiB |

最深链还剩 3,250 字节（约 3.17 KiB）。维护时测量**根到最深目录的 UTF-8 合计**，不要只看单文件行数；新增细节优先下沉到 guide、ADR 或 Skill。

PowerShell 测量示例：

```powershell
$paths = @(
  'AGENTS.md',
  'Assets/Game/AGENTS.md',
  'Assets/Game/Framework/AGENTS.md',
  'Assets/Game/Framework/Demo/Scripts/Modules/AGENTS.md'
)
$total = 0
foreach ($path in $paths) {
  $total += [Text.Encoding]::UTF8.GetByteCount((Get-Content -LiteralPath $path -Raw))
  [pscustomobject]@{ Path = $path; RunningBytes = $total }
}
```

## 5. 修改协作体系

### 添加或修改项目规则

1. 判断它是否无需关键词也必须影响每次决策；否则优先文档、Skill 或测试。
2. 放到覆盖范围最小的 `AGENTS.md`，删除被替代的旧规则。
3. 测量最深指令链，并验证新规则不会与更近目录冲突。
4. 协作拓扑或重大策略改变时同步本文。

### 添加 Project Skill

1. 在 `.agents/skills/<name>/SKILL.md` 写清触发条件、步骤、验证和失败处理。
2. 长参考放 `references/`，重复机械动作优先提供脚本。
3. 验证“能发现、该触发时触发、不该触发时不触发”。
4. 不为停用的 Claude 自动创建路由；未来重启兼容时再适配。

### 添加 Hook 或 custom agent

只有固定事件确实需要确定执行时才添加 Hook；只有相同隔离角色反复出现时才添加 custom agent。创建 `.codex/` 后同步本文，记录触发边界、权限、失败语义和验证结果。仓库配置不能替用户授予外部权限，也不得提交凭据。

### 修改用户级配置

`~/.codex`、`~/.agents` 与客户端已安装能力属于个人/机器状态，不是仓库真值。除非用户明确要求，不把项目任务扩张成用户配置变更；确需修改时先说明影响，并按当前 Codex 推荐方式保留可追溯记录。

## 6. 排查清单

### 规则没有生效

1. 确认当前 cwd、项目根和目标文件所在目录。
2. 测量根到 cwd 的指令链是否接近 `project_doc_max_bytes`。
3. 检查更近的 `AGENTS.override.md` / `AGENTS.md` 是否覆盖父规则。
4. 不用遗留 `CLAUDE.md` 的加载结果推断 Codex 行为。

### Skill 没出现或误触发

1. 确认目录位于 cwd 到 repo root 的 `.agents/skills` 链上。
2. 核对 frontmatter 的 name/description 与真实触发边界。
3. 同名 Skill 不会自动合并；明确项目权威来源。
4. 若客户端未热更新，重启任务后再验证。

### Hook 没运行

1. 先确认仓库实际存在并信任 `.codex/hooks.json` 或 `.codex/config.toml`。
2. 直接运行脚本，检查退出码、cwd、路径、权限和超时。
3. 检查是否同时配置 JSON 与内联 TOML，避免重复执行与告警。

## 7. 维护责任

- 改代码的人同步相关 guide / ADR / AGENTS / Demo / Test，避免文档与行为分叉。
- 改协作拓扑、Skill、Hook、Subagent 或 Codex 配置的人同步本文。
- 每次大版本重新测量指令链、检查空入口/断链，并核对官方能力与仓库实际目录。
- 停用工具的遗留入口只做保留性维护，不纳入日常完成定义；重新启用时重新设计，不承诺旧配置仍兼容。

## 8. 官方参考

- [Codex 最佳实践](https://learn.chatgpt.com/guides/best-practices)
- [Codex：AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md)
- [Codex：Skills](https://learn.chatgpt.com/docs/build-skills)
- [Codex：Hooks](https://learn.chatgpt.com/docs/hooks)
- [Codex：Subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents)
- [Codex：配置](https://learn.chatgpt.com/docs/config-file/config-basic)
- [Agent Skills 规范](https://agentskills.io)
- [MCP](https://modelcontextprotocol.io)
