# ADR-0011：消费工程目录与第三方隔离

**Status:** Accepted（消费工程约定，不约束 Framework Package 内部布局）

## Context

Unity 工程同时包含第一方代码、项目配置、场景和第三方插件。若把这些内容与可复用 Framework 源码混在同一个目录，抽包、升级和删除依赖都会变得不清楚。本 ADR 记录消费工程应遵循的边界；Framework Package 自身只维护可复用源码、文档、测试和必要的 Editor 工具。

## Decision

- Framework 源码和测试留在 Package 内；项目的场景、Prefab、玩法代码、构建 Profile、收集器配置和 ScriptableObject 实例由消费工程拥有。
- Profile 按类型和所属 Module 发现，不把某个消费工程的固定资产路径写进 Framework 公共 API 或通用工具。
- 需要搬迁 Unity 资产时使用 AssetDatabase 或项目自己的 Editor 迁移器，以保留 meta / GUID；不手改 Scene 或 Prefab YAML。
- 第三方依赖优先通过 UPM 或独立 Adapter 接入；不要为了方便把插件文件复制进 Framework Package。
- 截图、构建产物和本机缓存不属于 Framework 文档或源码提交；需要分享证据时保存可复核的报告和经过筛选的图片。

## Consequences

- 消费工程可以按自己的场景、艺术资源和发布链组织目录，而不改变 Framework 的 Package 身份。
- Profile、ProjectSettings 和第三方插件的生命周期由真正拥有它们的工程负责，Framework 只提供类型、Interface 和 Editor 接缝。
- 资产搬迁需要 Unity Editor 验证，换来序列化引用和 GUID 的可追踪性。
- 需要跨项目复用的规则应先回到 Framework 的 Interface、模块地图或 ADR，而不是复制某个项目目录。

## Related

- [ADR-0010：框架复用边界与 UPM 包形态](0010-framework-reusability-upm.md)
- [命名约定](../naming-conventions.md)