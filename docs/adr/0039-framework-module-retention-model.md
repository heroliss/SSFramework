# ADR-0039：Framework Module 选择与保留证据模型

**Status:** Accepted（2026-08-25）

## Context

Framework 的 Runtime 已按 Core、资源 Adapter、配置、字体、网络序列化、UI Core 与两套 UI 后端拆成独立 asmdef，且全部使用 `autoReferenced:false`。这建立了显式依赖方向，但容易被误解成“业务没有引用的程序集会自动从包里消失”。Unity 中至少还有四套独立机制参与结果：Player 编译图、UnityLinker 根、HybridCLR 热更 DLL 清单和最终平台构建。

本项目当前还存在一个重要的结构性约束：可选 Runtime Module 即使 `autoReferenced:false` 也仍参与 Player 编译，并且都引用 Core。只要 Core 属于热更程序集，这些仍在编译图里的引用方就不能单独留在 AOT，否则会违反“AOT 不得引用热更程序集”。因此“把 Fonts / Bridge 从热更 Profile 取消”不是独立开关；要真正不部署它，必须让 Module 同时退出 Player 编译图，或把其热更依赖退回 AOT。

另外，Module 目录与全局目录中的 `link.xml` 可能成为无条件 UnityLinker 根；`Assets/HybridCLRGenerate/link.xml` 又是派生产物，不能与可手工维护规则混为一谈。只显示 asmdef 闭包并给出“健康”结论会隐藏这些保留原因。

## Decision

### 1. 将五种状态保持正交

Framework 工具和教学不得使用一个“已启用”布尔值合并以下状态：

1. **源码存在 / Package 已安装**：决定文件、导入器和 asmdef 是否存在。
2. **参与 Player 编译**：当前平台的 asmdef 是否进入 `CompilationPipeline.GetAssemblies(Player)`；`autoReferenced:false` 不等于不编译。
3. **存在真实消费者**：已编译 DLL 元数据是否直接引用该 Module；Framework 消费者与项目消费者分开解释。
4. **存在保留或部署根**：场景、资源、反射、`link.xml`，以及 HybridCLR Profile 都可能让代码留下；热更构建按程序集部署完整 DLL，不走成员级 UnityLinker。
5. **最终 Player 证据**：UnityLinker、IL2CPP、引擎模块、压缩与资源共同决定，只能由目标平台 BuildReport / 发布产物回答。

工具可以汇总状态，但不能从前四项中的任意单项断言最终安装包一定包含或不包含某个 Module。

### 2. 深化现有 Module Audit，不建立第二份模块注册表

`SSFramework/诊断/模块裁剪审计` 继续从实际 Player 编译图、asmdef、DLL 元数据、热更 Profile，以及项目 Assets 与全部已注册 Package 中的 `link.xml` 派生 Catalog，不新增“已安装 Module”资产，也不让用户重复维护依赖。asmdef、linker 规则与模板的读取统一经过 `FrameworkModuleSourceCatalog`；稳定 Asset Path 用于定位和报告，真实 Physical Path 用于 I/O，因此 registry/Git 包位于 `Library/PackageCache` 时不会被误判为缺失。详见 ADR-0040。

Build Editor Module 额外提供只读派生证据：比较唯一 Profile 与 HybridCLRSettings、复用代码包构建门禁校验 Generate stamp，并把当前热更拓扑顺序及 `AOTGenericReferences.PatchedAOTAssemblyList` 与 `Assets/HotUpdateDlls` 中转 manifest、实际文件互相核对。通用 Editor 通过反射读取这个可删除 Module，不建立编译期反向依赖；空 Profile 不强制 Generate，但审计会检查启用场景是否仍依赖 `HotUpdateLauncher`：保留 Launcher 时仍要求其 Player 分支读取的空清单 CodePackage，只有直接 AOT composition root 才把中转视为可选。缺失 / 重复 Profile 不冒充明确配置。中转一致只证明结构与文件存在，不证明 DLL 内容相对源码新鲜，也不冒充 YooAsset bundle、Deploy 目录或 CDN 已更新。

每个 Runtime Module 显示：

- 已在当前 Player 编译图发现，以及 `autoReferenced` 是否仅关闭了预定义程序集的隐式引用；Module 退出该编译图后不会继续以“未参与”卡片出现，界面也不得把 `autoReferenced:false` 称为“按需启用”“消费方已选择”或“自动裁剪”；
- 当前已编译 DLL 快照的 Framework / 项目元数据消费者，以及完整 asmdef 图中的删除阻塞者（无论是否进入 Player）；Unity 6000 的 `outputPath` 可能指向 Editor DLL 变体，因此目标 Player 消费边仍由目标平台构建确认；
- 自身的 Framework 直接依赖；完整闭包在任意 Module what-if 中展开；
- 是否位于热更 Profile，以及哪些热更依赖造成结构性传播；
- 指向它、或由它拥有的 `link.xml` 规则；
- 可复制的安全移除顺序。

常用 Core / UGUI / Toolkit 档位继续保留；同时为任意 Runtime Module 生成 what-if 入口闭包，并把同一组结构化 Profile 交给隔离构建体积探针。what-if 只回答“以它为入口会带上什么”，不是全局开关。

删除边界由同一 Catalog 机器派生：Core 的 asmdef 声明与当前 DLL 元数据闭包都不得包含任意可选 Framework Player Module（包括 Boot）；Boot 若参与 Player 编译，两种闭包都不得接触 Framework Runtime；UGUI / Toolkit 仍额外互相隔离并默认不带 Bridge。检查名称、解释、窗口与文本报告消费同一结构化结果，不在测试中另写一份特例真相。

### 3. 全局、第三方和生成的 linker 规则只读追踪

Module 目录内的无条件 `link.xml` 进入 Module 风险提示。Module 目录外的规则另列为全局证据：

- `Assets/HybridCLRGenerate/` 标记为生成物，提示修改 Profile / HybridCLR 来源后重新 Generate，不建议手改；
- 第三方插件规则只报告来源与范围，不越过升级边界直接修改；
- 条件规则与无条件根明确区分。

当前不自动把无条件保留改成 `ignoreIfUnreferenced`。反射 Adapter（尤其 YooAsset Provider）必须先建立显式注册根，并通过目标平台 IL2CPP 回归，才能收窄规则。

### 4. 移除是一项结构事务，不提供 `SetEnabled(bool)`

安全移除顺序是：

1. 迁移项目和上层 Framework 的真实消费者；
2. 若 Module 受热更依赖传播约束，把“删除 / 卸载使其退出 Player 编译图”和“从 Profile 移除”作为同一次结构变更，不在中间状态执行同步；
3. 让 Module 自有 `link.xml` 随目录消失，或在保留源码时单独验证条件保留；
4. 在最终编译图上同步 HybridCLRSettings、重新 Generate、构建代码包；
5. 运行 Module Audit、Unity 测试和目标平台真实构建。

工具先给证据、阻塞原因和清单，不自动删目录、改 manifest 或批量切 define。删除与包配置属于可审查的代码变更，不应藏在一个不可表达中间状态的 Toggle 后面。

### 5. 与 Unity Package Manager 分工，不自制 Package Manager

asmdef 管**编译依赖边界**，UnityLinker 管**成员裁剪**，HybridCLR Profile 管**热更部署集合**，UPM 管**源码包、版本和包依赖**。四者互补，不互相替代。

当前 Framework 仓库仍位于项目 `Assets` 下，但 Module Audit 和体积探针已经能读取已安装 UPM 源码。它们只读说明依赖与移除条件，不接管 UPM 的安装、卸载、版本解析、registry 或 lockfile。等某个删除边界在真实项目中长期稳定，再按 ADR-0010 把粗粒度 Module 抽成独立 UPM package，由 Package Manager 负责安装和传递依赖；Module Audit 仍负责项目级真实消费者、热更与 linker 证据。

## Consequences

- ✅ 新手能区分“没写引用”“没进热更清单”和“最终没进包”，不再把一个机制的绿灯误当全链路结论。
- ✅ `autoReferenced:false` 被准确解释为预定义程序集引用规则；所有已存在 Runtime Module 仍参与当前 Player 编译的事实不再被“按需选择”文案遮蔽。
- ✅ Core / Boot 的删除边界覆盖任意新增 Runtime Module，不必在每次增加 Module 后补一条名称特例。
- ✅ Module Catalog 从真实构建输入派生，窗口、文本报告、测试与隔离探针共享同一模型，保持 locality。
- ✅ 任意 Module 都能做闭包与隔离构建 what-if，不再把可拆卸设计局限在 UGUI / Toolkit。
- ✅ 热更传播被显式说明，避免给出会被 `HotUpdateAssemblyGraph` 拒绝的操作顺序。
- ✅ UPM 保持粗粒度分发职责，Framework 不重复实现版本与依赖管理。
- ⚠ 静态元数据看不到字符串反射、场景与资源根；工具必须保留“未知，需真实构建”的诚实状态。
- ⚠ 审计能提前发现 Profile、HybridCLRSettings、Generate stamp、热更拓扑 / AOT 补元数据清单与本地 DLL 中转目录漂移；热更 DLL 是否包含最新源码、已构建 YooAsset bundle、Deploy 目录与 CDN 的真实版本仍由编译 / 发布流水线验证。
- ⚠ 物理删除 / UPM 分包比 Toggle 更明确，但属于代码结构变更，需要版本控制与回归测试。

## Alternatives considered

- **为每个 Module 提供全局启用 Toggle / scripting define**：拒绝。它会制造隐藏的组合矩阵、场景 Missing Script 风险和第二份依赖真相，也无法表达热更与 linker 中间状态。
- **只依赖 Unity 自动裁剪**：拒绝。热更 DLL 按程序集完整部署，无条件 `link.xml` 也会成为根；静态链接只覆盖最终链路的一部分。
- **在 Framework 内实现安装器、自动改 manifest 和删除目录**：拒绝。与 UPM 职责重叠，且一次误判可能破坏业务或第三方资产依赖。
- **立即把所有 Runtime Module 拆成独立 UPM 包**：暂缓。先用删除测试和目标平台体积证据证明稳定、值得维护的粗粒度边界，再按 ADR-0010 抽包。

## Related

- ADR-0008（HybridCLR 程序集约束）
- ADR-0010（UPM 抽包路线）
- ADR-0027（列表绑定 Module 粒度）
- ADR-0038（隔离构建体积探针）
- ADR-0040（UPM-aware Module Source Catalog）
- `docs/framework-module-map.md`
- [Unity asmdef 文件格式](https://docs.unity3d.com/cn/6000.0/Manual/assembly-definition-file-format.html)
- [Unity 托管代码裁剪](https://docs.unity3d.com/cn/6000.0/Manual/managed-code-stripping.html)
- [UnityLinker XML 规则](https://docs.unity3d.com/cn/6000.0/Manual/managed-code-stripping-xml-formatting.html)
- [Unity Package 依赖](https://docs.unity3d.com/cn/current/Manual/upm-dependencies.html)
