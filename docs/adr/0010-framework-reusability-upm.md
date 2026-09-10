# ADR-0010：框架复用边界与 UPM 包形态

**Status:** Accepted（当前 Package 形态已落地；早期“抽包延后”内容保留为历史背景）

## Context

Framework 需要在多个 Unity 工程中复用，同时保持 Core、可选 Adapter、Editor 工具和测试的依赖方向清楚。早期源码位于项目内的 Framework 目录，直接把目录移动到 UPM 包并不能自动解决依赖声明、测试组织、源码定位和可选第三方库的发布问题。

## Decision

1. 当前仓库以根目录 package.json 作为 Package 入口，源码位于 src，公共说明位于 docs；不把游戏、教程场景、项目配置实例或业务程序集放进包。
2. Core 和 Editor 通过 asmdef 显式声明依赖，保持 autoReferenced、precompiledReferences 和可选 Adapter 的真实边界；模块地图记录删除阻塞和验证方式。
3. 第三方库由拥有它的 Adapter 或 Editor Module 接入。Core 只保存稳定 Interface；不以一个总包的开关假装实现已经可选。
4. 示例、消费工程和项目配置作为包外工程维护。需要验证消费方边界时，应使用独立工程或测试程序集，不把示例类型回写进 Framework 公共 API。
5. 是否进一步拆分为多个可发布 UPM 包，必须由真实目标平台、安装依赖和 Player BuildReport 共同证明；不按程序集数量机械拆包。
6. 单包内无条件编译的 Module 所需运行库必须由根 package.json 声明。R3、ObservableCollections、Protobuf 和 BCL 运行库采用 OpenUPM 提供的 `org.nuget.*` 分发包；HybridCLR 采用 `com.code-philosophy.hybridclr`，其 dnlib 随包提供。包源由消费工程配置，Framework 不在导入期间运行自安装脚本，也不再复制一份第三方 DLL。已用真实消费工程副本验证自动解析和相关程序集编译，完整 Editor / Player 验收仍受已记录的 Unity 版本兼容边界约束。

## Consequences

- 包可以通过 Git URL、固定 commit 或本地路径接入，目录身份不依赖某个消费工程。
- 程序集和文档都能从真实依赖图、模块审计和删除测试得到验证。
- 发布前仍需在干净工程核对 Registry、Git、NuGet 和第三方授权；包自身不能假定消费方已经安装了某个主工程依赖。
- 可选能力的安装、版本和删除由 Unity Package Manager 与项目流程负责，Framework 的审计工具只提供证据，不接管包管理。

## Related

- [ADR-0004：程序集结构与 RP 归位](0004-assembly-structure-and-rp-location.md)
- [ADR-0011：消费工程目录与第三方隔离](0011-directory-organization.md)
- [ADR-0039：Module 选择与保留证据](0039-framework-module-retention-model.md)
- [文档索引](../README.md)
