# 架构决策记录（ADR）

记录 SSFramework 的关键设计决策——**为什么这样设计**，供人类与 AI 追溯。改动既有决策时更新对应 ADR 的 Status 并补充新 ADR，不要静默推翻。

格式：每个 ADR 含 `Status`（Accepted / Proposed / Superseded）、`Context`（背景/问题）、`Decision`（决策）、`Consequences`（后果/权衡）。

| # | 决策 | Status |
|---|---|---|
| [0001](0001-five-layers-and-permission-interfaces.md) | 五层 MVCS + 编译期权限接口 | Accepted |
| [0002](0002-commands-receive-icommandcontext.md) | Command 接收 `ICommandContext`（受限）而非 `GameContext` | Accepted |
| [0003](0003-custom-di-container.md) | 自研精简 DI 容器 + 主线程独占契约 | Accepted |
| [0004](0004-assembly-structure-and-rp-location.md) | 程序集结构与 `RP<T>` 归位 | Accepted |
| [0005](0005-no-runtime-hot-swap-of-layers.md) | 运行时不热替换已注册层 | Accepted |
| [0006](0006-odin-dependency.md) | Odin 硬依赖现状与未来解耦 | Accepted |
| [0007](0007-custom-object-pool.md) | 自研对象池替代第三方库 | Accepted（MVP） |
| [0008](0008-hybridclr-integration.md) | HybridCLR 热更：AOT/热更程序集分界 | Proposed |
| [0009](0009-luban-integration.md) | Luban 配置表：构建期生成 + 运行期经资源系统加载 | Proposed |
| [0010](0010-framework-reusability-upm.md) | 框架复用边界与 UPM 抽包路线 | Accepted |
| [0011](0011-directory-organization.md) | 项目目录组织与第三方隔离 | Accepted |
| [0012](0012-yooasset-3-migration.md) | YooAsset 3.0 迁移：先用官方兼容层 | Accepted |
