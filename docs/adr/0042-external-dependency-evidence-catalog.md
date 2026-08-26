# ADR-0042：第三方依赖证据目录保持只读并与 UPM 正交

**Status:** Accepted

## Context

Module 审计过去只在每个 what-if Profile 里重复列出外部程序集名称和原始字节。用户仍无法从一处回答：依赖从哪里安装、谁在当前代码中使用、哪些 asmdef 会阻塞删除、它是否只服务某个可选 Module，以及移除后应验证什么。直接把 Package Manager 的安装/卸载按钮搬进框架窗口，会把版本解析、Git/Registry 凭据、间接依赖和项目代码迁移混成一个不安全的“启用开关”。

同时，`PackageInfo.isDirectDependency` 只表示 manifest 解析层级；当前已编译 DLL 的引用、asmdef 声明、UnityLinker 根、热更 Profile 和最终 Player 保留是不同事实。任何一层缺失都不能推导 `SafeToRemove`。

## Decision

在 `FrameworkModuleAudit` 内建立单一 `ExternalDependencyEvidence` Module，窗口与文本报告只消费该模型：

1. `FrameworkModuleSourceCatalog` 是 Package 来源的唯一 owner。它把 Unity 的 BuiltIn、Embedded、Git、Local、LocalTarball、Registry、Unknown 映射为框架稳定词汇，并单独保存 manifest 的直接/间接关系；Assets 使用 `ProjectAssets / NotApplicable` 语义。
2. 目录不枚举“所有已安装内容”，而以 Framework 与项目 Assets 的一方 Player / Editor 消费者及 what-if Profile 为种子，再沿平台范围相交的外部 AssemblyRef 正向扩展。当前 Player/Editor DLL 元数据边与全部有效 asmdef 声明边分别记录；预编译 DLL 到另一个预编译 DLL 的 AssemblyRef 也进入传递图，没有一方种子的 Package 内部边不会单独冒充项目依赖。`overrideReferences:false` 的预编译条目无效，重命名 DLL 按内部 AssemblyName 还原。
3. 同一已注册 Package 的程序集按 package name 聚合；Assets 预编译 DLL 保留所有物理变体、Editor 兼容性与完整 BuildTarget 集合，只有可证明平台集合互斥的同名实现才视为合法变体。Player 编译输出只产生 Player 边，Editor 编译输出只产生 Editor 边，Test 程序集保留更窄的 Tests 边；Tests 可消费 Editor 依赖，但 Editor 快照不能反向冒充 Player 证据。当前引入者回溯继续传播平台交集，不把 Editor-only 与当前 Player 变体串成一条不存在的路径。无法还原或来源冲突的程序集逐项产生证据问题，不按名称猜供应商或 Adapter 角色。
4. 用途只分为 Core 基础、单一可选 Runtime Module、Editor 工具、项目消费者、共享/混合或 Unknown。角色由外部依赖链回溯到的首个一方程序集决定；Profile 只描述传播范围与体积，不能把上层入口重复算成引入者。静态结论使用 `RequiredByCore`、`RemoveWithOptionalModuleCandidate`、`RemoveWithEditorToolCandidate`、`SharedConsumerMigrationRequired`、`ProjectConsumerMigrationRequired` 或 `ReviewRequired`；不暴露 `SafeToRemove` 布尔值。
5. 任一 asmdef、GUID、有效 precompiled reference、Editor DLL 或来源扫描缺口都会进入结构化 issue，并把受影响依赖组的删除状态收紧为 `ReviewRequired`，但不会抹掉已经成立的角色事实。可定位到 AssemblyName 的问题只作用于包含该程序集的组；无法定位的全局扫描缺口才保守收紧全部组。what-if 原始字节按 Profile key 独立保存，目录摘要只把其中的最大档位标成“最高档位”，不会把多个互斥 Profile 的程序集最大值拼成一个不存在的总量；“磁盘上已安装的预编译 DLL 字节”则按去重后的实际物理文件求和，是另一种事实。未进入 Profile 的 Editor 依赖显示“未测得”，可另行提示已安装文件大小，但不伪装成 `0 B` 或玩家包体。目标平台 Player BuildReport、相关 EditMode/PlayMode 路径及反射/热更回归仍是最终验证。
6. 审计窗口保持只读。它提供来源定位、证据复制和渐进展开；Package 的安装、版本选择与卸载继续由 Unity Package Manager 负责，框架不调用 `Client.Add/Remove`。

## Consequences

- 同一份证据同时提升窗口、文本报告、测试与 AI 排查的 Locality；Profile 卡只引用依赖组，不再重复拥有程序集级解释。
- Odin、YooAsset、R3、UniTask、NuGet 聚合包等都能按真实消费者与安装形态解释，而无需把某个当前项目特例写进通用工具文案。
- “可随 Module 评估移除”是保守候选，不承诺资源、反射、序列化或最终包体已经安全。
- 首次冷扫描需要读取 Editor DLL 元数据；后续使用文件长度/写入时间缓存。若规模继续增长，应增量缓存证据输入，而不是删掉 Editor 消费层换取假快。

## Rejected alternatives

- **在审计窗口提供任意 Package 的启用/卸载按钮：**与 UPM 职责重叠，也无法原子处理代码迁移、热更配置和版本控制。
- **用 DLL/命名空间前缀猜供应商或 Adapter：**重命名、聚合包和同名平台变体会产生错误归属。
- **只看当前 Player DLL：**会漏掉 Editor 工具与暂未调用但仍阻塞物理删除的 asmdef 声明。
- **只看 asmdef 配置：**会把无效 precompiled 字段和 auto-reference 消费误当真实边，且无法解释传递闭包。

关联：[ADR-0039](0039-framework-module-retention-model.md)、[ADR-0040](0040-upm-aware-module-source-catalog.md)、[ADR-0041](0041-module-dependency-integrity.md)。
