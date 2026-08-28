# ADR-0044：Unity CLI 作为工程外自动化 Adapter

- **Status**：Accepted
- **Date**：2026-08-28

## Context

Unity Hub 3.21 自动安装了实验性的独立 Unity CLI。它能读取工程版本、发现 / 安装 Editor 与 Module、运行 headless test / build / run；安装 `com.unity.pipeline` 后还能连接当前 Editor 并执行结构化命令。

项目原有三条自动化路径各有明确语义：当前 Editor 由第三方 MCP 和稳定菜单驱动；关闭 Editor 后由 `Tools/run-tests.ps1` 直接启动 batchmode；隔离体积探针使用当前 `EditorApplication.applicationPath` 启动子工程。资源构建 workflow 又单独硬编码 Editor 路径，已经从项目要求的 `6000.3.22f1` 漂移为 `6000.3.14f1`。Editor 定位知识重复，缺少 Locality。

同时，CLI 1.0.0-beta.5 与 Pipeline 0.5.0-exp.1 仍是 Experimental。若为了追逐新入口直接替换所有自动化或让 Runtime / 通用 Editor Module 引用 Pipeline，会把一个外部启动问题扩散成框架硬依赖，并复制既有 MCP 的能力。

## Decision

1. 新增 `Tools/UnityAutomation.psm1`，拥有“读取项目精确版本 → 选择启动 Adapter → 核对二进制 → 同步运行 → 返回退出码”的工程外自动化 Seam。
2. `Auto` 模式在没有显式 Editor 路径时优先使用 Unity CLI：以 `unity editors path` 解析并核对 `ProjectVersion` 与 revision，再由 `unity run` 启动；CLI 不可用时回退 Hub 路径 / 注册表发现。显式 `-UnityPath` / `UNITY_EDITOR_PATH` 始终选择 Direct Adapter。
3. 通用启动与测试保持两个有语义的 Interface：`Invoke-UnityEditor` 的 CLI Adapter 移除 `unity run` 自己拥有的 `-batchmode`、`-quit`、`-projectPath`，Direct Adapter 保留原始参数并统一做 Windows command-line quoting；`Invoke-UnityTests` 分别映射到 `unity test` 与 `Unity.exe -runTests`，共同产出 NUnit XML。通用 CLI run 收到 `-runTests` 时会退出 0 但不执行测试，Module 明确拒绝该误用。
4. `Tools/run-tests.ps1` 与资源构建 workflow 复用该 Module。删除 workflow 的机器专属版本硬编码；退出码之外仍检查 NUnit XML / 构建产物，拒绝空跑假绿。
5. 两个 headless Interface 在共享 Module 内统一拒绝 `Temp/UnityLockfile`，但绝不自动删除；锁检查先于 stale XML / log 清理，拒绝启动时保留上一轮证据。测试脚本把启动、目录与 XML 解析异常归一为基础设施退出码 2。
6. 自动化不隐式安装 / 升级 Editor 或平台 Module，不删除工程锁，不在失败后换用另一 Unity 版本。
7. 不改写 Build Size Probe：它从当前 Editor 得到绝对路径，版本真值已经具有最高 Locality。
8. 当前不把 `com.unity.pipeline` 加入项目，也不替换第三方 Unity MCP。若未来需要 Pipeline 自定义命令，使用可物理删除的 Editor-only Adapter Module 调用既有项目 Implementation；Framework Runtime 与既有 Editor Module 不反向依赖它。

## Consequences

### 收益

- ProjectVersion 成为测试与 CI 的单一版本真值，Hub / CLI 安装布局变化集中在一个 Module。
- 新 Hub 环境自动获得 CLI 的精确版本发现、非交互启动和错误分类；旧环境仍可直接运行。
- 操作专属 Interface 保留自己的终态证据：测试看 NUnit XML，资源构建看退出码与产物，不被压成一个浅命令总线。
- 当前 Editor 的语义工具、PlayMode 保存预检、无焦点 job 和隔离构建证据保持稳定。
- Pipeline 将来只有形成第二个真实 Adapter 时才引入，不污染 Runtime 与最小消费工程。

### 代价与边界

- Auto 在装有可用 CLI 的机器上会经过一层实验性 launcher；可用 `-Adapter Direct` 快速隔离问题。
- CLI 不能绕过同工程锁，也不能替代原生模态框、真实键鼠焦点或桌面窗口管理。
- Pipeline 内置命令与第三方 MCP 有明显重叠；在 token、稳定性、队列、多实例与删除测试没有更好证据前，不做默认迁移。
- CLI / Pipeline 升级后必须复核命令参数、退出码、同步等待与 `CliCommand` API，不能只依据旧文档继续运行。
