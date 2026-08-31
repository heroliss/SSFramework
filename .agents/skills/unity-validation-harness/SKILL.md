---
name: unity-validation-harness
description: 根据 Unity / SSFramework 改动面选择、执行并汇总与风险相称的编译、测试、运行时、视觉、性能或隔离构建证据。用于实现完成后的验证计划、回归验收和交付证据收口；不替代单纯的故障诊断，也不把截图或测试绿灯误当成游戏体验已经成立。
---

# Unity 验证闭环

为本次改动建立“验收目标 → 最小充分证据 → 执行 → 观察 → 判定 → 恢复”的闭环。验证范围与风险相称，不为流程完整而盲跑所有工具，也不能用 `total=0`、菜单调用成功或“已截图”冒充结果。

底层测试 job、后台焦点与 CLI 细节按 `unity-background-automation` 和 `docs/unity-mcp-tips.md`；需要视觉证据时按 `unity-screenshot` 实际打开图片检查。本 Skill 负责选择和收口，不复制它们的操作手册。

## 先定义要证明什么

1. 读取用户验收条件、当前 diff、受影响调用方、最近目录的 `AGENTS.md`，以及相关 guide / ADR。
2. 把结论写成可观察行为，例如“取消旧 owner 后迟到完成不会覆盖新状态”，而不是“代码看起来合理”。
3. 标出本轮不能自动判定的维度。好玩、审美、市场价值和授权风险需要玩家、人工或专业评审；自动化只能提供证据。
4. 保留用户未提交改动。测试需要临时数据、场景或设置时，先确认隔离和恢复边界；不得手改 `.unity` / `.prefab` YAML。

## 选择最小充分证据

| 改动面 | 通常至少需要 | 何时升级 |
|---|---|---|
| 纯 C# 算法、数据或局部契约 | 编译 + 直接 fixture | 公共 API、共享基础类型或多模块调用方受影响时扩大到调用方与全量基线 |
| 生命周期、异步、取消、释放、缓存 | 成功/失败/取消/迟到/Teardown 的定向契约 | 依赖 PlayerLoop、场景或真实 Adapter 时补 PlayMode |
| Runtime UI、场景、流程、输入 | 相关 PlayMode 或玩家路径 smoke | 视觉、焦点或物理输入属于验收条件时补截图或真实输入验证 |
| EditorWindow、菜单、生成器 | EditMode 契约 + 稳定 Console/报告 | 布局与交互状态变化时补指定 EditorWindow 截图 |
| YooAsset、HybridCLR、Luban、模块构建 | owner Module 测试 + 已有隔离菜单/报告 | 改发布边界、模块闭包或平台行为时补目标 Player / 隔离构建 |
| Shader、渲染、性能、内存 | 固定场景和配置下的采样证据 | 要声明“无回归”时必须有同设备、同构建、同窗口的 before/after 或批准基线 |
| 文档、Skill、AI 路由 | 链接、frontmatter、发现边界和专用校验 | 只有其内容改变真实 Unity 流程时才运行对应 Unity 验证 |

测试覆盖率不能单独证明行为；截图不能单独证明视觉没有回归；Editor Profiler 数字不能直接外推目标 Player。

## 执行闭环

### 1. 建立环境快照

- 记录 Unity、关键 Package、当前提交或工作树状态，以及本轮使用的场景、平台、分辨率或质量档。
- 交互式 Editor 已打开时使用当前实例，不为同一工程启动第二个 batchmode Editor。
- `.cs` 改动后等待刷新、编译和域重载稳定；以 `unity_get_compilation_errors` 的 CompilationPipeline 结果判断，不用清过的 Console 代替编译结果。Play 中发生域重载时废弃该轮运行现场，退出后重新开始。

### 2. 精确运行测试

- 定向测试先用 `unity_testing_list_tests` 按相同 `mode` 和 `nameFilter` 确认目标归属。
- fixture 传 `groupNames`，单个完整用例传 `testNames`；当前 Unity 端不消费 MCP schema 的 `filter` 别名。
- 每次 PlayMode 测试前执行菜单 `SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）`，并从 Console 确认 `[SSFramework.Automation] READY`，不能只看菜单返回 success。
- 每个测试范围只启动一次 job，保存 job id 并轮询原 job。终态必须测试总数大于零；`succeeded + total=0` 是筛选失败。
- 先跑最相关 fixture。只有改动跨公共边界、准备提交/发布、定向结果暴露耦合，或用户要求完整基线时，再升级到 EditMode + PlayMode 全量。

关闭交互式 Editor 后才使用 `Tools/run-tests.ps1` / `Tools/UnityAutomation.psm1` 的工程外流程；仍需检查 NUnit XML 存在且测试数大于零。

### 3. 补运行时和非功能证据

- UI、场景或 EditorWindow 的视觉结论调用 `unity-screenshot`，截图落 `Screenshots/`，并实际查看图片；检查完成后恢复本次临时进入的 Play 状态。
- 性能结论记录 warm-up、采样窗口、p50/p95/max、硬件、Graphics API、质量档、分辨率和构建类型。没有稳定基线时，只报告本轮观测，不声称回归通过。
- 输入测试区分程序化 Input System 设备、Game View 真实焦点和物理设备链路；业务 Command smoke 不能证明绑定仍正确。
- 构建或长任务以最终报告、产物和状态为准。MCP 超时或菜单调用成功都不是终态，不得未经检查重复启动非幂等操作。

### 4. 收口与恢复

- 检查失败是否来自产品行为、测试 Oracle、环境、筛选器或工具基础设施；未理解原因前不靠重复运行换绿。
- 不自动接受新的黄金截图、性能基线或轨迹指纹。只有确认变化有意且获得相应授权后才更新。
- 确认测试自己的存档、场景、`timeScale`、`runInBackground`、Input System 设置和临时资源已经恢复；失败时把未恢复状态作为一等结果报告。

## 交付摘要

最终结果至少包含：

```text
验收目标：要证明的玩家/调用方可观察行为
环境：Unity / Package / commit 或工作树 / 平台与关键配置
已执行：编译、测试 mode + fixture + 数量、运行时路径、截图或性能采样
结果：通过、失败或基础设施阻塞，并附 job / 报告 / 图片 / 指标位置
未覆盖：尚未验证的设备、平台、真实输入、审美、性能或授权维度
恢复状态：Play、场景、存档、设置、临时目录是否已还原
```

结论必须和证据强度匹配：定向测试通过只能证明定向范围，静态截图只能证明所见帧，业务路径 smoke 只能证明该路径，不扩大为“整个游戏已验证”。
