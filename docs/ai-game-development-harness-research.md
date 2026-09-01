# AI 游戏开发 Harness 调研与项目采用决策

> 状态：**研究基线 v0.1**，核验于 2026-09-01。本文记录可复用机制、采用边界和来源，避免后续只凭记忆重复搜集。项目仍应在真实任务中验证并修改这些判断；这里不是第三方项目排行榜，也不是安装清单。

## 1. 这里说的 Harness 是什么

Harness 不是某个神秘的“全自动做游戏 AI”，也不等同于 MCP、测试框架或一组 Prompt。它是在 Agent 和真实开发环境周围建立的**受控执行闭环**：

```text
任务 / 假设 / 预算
  → 准备固定环境与输入
  → Driver 驱动 Unity、Blender、Player 或测试
  → Observer 收集结构化状态、日志、截图、视频和性能数据
  → Oracle / Rubric 判断确定事实、视觉问题与待人工问题
  → 在有限次数内恢复、重试或停止
  → 留下可复查的 Evidence Bundle
```

MCP 主要解决“Agent 能操作什么”；测试解决“某项确定行为是否满足断言”；Skill 解决“遇到这类任务时如何判断和组织步骤”；Harness 把它们连成一次可运行、可观察、能停下来并能交付证据的过程。

本项目需要的是**生产 Harness**：帮助人和 Agent 更可靠地开发《游牧工坊》并改进 SSFramework。公开项目中还有很多**Benchmark Harness**，其目标是公平测量某个 Agent，因而禁止人工提示、追求隔离和打分。它们的实验纪律值得借鉴，但不能把“不给 Agent 帮助”“无人值守无限运行”机械搬进日常协作。

## 2. 调研结论摘要

没有发现一套能够直接替换“Codex + 当前 Unity MCP + Unity Test Framework + 项目工具”的成熟开源总框架。当前最佳策略是保留已有主链，只吸收以下经过代码或实验支持的机制：

1. **精确版本、固定 Seed 与预算**：一次运行必须知道代码版本、Unity / Blender 版本、随机种子、时间 / 动作 / 费用上限。
2. **结构化状态与视觉证据并用**：数值和状态读取结构化 Dump；构图、遮挡、轮廓、反馈和动态过程看截图 / 视频，二者不能互相冒充。
3. **可回放动作轨迹**：优先保存语义动作或真实 Input Action 的时间轨迹，而不是只保存最终截图。
4. **硬门禁与软评审分离**：编译、资源守恒、存档往返是确定性 Oracle；美术一致性和可读性可由 rubric 辅助；“好玩”必须留给真实玩家。
5. **结果不只有 Pass / Fail**：至少区分 `passed`、`failed`、`inconclusive` 与 `infrastructure_error`，避免编辑器没启动却被当成功能失败，也避免零测试假绿。
6. **真实进度而非进程存活**：长跑要观察日志、结果文件、截图、测试阶段或提交是否继续更新；“Unity 和 Agent 进程还在”不等于有进展。
7. **单 Editor 所有权与有限恢复**：同一 Editor 同时只允许一个生命周期所有者；恢复要有锁、退避、停止条件，不能形成烧钱或破坏状态的无限重启环。
8. **生成资产可追溯、可替换**：记录来源 / Provider、模型、Prompt、Seed、请求 ID、许可证、文件哈希和用途；先用稳定占位路径，生成失败不能阻塞代码开发。

## 3. 候选项目与可借鉴内容

| 项目 | 它真正解决的问题 | 值得吸收的机制 | 本项目决策 |
|---|---|---|---|
| [GameDevBench](https://github.com/waynchi/gamedevbench) | 在 Godot 4.4.1 中评测 333 个多模态游戏开发任务 | 固定引擎版本、任务隔离、断点续跑、记录 Agent / Model / reasoning effort / 费用；简单图像或视频反馈使其公布的 GPT-5.4 结果从 41.1% 提升到 52.0% | **吸收实验结构与视觉反馈原则**；不移植 Godot Runner，不把 benchmark 分数当产品质量 |
| [GameCraft-Bench](https://github.com/FreedomIntelligence/gamecraft-bench) | 评测从需求到完整可运行游戏，并以输入轨迹重放实际 Gameplay | 构建硬门禁；按帧记录键鼠轨迹；重放后同时保留 MP4、抽帧、进程日志、judge 原始记录和 breakdown；机制项可取最佳证据，视觉项跨场景取平均 | **吸收 Trace + Evidence Bundle + 分层 rubric**；VLM 只能做审查线索，不能成为资源守恒、性能或“好玩”的真值 |
| [DeNA Anjin](https://github.com/DeNA/Anjin) | Unity 游戏的 Autopilot / Monkey 测试 | `ScriptableObject` 运行设置；Scene → Agent 映射；固定 Seed、Time Scale、Lifespan、输出目录；无可交互元素超时、重复操作检测、截图和 JUnit Reporter | **作为 Unity Playtest 设计参考**；现阶段不安装。游戏尚需先建立语义动作、任务 Oracle 和真实 Input Driver，随机点 UI 不是最高风险 |
| [Gamebrew](https://github.com/ataberk-xyz/Gamebrew) | 让 Agent 在 Unity Play Mode 中移动、观察、输入、截图与 Dump 状态 | “截图看表现、Dump 看数值”的双感官规则；观察 Agent 只能使用默认拒绝 allowlist，生命周期由单独 Orchestrator 拥有；看道具要多角度而非单帧 | **只借鉴交互协议**。项目非常新，且与当前 Unity MCP 重叠；不增加第二个 Editor Bridge 或本地端口 |
| [Signal Loop Unity Code MCP](https://github.com/Signal-Loop/UnityCodeMCPServer) | 用 C# 驱动 Unity Editor 与 Play Mode 的闭环实验 | `enter → act → observe → adapt → exit`，真实 Input Action、截图、日志与实时状态协作 | **只借鉴闭环**。任意 C# 执行权限高，并与当前 MCP 工具集重复 |
| [AAABench](https://github.com/ukanwat/aaabench) | 让 Agent 在 Unreal 中进行超长、无人值守的完整世界构建实验 | 单实例锁、暂停文件、指数退避；检查端口实际持有者；以最近产物和 Agent 回合而非进程计数判断活性；记录模型 / 提示污染 | **吸收长跑基础设施经验**。其 bypass 权限、无限续跑和“不得帮助 Agent”属于 benchmark 条件，不进入日常开发默认值 |
| [everything-game-dev-code](https://github.com/MRCalderon3D/everything-game-dev-code) | 面向多引擎、多 Agent 的超大脚手架与资产生成注册表 | 共享真值 + 薄适配器；manifest / schema / doctor / drift 检查；Provider 注册表、费用确认、质量档；中立暂存格式、稳定占位路径、`.provenance.json` | **选择性重写机制，不整包采用**。其大量 Agent、Command、Skill 和客户端镜像会显著增加维护面，与本项目“最薄兼容”目标相反 |
| [Unity Technologies Skills](https://github.com/Unity-Technologies/skills) | Unity 官方维护的窄域 Agent 知识与工作流 | UI Toolkit / uGUI、URP 后处理、Render Graph、Shader Graph、音频优化、AI Navigation、Localization 等具体 API 陷阱和检查表 | **作为高信誉上游知识池**，真实任务触发时逐个差异审查；不把整包安装为项目规则。部分 Skill 假设官方 Unity CLI / Pipeline，需适配当前 MCP 与项目安全边界 |
| [AltTester Unity SDK](https://github.com/alttester/AltTester-Unity-SDK) | 对 Editor 或真机 Player 做 UI 驱动的外部黑盒测试 | 多语言 Driver、对象定位、输入、截图、跨设备运行 | **Later**：只有 Android / iOS 真机矩阵或外部 QA 成为需求时再评估；先审 GPL-3.0、测试构建注入和服务依赖 |
| [GameCI Unity Test Runner](https://github.com/game-ci/unity-test-runner) | 在 GitHub Actions 中执行 Unity Test Framework | CI 隔离、测试结果和构建环境编排 | **需要 GitHub CI 时采用**；它不是 AI Playtest，也不替代本地 MCP 和业务 Oracle |
| [Unity ML-Agents](https://github.com/Unity-Technologies/ml-agents) | 训练或评测学习型游戏 Agent | 大状态空间探索、策略训练、平衡与异常路径发现 | **Later**：只有规则 Harness 无法覆盖的探索 / 平衡问题成立时使用，不作为普通居民 AI 或回归测试的前置依赖 |

## 4. 本项目的最小 Harness 契约

以下字段是 v0.1 建议，不要求所有小改都填写完整表格。只有可重复实验、长跑、视觉评审、资产生成或高风险验收才需要物化为 manifest。

### 4.1 Experiment Definition

| 字段 | 含义 |
|---|---|
| `id` / `purpose` | 稳定实验 ID 与要回答的单一问题 |
| `hypothesis` | 什么观察会支持或推翻当前判断 |
| `revision` / `toolVersions` | Git revision、Unity / Blender / Package / Harness 版本 |
| `seed` | 世界、AI、采样或生成 Seed；无随机性时明确为空 |
| `budgets` | 时间、动作、重试、费用、截图 / 视频大小上限 |
| `preconditions` | 场景、Build、账号、Editor 状态与输入数据 |
| `actions` | 语义 Command 或真实 Input 的有序轨迹 |
| `observables` | 状态 Dump、日志、库存、截图、视频、性能计数器 |
| `oracles` / `rubrics` | 确定断言、视觉 rubric 与必须人工回答的问题分开 |
| `stopConditions` | 成功、失败、卡死、预算用尽或需要人工决策时何时停止 |
| `cleanup` | 退出 Play Mode、释放预留、恢复设置、删除临时对象等 |

### 4.2 Evidence Bundle

一次非琐碎 Harness 运行应尽量把证据收在同一目录：

```text
<run-id>/
├── run.json                 # 定义、版本、Seed、状态、耗时、停止原因
├── actions.json             # 可选：语义动作或 Input 时间轨迹
├── results/                 # NUnit XML、结构化指标、存档对拍结果
├── logs/                    # Unity / Blender / Driver / Reporter 日志
├── captures/                # 截图、视频和必要的抽帧
└── review.md                # 人工尚需判断的体验、美术、授权问题
```

证据包不是为了制造文档，而是为了回答三个问题：**运行的到底是什么、为什么得出这个结论、失败能否复现**。若已有 Unity NUnit XML 或当前 `unity-validation-harness` 摘要足够，就引用现有证据，不重复包装。

## 5. 《游牧工坊》的 Harness 分层

### L0：纯模拟与资源守恒

- 固定 Seed 下 Utility AI 候选、得分、抽样和预留可复现；
- 物品只能从明确来源转移到有容量的目的地；失败不复制或吞掉资源；
- 饮水 / 进食、排泄、垃圾、污物储存、管道或人工搬运形成可核对的输入—输出链；
- 暂停、1×、加速与保存往返满足各自误差边界。

这层主要由 EditMode / PlayMode 测试和结构化 Dump 判断，不需要 VLM。

### L1：语义场景轨迹

- 用游戏公开的测试 Command 表达“设置目标、建造、分配优先级、停车、启程”；
- 每一步记录前置状态、动作、里程碑、失败原因和清理；
- 可快速到达沙尘暴、缺水、厕所满、管道断电等高价值状态，避免每次真实等待数十分钟。

这层是高速诊断接口，不能冒充玩家输入已经成立。

### L2：真实 Input Playtest

- 经 Input System 驱动大地图、建造、选择和暂停 / 加速；
- 轨迹可回放，关键节点同步截图或短视频；
- 同时读结构化状态，避免只凭画面猜库存或 AI 原因。

### L3：性能、视觉与体验

- 固定镜头和场景做性能与视觉基线；黄金图由人工批准；
- Agent / VLM 可按具体 rubric 寻找遮挡、轮廓、反馈和跨画面不一致，但结论保留原图和理由；
- 目标玩家实际试玩，观察等待时间、错误归因、主动目标、情绪和是否想继续。

只有 L3 的真实玩家反馈能支持“有趣”；L0–L2 主要支持“可靠、可理解、可复现”。

## 6. 近期采用顺序

1. **现在**：保留现有 `unity-validation-harness`；为 Blender Smoke 生成 manifest、预览和哈希；生成资产开始记录最小来源与用途。
2. **首个生活模拟 Spike**：给物品转移和生理循环建立确定性 Oracle，并增加面向开发者的状态 Dump，不先做随机 Monkey。
3. **首条完整可玩路径**：定义很薄的语义动作轨迹，再补真实 Input Action 轨迹；关键阶段同时保存状态与截图。
4. **第一个长跑**：加入固定 Seed、时限、循环 / 无进展检测、单 Editor 锁和有界重试；仍由当前任务拥有生命周期，不启动无限自治服务。
5. **垂直切片**：再评估视频证据、视觉 rubric、Unity Performance Testing / Graphics Test Framework、GitHub CI 和真机黑盒工具。

## 7. 明确不采用的做法

- 不同时运行两套 Unity MCP / REST Bridge；新工具必须填补当前 AnkleBreaker MCP 的真实缺口。
- 不因支持更多 Agent 而复制几十份 Command、Skill 和规则；公共真值仍在 `AGENTS.md`、`.agents/skills/`、文档、测试与工具。
- 不把截图存在、VLM 高分、测试进程退出码 0 或 Agent 自称完成当作充分证据。
- 不让随机 Monkey 直接承担复杂模拟游戏的主验收；它适合发现 UI 崩溃和循环，不理解玩家意图与经济守恒。
- 不默认以 bypass 权限、无限重试、无限时长运行 Agent；预算、权限和停止条件属于 Harness 的一部分。
- 不在真实任务出现前安装庞大 Skill 包或预建跨引擎“工作室操作系统”。

## 8. 后续研究问题

- 当前 Unity MCP 是否能稳定注入 Input System 事件并连续录制短视频；若不能，最小 Editor / Runtime Seam 应放在哪里？
- 语义动作轨迹如何绑定游戏稳定 ID，同时避免测试 API 泄漏到发行构建？
- 哪些视觉项适合确定性图像差异，哪些只能用多模态 rubric，如何保存人工批准与撤销记录？
- 长达 20–30 分钟的旅途应采样哪些状态和时窗，才能发现“动态壁纸”和等待问题而不录制海量视频？
- 生成模型、音效与音乐是否共享同一 provenance schema，还是需要按媒体类型增加授权、循环、响度、骨架 / 拓扑等字段？

这些问题应由实际 Spike 给答案；出现两次以上同类判断流程后，再决定创建新的 Project Skill 或独立 Harness 工具。
