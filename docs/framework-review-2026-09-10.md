# Framework 审查记录（2026-09-10）

类型：**Evidence**。以下结果只适用于各节列明的修订、范围与验证环境，不作为当前 API、安装基线或默认开发指令；当前接入方式见[接入与升级](consuming-framework.md)。

基线：`d953054`；修复分支：`codex/framework-review`，从干净的 `main` 创建。适合在同一分支评审这些相关修复，验证后再合并；依赖分发重构应另开分支，并先取得真实消费工程证据。

本轮完成全仓静态检查、重点运行时路径审查与可复现问题修复。**这不是全部 Unity 测试通过或所有模块逐行审查完成的声明。**

## 审查范围

| 范围 | 本轮证据 |
|---|---|
| 全部源码 | 349 个 C# 文件，以 C# 10 解析 Editor / Player 两组条件编译分支；这是语法检查，不是 Unity 语义编译 |
| 全部程序集 | 34 个 asmdef（17 个生产、17 个测试）；检查内部循环、Core / Boot / UI 后端依赖方向、Editor 平台边界及预编译依赖显式声明 |
| Unity 元数据 | 源码与 asmdef 的 `.meta` 完整；471 个 `.meta` 未发现重复 GUID |
| 重点逻辑 | Command 权限、注入计划、Container / Context 所有权、Bag / 对象池、存储 FIFO、Proto 解析、UI 栈与释放；结合相关测试核对 |
| 其余 Module | Asset / Yoo、Config、Fonts、两套 UI 后端、Bridge、Boot、Editor 与 Build 以结构、语法、依赖及部分关键入口抽查为主；未执行真实资源、场景、生成和构建矩阵 |
| 文档 | 重点核对权限表、注入说明、UI 调用、接入依赖与相关 ADR；没有把历史性能数字重新当作本轮测量 |

## 已修复问题

| 优先级 | 触发与影响 | 修复及回归入口 |
|---|---|---|
| P1 | Proto 长度先缩窄、再做整数加法校验，溢出可以绕过消息边界；高位 varint/tag 会被截断，损坏输入可能成为错误消息或触发大额分配 | 完整 64 位 varint 校验、按剩余区间检查长度、验证构造切片与字段号；envelope 校验已知字段 wire 类型。`ProtoWireTests` |
| P1 | UI 的 OnCreate / OnOpen 同步释放 owner 后，Open 继续写窗口栈或交付已销毁窗口 | 在用户 hook 后复检释放状态，停止后续发布和过渡并返回 null。`UIWindowStackTests.Open_HookDisposesOwner_DoesNotPublishWindowOrStartTransition` |
| P2 | `Close(oldWindow)` 只查类型，旧实例或其他 UI 的实例会关闭当前同类型窗口 | 实例关闭要求引用身份匹配；`Close<T>()` 保留按类型关闭。`Close_StaleOrForeignInstance_DoesNotCloseCurrentWindow` |
| P2 | OnClose 重入 CloseAll 会修改正在反向遍历的活列表，造成下标越界；新实例也可能被旧批次误关 | 按入口快照遍历、保存嵌套批次状态，忽略已关闭或被替代的实例。`CloseAll_ReentrantClose_DoesNotInvalidateTraversal`、`CloseAll_CallbackReplacesSnapshotWindow_PreservesReplacement` |
| P2 | 基类和 override 都标记 Inject 时，反射对基类的调用仍分派到 override，使初始化方法或 setter 执行两次 | 按虚槽位去重，保留 new 隐藏成员的独立调用；覆盖继承属性、方法和计划复用。`InstallBindingsInjectionTests.InjectionPlan_InheritedMembers_PreserveDistinctSlots` |
| P2 | 文档将 Command 描述为“访问一切层”，并错误要求注册 View；另有 Utility 权限、无反射开销、Container 可见性及 Boot 依赖等描述漂移 | 同步 guide、接入说明、XML 注释与 ADR，修正失效测试名和 AGENTS 编号引用 |

Command 的分层获取能力是 Model / System / Utility，没有 GetView。它可以读写 Model、调用 System、发送 Event、调用子 Command。需要更新视图时通常通过返回值、只读订阅源或事件；但 `IUIUtility.Open/Get` 本身可以返回窗口引用，所以“Command 在任何情况下都拿不到任何 View 引用”也不是准确的绝对表述。这是分层 API 的防误用约束。

## 验证结果与限制

- 本地同步验证：**29 通过、0 失败**。其中 21 项直接运行包内 ProtoWireTests；8 项用真实 InjectionPlan / UIUtility 配合替身 Context、日志与任务调度验证同步控制流。替身不验证 R3、Unity 对象语义、UniTask PlayerLoop 或线程恢复。
- 首批回归在修复前为 11 通过、10 失败；另外分别复现了两项 envelope wire 类型错误、两项 UI hook 释放错误，再修复。没有将“源码推断”代替这些失败证据。
- Editor / Player 两组语法检查、程序集图检查和元数据检查通过。解析基线使用 C# 10，不证明某个消费工程的 Unity 编译器配置已经满足它。
- 本轮没有可用的、已接入当前 Framework 包并配齐依赖的 Unity 消费测试环境，**未运行 Unity EditMode / PlayMode 全套测试、实际 Module 删除编译、YooAsset 加载、UGUI / Toolkit 渲染、HybridCLR 或 Player 构建**。
- 公共方法签名不变；错误输入更早失败，重复初始化被消除，旧窗口关闭请求不再作用于替代实例。这些行为收紧均已在接口旁和 guide 中说明。

合并前的消费工程回归入口：`LayerAccessTests`、`ArchitectureTests`、`InstallBindingsInjectionTests`、`ContainerContractTests`、`ProtoWireTests`，以及 UI 的窗口栈、过渡、线程边界与 OpenRequired 测试。预检和取证流程见 [Unity 自动化与验证](unity-mcp-tips.md)。

## 尚待处理：完整包的依赖分发

**初次静态审查阶段的 P1（对应 `182d967`，后续进展见下节）。** `package.json` 没有覆盖全部源码的外部依赖；例如 Boot / HybridCLR Editor 的直接 HybridCLR 引用未列入清单，UI / Proto 等还依赖外部预编译 DLL。业务只引用 Core 不会阻止同包其他无条件程序集编译。当时只能将完整依赖来源由消费方提供，不能承诺“干净工程添加 Git URL 即可编译”。

已补充[依赖前置条件](consuming-framework.md#依赖前置条件)，并保留现有包结构。依据仓库要求，版本选择、传递依赖、许可证和拆包/删除策略必须先在真实消费方验证，再修改分发方案。静态审查无法为未知来源的 DLL 选择可靠版本，也没有用关闭 asmdef 错误检查掩盖缺失。

下一步优先完成干净消费工程的安装与编译证据，再锁定包依赖，最后运行全部测试和目标 Player 构建。已有模块边界清楚，本轮没有证据支持先进行大规模重命名、额外抽象或模块拆分。

## 消费方接入复核

真实消费方 MoonBase 在 Unity `6000.6.0f1` 中手动安装 `182d967` 后，Editor 日志包含 429 条去重后的 C# 编译错误：R3 适配层 414 条（R3 / BCL 运行库缺失）、YooAsset Editor 14 条（已移除的 UxmlFactory / UxmlTraits）、Boot 1 条（HybridCLR 缺失）。另有 59 条 Framework 资产缺少 `.meta` 的警告。初次审查只核对 C# / asmdef 的元数据，漏掉了根目录、docs 与 src 文件夹；该检查范围不足以证明完整 Git 包可导入。

接入修复在 `codex/package-installation` 分支进行。验证使用消费方 Assets、ProjectSettings、Packages 的独立副本与包的独立工作副本，未修改 MoonBase 的安装配置。先在副本补齐并验证，再回流 Framework；副本和下载产物位于忽略的 Temp 目录，不进入包。

| 修复 | 证据与范围 |
|---|---|
| `0.1.1` 声明 R3 `1.3.1`、ObservableCollections / R3 适配 `3.3.4`、Google.Protobuf `3.36.1`、TimeProvider `8.0.0` 的 `org.nuget.*` 包，以及 HybridCLR `8.14.1` | OpenUPM 指定版本与传递依赖均可解析；Unity 在消费副本中实际完成安装，Bcl.AsyncInterfaces、Channels 等随依赖图安装，dnlib 由 HybridCLR 自带 |
| 补齐 59 份元数据，移除孤立的 `src/Demo.meta` | 缺失路径与消费方日志逐一对应，新增 GUID 由 Unity 在本地包副本导入时生成；原有资产 GUID 保留，避免只读 PackageCache 生成失败与空 Demo 目录复活 |
| 将 Editor 的对象身份与缓存键迁移为 `EntityId` | 包括 Context 诊断分组、迁移去重、生成输出标识与 Proto 预览缓存；StringBuilder 显式使用 EntityId.ToString，避免隐式转 int。UGUI Hierarchy 在 6.6 使用新回调，保留 6.3 的入口适配 |

验证结果：

- 在 `6000.6.0f1` 消费副本的 Unity 批处理编译中，R3.Unity、HybridCLR 及全部 10 个 Framework Runtime 程序集成功产出；原来的依赖缺失错误消失。Runtime 程序集在此为 Editor 目标的编译产物，**不是 Player 构建证明**。
- 补齐依赖后进一步暴露了 Framework Editor 的旧对象身份 API 错误。修复后使用该副本由 Unity 生成的原始编译响应文件、引用程序集和 Roslyn 编译器，直接编译通过 `Game.Framework.Editor`、`Config.Editor`、`Network.Proto.Editor`、`Fonts.Editor`、`UI.UGui.Editor` 五个程序集。未修改第三方源码或禁用签名 / 程序集引用检查。
- 349 个 C# 文件的两组条件语法检查和 34 个程序集结构检查通过；按待提交包内容检查，529 份元数据 GUID 唯一，导入资产无缺失元数据，也无孤立元数据。新增文件夹保留明确的 `folderAsset` 标记；修改文档的本地链接与实际消费副本锁定的第三方版本一致性检查通过。
- 完整 Unity 6.6 编译仍被 YooAsset 3.0.5 的 14 条旧 UI API 错误阻塞，依赖 YooAsset.Editor 的 Build.Editor / Build.HybridCLR.Editor 尚未验证。OpenUPM 最新版及核对时的上游默认分支 `b989d1505d0a9506e89121a5d91f5fef6608edea` 均仍使用该旧 API。
- 额外创建并启动隔离 Unity 测试工程的操作被自动审批以 `blocked by policy` 拒绝，未返回更具体原因；本次未执行 EditMode / PlayMode 测试。改为完成上述现有副本的直接编译验证，没有将它称作测试通过。
- 本机当前仅有 Unity 6.6；推荐接入目标仍为 `6000.3.22f1`，本轮 **尚未取得 6.3 LTS 的完整编译、测试或热更新构建证据**。需由使用者手动在目标版本接入验收，不能立即将修复分支视为已验证发布版或合并 main。

手动升级时，在已有四项 OpenUPM Scope 上增加 `org.nuget` 与 `com.code-philosophy.hybridclr`，然后更新到包含 `0.1.1` 的固定 commit。仅增加 Scope 而继续引用旧 `182d967` 不会安装新增依赖。完整操作见[接入文档](consuming-framework.md)。

## Unity 6.3 重建后的接入准备复核

后续真实消费工程已重新创建为 `6000.3.23f1`，本机也已安装该 Editor；这替代上节“本机仅有 6.6”的环境状态。检查时尚未安装 Framework，也尚未配置 OpenUPM，因此 **6.3 的完整 Framework 编译、EditMode / PlayMode、Player 与 HybridCLR 构建仍待验收**。

- 消费工程已安装 Entities `1.4.8`、Entities Graphics `1.4.21`、Burst `1.8.30`、Input System `1.20.0`；新输入后端、Linear、Standalone IL2CPP、Force Text、Visible Meta Files 已启用。实际仍为 Built-in 渲染管线，与 Entities Graphics 的 SRP 要求不符。渲染选择属于消费方准备工作，不写入 Framework 默认配置。
- 清空工作区保留了原有 Git 历史，但删掉了 `.gitignore` 与 `.gitattributes`。已从消费方自身 HEAD 恢复这两份文件，并确认 Library、Logs、Temp、UserSettings 再次被忽略；未恢复旧 Unity 工程设置或删除当前资产。
- 提供 Unity 外部的包源配置工具，先在临时位置开发，以真实消费清单做只读预览、在其原始字节副本上执行写入，再回流到 `Tools~`。检查覆盖默认预览、WhatIf、原子备份、幂等、旧四项升级、命名空间覆盖、竞争包源、错误清单、Unicode / BOM、Unity 文件锁，以及根 package.json 依赖覆盖。此证据验证配置合并，**不代表 UPM 解析、签名、Unity 编译或游戏运行通过**。
- 修正快速开始中的无 revision Git URL：修复尚未合入 main 时，该地址会引导使用者安装旧依赖声明。文档改为包含 `0.1.1` 修复的固定候选提交，并明确验收状态。
- 补充 Framework 与 ECS 的职责边界，明确尚无内置 ECS Adapter、不能将实体仿真写成每帧 Command，也不能把配置选择作为性能验证。未增加运行时 API 或 Entities / Input System 依赖。

当前继续在 `codex/package-installation` 上提交接入工具与说明。真实消费方仍由使用者手动配置和安装；完成目标版本验收后再合并 main。
