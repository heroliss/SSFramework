# SSFramework 文档索引

这里是 SSFramework Package 的公共文档。文档只描述框架本身的 API、模块边界、接入方式和设计取舍，不依赖某个游戏、教程工程或本机目录。

## 推荐阅读顺序

| 目的 | 先读 | 然后看 |
|---|---|---|
| 第一次接入 | [接入与升级](consuming-framework.md) | [框架使用指南](framework-guide.md) |
| 了解结构 | [README](../README.md) | [模块地图](framework-module-map.md) |
| 修改框架源码 | [根协作规则](../AGENTS.md) 与 [源码规则](../src/AGENTS.md) | 相关模块的源码、测试和 ADR |
| 运行自动化验证 | [Unity 自动化与验证](unity-mcp-tips.md) | [ADR-0036：测试预检](adr/0036-ai-playmode-preflight.md)、[ADR-0038：隔离体积探针](adr/0038-isolated-framework-build-size-probe.md) |
| 评估可选能力 | [模块地图](framework-module-map.md) | [Odin 可选集成](optional-odin-integration.md)、对应模块 ADR |

## 文档职责

- **README.md**：面向新读者的能力概览、最小接入路径和验证入口。
- **framework-guide.md**：面向使用者的 API、生命周期和常见组合方式；不承担某个项目的完整玩法教程。
- **framework-module-map.md**：程序集、依赖方向、删除边界和证据口径的结构真相。
- **consuming-framework.md**：Git / 本地包接入、版本固定、升级和兼容性检查。
- **adr/**：记录关键设计为什么成立、刻意不做什么，以及后来如何修订。
- **naming-conventions.md**：Package ID、程序集、命名空间和类型命名的边界。
- **optional-odin-integration.md**：商业 Inspector 插件的可选接入边界。
- **unity-mcp-tips.md**：与 Framework Editor 工具和测试预检有关的通用自动化注意事项。
- **[2026-09-10 审查记录](framework-review-2026-09-10.md)**：本轮范围、已修复问题、验证证据与尚待处理的分发缺口。

## ADR 编号与状态

ADR 编号按历史创建顺序保留，不要求连续。0029–0032 的项目级决策不属于当前 Framework Package，因此文件没有随包保留；空档保留以避免旧引用被重新解释为另一项决策。当前 ADR 索引是可发现性入口，具体文件中的 Status、Context、Decision 和 Consequences 才是正文真相。

| 状态 | 含义 |
|---|---|
| Accepted | 当前决策基线，代码、测试和使用文档应与之保持一致 |
| Proposed | 只记录方向或待验证方案，不代表功能已经提供 |
| Superseded | 保留历史背景，当前行为以链接到的新 ADR 为准 |

## 维护约定

1. 公共 API、程序集或 Editor 工作流变化时，同时检查本索引、使用指南、模块地图、相关 ADR 和测试入口。
2. Framework 文档不写消费项目名称、项目私有路径或迁移阶段的临时拓扑；需要说明证据时使用“真实消费工程”或“隔离验证工程”等中性称呼。
3. 只在有可复核来源时写具体版本、测试数量、性能数字和日期；历史测量必须标明适用范围，不能当成当前基线。
4. 新增链接后先检查目标文件和相对路径；删除文档前先迁移仍有价值的契约，再更新索引。
