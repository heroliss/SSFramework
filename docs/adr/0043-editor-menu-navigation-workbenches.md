# ADR-0043：Editor 菜单只导航，副作用操作进入 Module 工作台

- **Status**：Accepted
- **Date**：2026-08-28

## Context

`SSFramework` 顶部菜单曾同时承载窗口导航、配置定位、代码生成、资源构建、清缓存、部署、启动外部进程和本机会话开关。用户无法在点击前看到完整用途与影响；新用户容易把“菜单项”误解为安全的导航，而某些看似只读的入口还会在 `Resolve()` 或目录打开过程中暗中创建项目资产。

这也削弱了 AI 自动化：原生保存确认或长耗时任务一旦被误触，会占住 Unity 主线程/MCP 队列；散落菜单各自实现 Play、编译与导入门禁，反馈格式也不一致。

## Decision

1. 人工顶部 `SSFramework/` 菜单只打开 `EditorWindow`。会写项目、生成代码、构建、清理、部署或启动外部进程的动作，进入所属 Module 的工作台按钮。
2. 工作台必须在按钮附近解释用途、前置条件、主要影响和失败后的下一步；按钮禁用只改善体验，动作 Implementation 仍调用 `FrameworkEditorOperationGate` 二次检查。
3. `FrameworkToolsWindow` 是意图导航 hub，不复制 Module 的业务逻辑。可选 Editor Module 通过 `FrameworkToolRegistry` 登记描述符；删除整个 Module 后，其卡片自然消失。
4. `FrameworkConfigOverviewWindow` 保持只读配置发现 hub。只读/导航路径使用 `TryResolve`，缺配置时显示显式“创建”按钮；查看目录不创建空目录。
5. 保留两类例外：
   - `Assets/SSFramework`、`GameObject/SSFramework` 等拥有真实选择上下文的操作；
   - `SSFramework/诊断/AI 自动化/*` 下由 ADR 锁定路径、供 MCP/CI 使用的稳定机器 Interface。
   配置驱动的 `SSFramework/场景/*` 动态项也保留，因为它们只执行明确导航，并已有保存/退出 Play 安全语义。
6. `FrameworkMenuContractTests` 反射全部 `[MenuItem]`：除自动化白名单外，所有人工 `SSFramework/` 顶部入口的声明类型必须是 `EditorWindow`，执行路径必须唯一。
7. Demo 特有维护入口归 `SSFramework/Demo 教学/维护与校验`，不注册进通用工具中心。
8. 共用的 `FrameworkProjectPath` 在写盘前把配置路径规范化并验证工程 / `Assets` 边界；只检查
   `StartsWith("Assets/")` 不足以阻止 `Assets/../..`。具有整理或清理语义的生成器还必须声明输出所有权：
   Protobuf / Luban 拒绝相同或嵌套目录，服务安装器拒绝重复文件，避免后执行配置覆盖前一份产物。
9. 资源包名与版本号同时是磁盘目录和 CDN URL 段，统一经 `FrameworkBuildArtifactPath` 限制为可移植叶子名；
   构建与部署在任何递归清理前再次证明目标是声明根目录的直接子项，并按大小写不敏感口径拒绝重复包名。

## Consequences

- 菜单树从“命令清单”变成可预测的导航目录；新用户在执行前能理解步骤与风险。
- Module 的交互说明与 Implementation 保持 Locality，Core 只持有路径/注册 Seam，不反向引用可选模块。
- 常用操作多一次“打开窗口”的动作，但窗口可连续执行同一流水线并保留上下文，整体操作成本更低。
- 既有人工菜单字符串发生迁移；Demo、guide 和生成代码注释必须同步。机器自动化路径保持不变，避免破坏 MCP/CI。
- 错误输出路径现在在任何目录创建、覆盖或清理前失败；已有把输出写到 `Assets` 外的配置需要迁回项目资产目录。
- 本决策不引入通用 command bus，也不替代 Unity Package Manager；窗口仍调用既有 Builder/Generator Implementation。

## Alternatives considered

- **保留即时菜单并逐项加确认框**：会制造大量模态交互，反而更容易阻塞人工与 MCP；安全信息仍难在点击前阅读。
- **把全部动作塞进一个巨型窗口**：破坏 Module Locality，可选模块删除后需要中央窗口维护字符串分支；工具中心应只导航。
- **所有操作都强制经窗口，包括机器自动化**：会让 MCP/CI 依赖 GUI 状态，破坏 ADR-0036/0038 已建立的稳定自动化 Seam。
