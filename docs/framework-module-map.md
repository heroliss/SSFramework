# Framework Module 地图

本文面向框架维护者，记录 `Assets/Game/Framework/` 的程序集边界、依赖方向与删除测试。**`.asmdef` 是编译期真相**；本表解释每个 Module 为什么存在，结构变化时同步更新。

## 设计不变量

1. `Game.Framework` 是运行时内核，只依赖通用异步/响应式基础库，不依赖付费 Inspector、具体资源、UI 后端、Protobuf 或业务程序集。
2. 可替换能力采用“Interface 在稳定侧，Implementation 在可删除 Module”的 Seam：例如 `IAssetProvider ← YooAssetProvider`、`IUIBackend ← UGUI/Toolkit Adapter`。
3. `Game.Framework.Boot` 是 AOT 薄壳，永不引用 `Game.Framework*` 运行时程序集，否则框架无法作为热更新代码加载。
4. Editor Implementation 与 Runtime 分离；`Game.Framework.Editor` 是轻量、稳定的编辑器工具基座，拥有跨模块反馈、诊断与通用配置体验；重第三方 Editor 依赖仍放进独立 Editor Module。可选 Editor Module 可以单向依赖该基座，基座不得编译期反向引用它们；工具、配置卡与生成输出声明分别由 Module 向 `FrameworkToolRegistry`、`FrameworkConfigRegistry`、`FrameworkGeneratedOutputClaimCatalog` 自注册。
5. 运行时内核和可热更新 Module 保持 `autoReferenced:false`；消费方通过 asmdef 显式声明依赖。
6. 删除测试：移除一个可选 Module 目录后，只应失去该能力及其直接消费方，不应迫使核心 Module 修改源码。
7. 外部程序集的直接依赖必须在 asmdef 显式可见，即使插件 DLL 的 auto-reference 已让代码偶然编译通过；否则 UPM 声明、删除测试与 AI 导航都看不到真实代价。
8. “源码存在、参与编译、真实消费、linker 根、热更部署、最终 Player”是不同状态；工具与文档不得合并成一个含糊的“已启用”。

Odin Inspector 是项目级可选专业工具，不是 Runtime 前置。通用基线用 Unity 原生 Drawer/fallback Editor
保证资源包配置、UI 生成配置与诊断；可整体删除的 `Game.Framework.Odin.Editor` 用无持久化的临时 Editor 映射
组合 OdinEditor 与 Framework 诊断，基线不得反向引用。Fonts 等可选 Module 的专属诊断经 Editor-only contributor
接缝单向注册，避免通用
Editor 反向引用。Adapter 不得随 Framework 分发付费插件本体。详见 [Odin 可选集成与移除](optional-odin-integration.md)与
[ADR-0015](adr/0015-odin-decoupling-assessment.md)。

## 轻量组合档位与证据口径

菜单 `SSFramework/诊断与分析/模块与依赖` 打开时只显示轻量说明；点击“采集当前证据”后才读取当前目标平台的 Player 编译图、asmdef、当前已编译 DLL 快照的元数据引用、FrameworkHotUpdateProfile，以及项目 Assets 与全部已注册 Package 中的 `link.xml`。Unity 6000 的 CompilationPipeline `outputPath` 仍可能指向 Editor 变体，所以这份 DLL 闭包用于发现依赖方向和候选，不冒充目标 Player 证明；所有一方 Runtime、Editor 与测试 asmdef 另以 `overrideReferences:true` 关闭预编译 DLL 的全局 Auto Reference，可删除 Editor Module 同时关闭预定义程序集隐式引用。平台分支中的漏声明会在真实目标编译时失败。`FrameworkModuleSourceCatalog` 同时保留 Unity 可定位的 Asset Path、可供 `System.IO` 读取的 Physical Path、package 名称/版本、安装来源，以及 manifest 的直接/间接关系；最后一项只表示 Package 解析层级，不是代码直接消费者或安全卸载结论。因此源码搬到嵌入式包或 `PackageCache` 后仍使用同一套审计和构建证据。

窗口新增只读的“第三方依赖证据目录”：它从一方消费者与 what-if Profile 出发，不把无一方种子的 Package 内部关系枚举成项目依赖；同一 Package 的程序集只显示一组，Assets DLL 保留全部物理变体、Editor 与 BuildTarget 平台集合。每组分开列出当前 Player/Editor/Tests DLL 快照的结构化直接消费者、沿平台相交的外部 AssemblyRef 链回溯到的首个一方引入者、完整一方 asmdef 的删除阻塞，以及 Core/UGUI/Toolkit/任意 Module what-if 档位中的传播与体积影响。Tests 可沿 Editor 边追踪，Editor 快照不会反向冒充 Player 证据。角色看“最初是谁引入”，Profile 看“会传播到哪些组合”，两者不混算；体积按 Profile 独立计量，目录摘要明确显示最高档位，已安装 DLL 则按去重后的物理文件另行说明，不把互斥档位相加，也不伪装成 `0 B` 或玩家包体。`可随单一 Module 评估移除` 只是静态候选，不是 `SafeToRemove`；可归属到 AssemblyName 的证据缺口只收紧相关组，无法归属的全局扫描问题才收紧全部组。目录只提供定位与复制，不调用 Package Manager 安装/卸载，也不按 DLL 名猜 Adapter。详见 ADR-0042。

窗口还会先只读比较唯一 Profile、HybridCLRSettings、Generate stamp、当前热更加载顺序、AOT 补元数据清单与 DLL 中转目录，再解释当前 Player 编译图发现的每个 Runtime Module、当前 DLL 消费者、完整 asmdef 删除阻塞、热更部署和 linker 根；Module 退出编译图后不会保留一张“未参与”卡片。之后窗口给出 Core-only、Core + UGUI、Core + Toolkit、全部 Runtime Module、Profile 期望热更档位，以及任意 Module 作为入口的 what-if 闭包。`autoReferenced:false` 只关闭 Assembly-CSharp 等预定义程序集的隐式引用，不代表 Module 已退出编译图或会自动从包中消失。空 Profile 不强制 Generate；只有启用场景不依赖 `HotUpdateLauncher` 的直接 AOT composition root 才可省 CodePackage，保留 Launcher 时步骤 3 会产出其 Player 分支需要的空清单包。缺失或重复 Profile 会明确告警。完整闭包、全局 / HybridCLR 生成的 linker 规则和原始报告按需展开。它同时机器执行四条声明 + 当前 DLL 双层删除测试：Core 不反向依赖任意可选 Framework Player Module（含 Boot）、Boot 不接触 Framework Runtime、UGUI 不带 Toolkit/Bridge、Toolkit 不带 UGUI/Bridge。

报告里的大小是链接、AOT、压缩前的原始托管 DLL，只用于发现“一个很小的 Adapter 意外拖入很大的外部依赖”以及比较组合；它不是最终包体承诺。需要真实平台证据时打开 `SSFramework/诊断与分析/真实构建体积`：窗口打开不做全矩阵扫描，读取组合后再选择构建；动作层重新采集当前证据并只为所选组合计算指纹。探针在 `Library` 下创建隔离空工程，只复制所选 Runtime Module 和当前版本依赖，再用当前目标平台 / 脚本后端读取 Player BuildReport。Package 计划由所选 asmdef 声明、当前 Player DLL 元数据引用与 Source Catalog 派生，不按 Module 名猜依赖；Framework 的 declared-only Module 也进入编译闭包；registry Package 复用主 manifest 版本与 scoped registry，整轮启动时冻结每档 manifest 指纹；Git / embedded / local / tarball Package 从已解析源码根整体复制并记录去敏身份与内容指纹。所选程序集完整保留，因此结果是可重复的体积上界。`nuget-packages` 当前仍是聚合物理边界，探针不会把其中 DLL 假装成可独立卸载模块；详情见 ADR-0038。

### 五层状态与当前例外

| 层 | 回答的问题 | 当前证据 |
|---|---|---|
| 源码 / Package | 文件、导入器和 asmdef 是否安装？来自 Registry、Git、嵌入或本地？manifest 直接声明还是间接解析？ | Source Catalog 的 Asset / Physical / package / source / directness 身份、UPM manifest / lock；隔离探针另记录实际复制内容指纹 |
| Player 编译 | 当前平台是否编译该程序集？ | `CompilationPipeline.GetAssemblies(Player)`；`autoReferenced:false` 不会让源码停止编译 |
| 当前代码消费 / 删除阻塞 | 谁在当前 DLL 快照元数据里引用；谁在任意 asmdef 中声明引用？ | 前者解释代码保留候选但可能是 Editor 变体，后者覆盖完整 asmdef 图中的物理删除编译阻塞；目标平台、Assembly 注册与反射创建仍需结合真实构建和 linker 根说明 |
| 保留 / 部署根 | 什么会让它留下？ | 场景、资源、反射、`link.xml`；HybridCLR Profile 同步后按程序集部署完整 DLL |
| 最终 Player | 链接、IL2CPP、引擎模块和压缩后是多少？ | 目标平台 BuildReport / 发布产物 |

当前 `Asset.Yoo`、`Network.Proto`、`UI.Toolkit` Module 目录各有无条件 `link.xml`：分别保留 Yoo Adapter、Google.Protobuf、UIElementsModule。它们不一定是错误，但意味着“业务没有静态调用”不能推出“最终自动消失”。`Asset.Yoo` 的默认 Provider 注册属于 Adapter Assembly，Core 不再保存具体类型名；保守的 `link.xml` 仍覆盖自定义属性 + 反射创建在不同 Unity linker 版本下的可达性差异。`Assets/HybridCLRGenerate/link.xml` 是生成物，第三方目录的规则有自己的升级边界；审计只读展示，不提供一键改写。

当前所有 Runtime Module 都参与 Player 编译并引用 Core。若 Core 热更，仍留在编译图的可选 Module 不能被单独改成 AOT，否则形成 AOT → 热更违规。强裁剪应把“迁移消费者、删除 / 卸载 Module 使其退出编译图、清理 Profile、同步并重新 Generate”作为一项结构事务；不要先只从 Profile 取消再同步。完整决策见 ADR-0039。

## 程序集地图

| Module | 路径 | 职责 / 边界 | 删除测试 |
|---|---|---|---|
| `Game.Framework` | `Core/` | Context、Container、MVCS 权限、Command/Event、生命周期与通用 Interface；含零第三方实现的 Storage/Audio/Flow/Localization/Logging/Network 等能力。 | 不可删除；其余运行时 Module 的稳定依赖方向指向它。 |
| `Game.Framework.Asset.Yoo` | `Asset.Yoo/` | `IAssetProvider` 的 YooAsset Adapter；YooAsset 接触面和 `[assembly: DefaultAssetProvider]` 默认装配都集中在这里。 | 删除后仅失去 YooAsset Implementation；Core 不含 Yoo 类型名，安装另一个注册 Adapter 即可替换。 |
| `Game.Framework.Asset.Yoo.Tests` | `Asset.Yoo/Tests/Editor/` | Yoo package 进程级 Reader/Writer、取消、缓存世代、同步快照与后台终态的纯 EditMode 契约。 | 随 Yoo Adapter 删除；不进入玩家构建，也不让通用 Core Test 反向依赖可选 Adapter。 |
| `Game.Framework.Config` | `Config/` | 配置运行时编排与 `IConfigUtility<TTables>`；不依赖 Luban。 | 删除后失去配置表 Module，Core 不改。 |
| `Game.Framework.Config.Editor` | `Config/Editor/` | Luban CLI/Profile/配置总览，以及代码 + 数据 + manifest 的暂存校验、双树差量发布与失败回滚；复用通用 Editor 反馈和输出 claim Catalog。 | 可与 Config 一起删除；不向 Runtime 泄漏 Editor 依赖，也不把 Luban 双树语义塞进 Proto。 |
| `Game.Framework.Config.Editor.Tests` | `Config/Editor/Tests/` | Luban 配置/claim 注册、受控 CLI 参数、暂存产物边界、`.meta` 保留、零写盘差量与双目录回滚契约。 | 随 Config Editor Module 删除；不进入玩家构建。 |
| `Game.Framework.Config.Tests` | `Config/Tests/` | 配置就绪、根失败、取消所有权与清单边界的 PlayMode 契约；使用可控资源 Provider，不依赖 Luban/YooAsset。 | 随 Config 一起删除；不让通用 Core Test 反向依赖可选 Config Module。 |
| `Game.Framework.Fonts` | `Fonts/` | TMP/Toolkit 多语言 fallback 链；TMP 依赖收口。 | 删除后仅失去自动字体链，本地化 Interface 仍可用。 |
| `Game.Framework.Fonts.Tests` | `Fonts/Tests/` | Runtime 字体 fallback 链、locale 切换、OS 字体缓存与释放契约。 | 随 Fonts Runtime Module 删除；通用 Test Module 不再引用 TMP 或 Fonts。 |
| `Game.Framework.Fonts.Editor` | `Fonts/Editor/` | 常用字集扫描与生成。 | 可独立删除，不影响运行时字体链。 |
| `Game.Framework.Fonts.Editor.Tests` | `Fonts/Editor/Tests/` | 字集生成与 Fonts 工具/配置/精确输出 claim 注册契约。 | 随 Fonts Editor Module 删除；不进入玩家构建。 |
| `Game.Framework.Network.Proto` | `Network.Proto/` | Google.Protobuf Adapter，把生成的 `IMessage` 接到内核网络 Seam。 | 删除后 JSON 与内核手写 Protobuf 仍可用。 |
| `Game.Framework.Network.Proto.Tests` | `Network.Proto/Tests/` | Google.Protobuf Adapter、测试专用 `.proto`、生成夹具与 Core wire 互通契约。 | 随 Proto Runtime Module 删除；通用 Test Module 不再引用 Google.Protobuf。 |
| `Game.Framework.Network.Proto.Editor` | `Network.Proto/Editor/` | protoc Profile、代码生成与总览入口；复用通用 Editor 反馈。 | 可与 Proto Runtime 一起删除，Core 不改。 |
| `Game.Framework.Network.Proto.Editor.Tests` | `Network.Proto/Editor/Tests/` | Protobuf 配置/claim 来源注册、递归后缀清理与跨生成器冲突拒绝契约。 | 随 Proto Editor Module 删除；不进入玩家构建。 |
| `Game.Framework.UI` | `UI/` | 渲染中立窗口编排、层级/栈/模态/过渡及后端 Interface；当前也承载 ObservableCollections 增量列表引擎，因此该第三方依赖会随 UI Core 进入托管闭包。物理返回输入由项目 composition layer 接到 `IUIUtility.Back()`，本 Module 不依赖输入 Package。 | 删除后失去窗口框架与列表绑定引擎，但 Core MVCS 仍可用。 |
| `Game.Framework.UI.UGui` | `UI.UGui/` | UGUI Window/View Adapter，含 `Transform → GameObject` 的 `Bag.BindList` Adapter。 | 删除后 Toolkit 后端与 UI Core 仍可编译。 |
| `Game.Framework.UI.UGui.Editor` | `UI.UGui/Editor/` | UGUI 节点绑定生成等 Editor 工具；复用通用 Editor 反馈。 | 可独立删除，不影响 UGUI Runtime 手写接线。 |
| `Game.Framework.UI.UGui.Editor.Tests` | `UI.UGui/Editor/Tests/` | UGUI 绑定 Inspector、Popup、Overlay，以及逻辑/节点精确输出 claim 注册契约。 | 随 UGUI Editor Module 删除；不进入玩家构建。 |
| `Game.Framework.UI.Toolkit` | `UI.Toolkit/` | UI Toolkit Window/View Adapter、`VisualElement` 的 `Bag.BindList` Adapter 与 RenderTexture 显示原语。 | 删除后 UGUI 后端与 UI Core 仍可编译。 |
| `Game.Framework.UI.Bridge` | `UI.Bridge/` | UGUI/相机内容嵌入 Toolkit 的 RenderTexture Adapter。 | 删除后两套独立 UI 后端仍可用。 |
| `Game.Framework.Boot` | `Boot/` | HybridCLR/YooAsset 热更启动 AOT 薄壳。 | 可在无热更项目删除；不得反向依赖 Framework Runtime。 |
| `Game.Framework.Editor` | `Editor/` | 稳定且零付费插件依赖的编辑器工具基座：Core 原生 Drawer/Inspector、跨模块非阻塞反馈、诊断窗口、菜单、项目路径，以及 Module-local 注册的工具/配置/生成输出 claim Catalog；Module Audit 与隔离 Player Build 体积探针共用结构化组合，Source Catalog 统一解析 Assets / Packages / PackageCache。 | 玩家构建不包含。若删除，需一并删除或改接直接依赖它的 Build / Config / Proto / UGUI Editor 工具；所有 Runtime API 与玩家构建仍不受影响。 |
| `Game.Framework.Odin.Editor` | `Odin.Editor/` | 可选专业 Inspector Adapter：仅把原生 fallback 接管且 Odin 允许绘制的具体 Framework Mono 类型临时映射到组合诊断的 OdinEditor；不写 Odin 配置，不含或重分发 Odin。 | 移除 Odin 前先整体删除；Domain Reload 后原生 fallback 接管，Runtime 与资产布局不变。 |
| `Game.Framework.Editor.Tests` | `Editor/Tests/` | 通用 Editor 工具的 EditMode 契约；覆盖生成 claim 冲突矩阵、写盘前刷新，以及 AI PlayMode 预检的无弹窗保存与未命名场景拒绝。 | 随 Editor Module 删除；不进入玩家构建。 |
| `Game.Framework.Build.Editor` | `Build/Editor/` | YooAsset 普通 AssetBundle 的 Profile、SBP 构建、部署、本地 CDN、加密接入与产物路径安全；保留既有程序集名以兼容 Profile 与 CI，但不引用 Boot、HybridCLR 或 dnlib。 | 无资源构建需求时可删；删除下游热更新构建后仍能独立编译与使用。 |
| `Game.Framework.Build.Editor.Tests` | `Build/Editor/Tests/` | 资源构建工具/配置/包名常量 claim 注册、可移植产物路径和“零热更工具链引用”的删除契约。 | 随资源构建 Module 删除；不进入玩家构建。 |
| `Game.Framework.Build.HybridCLR.Editor` | `Build/HybridCLR/Editor/` | HybridCLR 热更 Profile、程序集图、Generate 新鲜度、目标 DLL 与 YooAsset RawFile CodePackage；单向复用资源构建侧的版本、部署、预检和路径安全。 | 删除后失去代码热更新构建与配置卡；普通资源构建不改源码，项目可连同 Boot、HybridCLR/dnlib 评估移除。 |
| `Game.Framework.Build.HybridCLR.Editor.Tests` | `Build/HybridCLR/Editor/Tests/` | 热更新证据、元数据拓扑、代码包 Collector、目录注册与 Profile 程序集迁移兼容契约。 | 随热更新构建 Module 删除；资源构建测试不引用热更新工具链。 |
| `Game.Framework.Demo` | `Demo/` | 32 个可运行教学章节，是所有 Module 的消费方与集成样板；Catalog 集中拥有章节 Adapter、生命周期与 Host 教学语义校验。包含 Input System → `IUIUtility.Back()` 等项目 composition 样板，但这些不是 Framework Runtime API。 | 可整体删除；`UNITY_EDITOR` define 保证不进玩家包。 |
| `Game.Framework.Demo.Tests` | `Demo/Tests/` | Demo 专属 EditMode 门禁：章节生命周期/回滚、教学形态与结构化降级契约、内嵌服务器、关键示例行为及全部 CodeRef 防腐。 | 随 Demo 一起删除；不让 Demo 专属依赖反向进入通用 Test Module。 |
| `Game.Framework.Demo.PlayMode.Tests` | `Demo/Tests/PlayMode/` | 加载真实 DemoScene，穿过 Context、Catalog 与 Shell 逐章 Build 32 个 Adapter，并验证真实缺依赖降级页。 | 随 Demo 一起删除；不进入玩家构建，也不把场景集成依赖塞回纯 EditMode 门禁。 |
| `Game.Framework.Test` | `Test/Scripts/` | Core 与跨 UI Adapter 的 PlayMode/EditMode 契约及回归测试；`Test/Res/SuspendedSceneProbe` 是无业务 Awake 的 Yoo 场景激活门 fixture。可选 Module 的独立契约逐步迁回各自 owner。 | 产品运行不依赖；开发/CI 不应删除。 |

## 维护检查清单

- 新增程序集前先问：它是否拥有独立的变化原因、第三方依赖或删除边界？如果没有，优先放进现有 Module。
- 新增 Interface 前做删除测试：去掉某个 Implementation 后，调用方是否仍能以同一抽象工作？只有真实 Seam 才值得抽象。
- 新增第三方库时，先放入 Adapter Module；不要为了“以后也许替换”把每个类都拆成一对 Interface/Implementation。
- 新增付费 Editor 插件集成时，原生基线必须先成立；Adapter 只依赖用户已安装的插件，不复制插件本体，并补“删除 Adapter 后仍可编辑/编译”的门禁。
- 修改 asmdef 引用后，运行完整 Unity 测试，并检查 Boot、Core、两个 UI 后端与可选 Module 的依赖方向。
- 运行 `SSFramework/诊断与分析/模块与依赖`；隐式外部引用和第三方证据缺口应为 0，四条删除测试应通过，并逐项解释新 Module 的 Player 编译、项目消费者、第三方依赖、热更部署与 linker 根。报告变大只作为调查信号，不以原始 DLL 字节直接宣称最终包体回归。
- 对包体敏感的结构决策再运行 `SSFramework/诊断与分析/真实构建体积`；先切到目标平台，比较同环境下相对 Core 的体积上界，不跨平台外推。
- 本文与实际 `.asmdef` 不一致时，以 `.asmdef` 为准并立即修正文档。
