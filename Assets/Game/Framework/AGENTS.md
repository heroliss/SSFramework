# Game.Framework 内部编码约束

本文件只约束 `Assets/Game/Framework/` 的源码维护。业务侧 API 规则见父目录 `AGENTS.md`；程序集职责与删除测试见 `docs/framework-module-map.md`；关键取舍见 `docs/adr/`。

## Module、Interface 与依赖方向

- `Game.Framework` Core 禁止引用项目业务程序集、具体 UI 后端、YooAsset、Google.Protobuf 等可选 Implementation。依赖只能指向 asmdef 中声明的通用库。
- 可替换能力采用“稳定 Interface 在 Core / 稳定上游，可删除 Implementation 在 Adapter Module”。新增抽象前做删除测试；没有真实替换 Seam 时不要机械制造 Interface。
- `Game.Framework.Boot` 是 AOT 薄壳，永不引用任何 `Game.Framework*` Runtime 程序集。
- Runtime 与 Editor 分离。Drawer/EditorWindow 放 Editor asmdef；重第三方 Editor 依赖建立内聚的独立 Module，避免污染通用 Editor。
- Core 与热更新 Runtime Module 保持 `autoReferenced:false`。新增/移动程序集时同步 `docs/framework-module-map.md`、热更清单、Demo/Test asmdef，并执行完整测试。
- 一方 Runtime、Editor 与测试 Module 直接使用的外部程序集必须按 Unity 的真实字段显式声明：asmdef 生成的程序集放 `references`；预编译 DLL 放带 `.dll` 后缀的 `precompiledReferences`，并设 `overrideReferences:true`。不得把 DLL 名写进 `references` 冒充声明，也不能依赖插件 `Auto Reference` 恰好让编译通过。可删除 Editor Module 还应设 `autoReferenced:false`，避免 `Assembly-CSharp-Editor` 获得隐式依赖。改引用后运行 `SSFramework/诊断与分析/模块与依赖`；`AllFirstPartyAsmdefs_DisableGlobalPrecompiledAutoReference` 与 `OptionalEditorAsmdefs_DisablePredefinedAssemblyAutoReference` 同时作为机器门禁。
- 通用 Editor 工具读取 Framework 源码、asmdef、`link.xml` 或模板时，通过 `FrameworkModuleSourceCatalog` 保留稳定 Asset Path 并解析真实 Physical Path；不要把 `Assets/...` 直接交给 `System.IO`，也不要假设源码一定在项目根目录内。该约束让同一 Implementation 可从 `Assets`、嵌入式 Package 或 `PackageCache` 工作。
- 第三方库不直接修改；依赖行为通过 Adapter 封装，并在边界注释记录版本相关假设与失败语义。
- Framework 面向使用者的 Editor 工具、默认配置与通用说明必须保持项目无关。可以动态展示扫描当前工程所得的场景、程序集与路径作为证据，但不得把 DemoScene、样例包、业务目录或项目程序集硬编码成默认值、固定分类或必经步骤；具体案例留在对应 Demo、项目配置或明确标记的案例文档中。

## Editor 菜单与工具窗口

- 顶部 `SSFramework/` 人工菜单只打开 `EditorWindow`，不得直接生成、构建、清理、写配置或启动外部进程。操作放到所属 Module 的工作台，按钮旁说明用途、前置条件、影响和失败后的下一步。
- 三类显式例外：有真实选择上下文的 `Assets/SSFramework` / `GameObject/SSFramework` 操作；供 MCP/CI 调用且由 ADR 锁定路径的 `SSFramework/诊断/AI 自动化/*` 机器 Interface；配置驱动且只做安全导航的 `SSFramework/场景/*` 动态项。新增例外必须扩充 `FrameworkMenuContractTests` 白名单并记录理由；动态项由场景快捷入口测试锁定，因为它们不是可反射的 `[MenuItem]`。
- 通用工具中心只消费 `FrameworkToolRegistry` 描述符，不反向引用可选 Editor Module。Module 自己登记标题、分类、说明和导航菜单；删除 Module 后卡片应自然消失。
- 通用配置中心只消费 `FrameworkConfigRegistry` 描述符。Profile 类型、单例/多份语义、附属配置和工作台入口由拥有它们的 Module 自行登记；不得在中央窗口恢复程序集限定类型名清单。
- 只读窗口和“定位/打开”动作不得暗中创建 Profile、目录或其它资产；缺配置时展示明确的创建按钮。创建资产、生成、构建等入口在按钮禁用之外，还要在动作 Implementation 中调用 `FrameworkEditorOperationGate` 二次校验。

## 公共 API 与注释

- 公共类型、公共/受保护成员说明：它在架构里的职责、谁调用、所有权/生命周期、取消与异常约定，以及刻意隐藏的细节。
- 异步、释放、缓存、反射、初始化顺序和第三方适配必须解释“为什么这样做、规避什么坑”；直观字段/分支不写翻译式注释。
- XML doc 泛型遵循根 `AGENTS.md`：cref 用 `{T}`，正文用 `&lt;T&gt;`，多行 `<code>` 用 CDATA。
- 注释描述当前设计，不写“以前怎样/本次为什么改”；演进历史属于 ADR/commit。
- 公共 API 变更必须同步 guide、相关 ADR/AGENTS、Demo 与测试；兼容性变化要在 API 旁写清迁移方式。

## 异步、取消与日志

- 面向业务且没有同步对应版本的公共 UniTask API **省略 `Async` 后缀**：`Load/Initialize/ClearCache`。Provider/第三方 Adapter 内部保留 `XxxAsync`，与底层 API 对齐；同时存在同步版本时也保留后缀区分。
- 生命周期 token 由调用链传入并默认透传到所有真正支持取消的 await；取消保持 `OperationCanceledException` 语义，不包成普通失败。若第三方 token **只会取消等待、不会终止已经启动的物理 operation**，必须显式拆分 waiter 与 owner：调用者可及时离开，owner 继续观察到可证明的物理终态后才释放互斥/资源，并在边界注释该第三方行为。不要把“await 抛了 OCE”误当成底层已经停止。Fire-and-forget 必须有清晰的所有权和异常观察点。
- 定义第三方 operation 的“物理终态”时检查推进条件：owner 不能等待一个只有调用方拿到返回 handle 后才能解除的状态。场景预加载这类主动挂起流程应在“内容已读完 / 可交接”的 barrier 返回，再由 handle 暴露恢复动作；否则会形成循环等待并永久占住互斥。
- 同步快照工厂若与异步维护共享状态，不能在 Writer 活跃或排队时绕过协调，也不能阻塞 Unity 主线程等待；用短同步 Reader admission 原子完成“读世代 + 建快照”，无法立即进入时 fail-fast 并提示维护后重试。
- 新代码日志统一 `Game.Framework.Logging.Log`。Trace 使用插值处理器且插值表达式无副作用；后台线程可能进入的 sink/回调要说明线程安全约束。
- 新增 Unity Console 转发层时保持整个转发链 `[HideInCallstack]`，并扩充对应回归测试。

## Mono 层实现

`MonoModelBase` / `MonoSystemBase` / `MonoUtilityBase` 是 `MonoLayerBase<TLayer>` 的薄壳，只声明执行顺序和层标记。注册、注入、AssetReference 绑定、Bag 释放与反注册统一放在 `MonoLayerBase`。

- 生命周期模板改一处，让三层自动一致；不要在三个薄壳复制实现。
- `[DefaultExecutionOrder]` 留在具体类，泛型基类上的特性不按预期生效。
- `MonoViewBase` 只 Inject、不注册，保持独立，不继承 `MonoLayerBase`。
- 父 Context 先销毁时的 `IsDisposed` 短路已集中实现。只有新增“会注册到 Container 的 Mono 层基类”才复用同一模式。
- Unity fake null 判定使用 `==/!=`；序列化必填引用默认 fail-fast，不静默降级。

## Container、生命周期与错误语义

- Container 解析、运行时覆盖和父级回退是公共契约；修改前先补失败用例与边界测试，再改实现。
- 注入权限与 `ICanXxx` 扩展必须同源；不能出现“扩展方法被编译器挡住，却能用 `[Inject]` 绕过”的路径。
- 所有权进入 Context/Bag 后必须幂等释放；组合对象逆序释放。缓存的 CTS、订阅、租借登记等要在移交/提前归还时摘除旧所有权，防止二次 Dispose/Return。
- `ContainerBuilder → Container → GameContext` 是提交式所有权链：Build 前 Builder 临时持有，Build 后 Container 接手，Context 构造失败必须回滚。框架内手工 Builder 一律 `using var`；新增初始化路径不得发布半初始化 Context。
- 预期内缺失可返回 null/false；系统性失败、配置错误和破坏契约的调用应抛含上下文的异常。不要用日志替代失败语义。

## 配置 Profile 的可发现性

新增 Editor 配置 Profile 必须同时完成：

1. 由所属 Module 的 `SSFramework` 工作台可达：单例型显式创建/定位资产，多份型在专属总览管理；
2. 在所属 Module 的 Editor 初始化入口向 `FrameworkConfigRegistry` 登记真实 Profile 类型、数量语义和工作台路径；
3. 明确数量语义：单例 `Resolve()` 多份时取稳定第一项并 Warning；多份 `ResolveAll()` 按资产路径排序。

运行时场景组件不适用本规则。Profile 的查找以类型为契约，不依赖固定资产路径。

## 完成定义

一项非琐碎 Framework 能力完成时检查：

- ADR/设计说明明确 Interface、Implementation、Seam 与刻意不做；
- asmdef 依赖方向和 Module 删除测试成立；
- 正常、边界、取消/异常、释放路径有测试；
- Demo 用原子操作展示可观察因果，并能跳到真实源码；
- guide 与业务 `AGENTS.md` 只记录调用者真正需要的约束。

避免把一次交互式开发拆成多个互相猜测的实现 Agent；改公共 API 或多文件架构后，按根规则提议独立只读评审。
