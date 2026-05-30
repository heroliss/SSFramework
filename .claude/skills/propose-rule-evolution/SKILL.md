---
name: propose-rule-evolution
description: 提议演进项目协作规则 / AGENTS.md / skill / hook / memory。当 AI 已识别出"持续改进"触发信号（连续纠正、规则脱节、多步流程反复手动指引、必须执行的操作、用户提个人偏好等），准备把观察落到具体文件结构时使用。提供归类标准、落地格式、措辞风格指引。
---

# 提议规则演进 —— 归类与落地指南

> 触发信号清单在 root `AGENTS.md` 的"持续改进"小节。本 skill 假设你已识别信号，正在决定**该放哪里、怎么写**。

## 核心姿态

- **只提议，不擅自落地**：写文件前一定给用户看"观察到 X / 提议 Y / 为什么 Z"，等用户拍板
- **措辞风格分两档**：严格"不"（硬约束，违反会出 bug / 不可逆）vs 建议"优先"（偏好型，AI 可凭判断豁免）
- **写规则前先想"这条规则会被读到吗"**——按触发模式选载体（见下表）

## 五类信号 → 五种载体

| 信号 | 载体 | 判断标准 |
|---|---|---|
| 用户连续多次纠正同一类**编码/架构**错误 | 嵌套 AGENTS.md（就近目录） | 规则只在动该目录代码时需要 |
| 用户纠正的是**跨场景**协作姿态（沟通、风险意识） | root AGENTS.md | AI 需要常驻识别、无关键词触发 |
| 同样的**多步流程**被反复手动指引 | `.claude/skills/<name>/SKILL.md` | 有明确触发关键词、按需加载省上下文 |
| 某操作**必须**在特定时机自动发生 | `.claude/settings.json` 的 `hooks` | Hook 适合"必须执行"——AI 不可豁免 |
| 用户的**个人偏好 / 外部资源引用 / 非显然判断依据** | `~/.claude/projects/<project>/memory/` | per-user、跨会话、不跟项目走 |

### 关键边界

- **规则 vs Hook**：规则 = "应该"（AI 可凭判断豁免）；Hook = "必须"（harness 强制执行）。决定写哪里时问自己"AI 偶尔豁免可以接受吗？"
- **项目编码规范绝不放 memory**：memory 是 per-user 的，不跟代码走。代码约束 → AGENTS.md。
- **跨项目通用流程优先 `npx skills add <name>`**；只对当前项目有意义的才放 `.claude/skills/`。

## 文件格式速查

### AGENTS.md 条目
```markdown
## N. 简短规则标题
**严格"不"措辞**：硬约束的核心要求一句话说清。
**Why:** 不这么做会出什么问题（具体场景、踩过的坑）。
**How to apply:** 在什么场景下生效（精确边界条件）。
```

### Skill frontmatter
```markdown
---
name: kebab-case-name
description: 一句话说清做什么 + 何时触发（含关键词，AI 据此自动选用）
---

# 标题
## 核心约束 / 流程 / 常见错误 / 触发关键词
```
- description 要写**触发关键词**，AI 才会自动调用
- 触发模式是"用户请求"——观察型规则不适合做 skill

### Hook 配置（settings.json）
```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [{
          "type": "command",
          "command": "echo '$TOOL_INPUT' | grep -q 'settings.json' && date >> .claude/SETTINGS_LOG.md"
        }]
      }
    ]
  }
}
```
- 改完 `.claude/settings.json` 一定要在同目录 `SETTINGS_LOG.md` 追加变更说明

### Memory 文件
见 `~/.claude/CLAUDE.md` 的 auto memory 说明（user / feedback / project / reference 四类，每类各有 frontmatter 格式）。

## 提议模板

给用户看的格式：

```
观察到：<具体信号，引用一两次对话片段或代码位置>
提议：<把这条规则/流程加到 X 文件，措辞 Y>
为什么放 X：<触发模式判断、为什么不放其他地方>
措辞风格：<严格"不" / 建议"优先">
要落地吗？
```

## 反模式

- ❌ 把"AI 主动观察型"规则做成 skill（永远不会被触发，变成死文件）
- ❌ 把"用户请求触发型"规则塞 root AGENTS.md（白占上下文）
- ❌ 不带 Why 的规则（未来无法判断边界条件，要么过度严格要么被无脑豁免）
- ❌ 用 memory 存项目编码规范（换台机器或换个开发者就丢）
- ❌ 用 Hook 实现"应该"型偏好（强制执行会激怒用户）
