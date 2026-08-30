# SSFramework Domain Context

本文件只记录跨代码、测试与文档都需要稳定使用的领域词汇；架构取舍仍以 `docs/adr/` 为准。

## Demo Module

一个可自动发现的框架教学章节 Adapter。它贡献章节所需的容器绑定，并在被选中时构建可交互内容；同一实例可以经历多轮 Build / Teardown，但只属于一轮 Demo 根 Context。

## Demo Module Catalog

Demo 章节实例、排序校验与生命周期的唯一 owner。它一次构造所有 Demo Module，让同一实例按 Discover → InstallBindings → Initialize → Build / Teardown 执行，并持有当前章节 Host，保证取消发生在 Teardown 之前。`MonoDemoContext` 持有 Catalog，`DemoShellController` 只负责展示和选择。

## Demo Teaching Contract

Demo 教学内容与自动化之间的运行时 Seam。`DemoModuleHost` 在真实 Build 中记录定位、步骤、概念、动作、结果与源码引用等语义，`DemoModuleCatalog` 再按 Capability / Concept / Workflow 三种教学形态校验；缺少场景 Adapter 时改查“原因 → 恢复 → 继续学习”的结构化降级闭环。故意失败或带持久/共享副作用的动作使用 Experiment Notice + Experiment Action，在同一小节形成“影响范围 → 预期证据 → 恢复方式 → 可执行动作”的机器可读顺序；预期异常由 Module 本地精确捕获，Host 兜底只表示真实 Demo 缺陷。契约验证实际执行的内容，不扫描源码 token，也不猜测 USS / VisualElement Implementation。

## UI Async Action Binding

`Game.Framework.UI.Toolkit` Adapter 中连接 `Button.clicked` 与 View 生命周期所有权的窄 Interface。`Bag.SubscribeClickAsync` 负责解绑、把取消 token 交给 handler，并把未处理异常送到 `Log` Seam；生命周期取消静默收口。它不决定按钮禁用、去抖、single-flight 或面向玩家的错误呈现。通常异步操作跟随 Bag 取消；若包下载等物理操作必须在 View 消失后走到终态，handler 可明确不透传 View token，但仍由绑定观察完成，且不得向旧 UI 发布。该能力保持在 Toolkit Module，以免 Core `DisposableBag` 获得渲染后端语义。

## UI Back Input Wiring

项目 composition layer 把物理返回输入（Input Action、Esc、Android Back 或平台事件）映射到 `IUIUtility.Back()` 的浅接线。UI Module 只拥有 Popup → Window → Page、`BackClosable` 与过渡中吞键等深导航语义，不依赖或探测任何输入 Package。Demo 的 `DemoInputSystemBackKeyDriver` 是可搬走的 Input System 样板，不是 Framework Runtime API；项目可按自己的输入路由替换它，而无需新增 UI Core Seam。

## Asset Location Snapshot

`IAssetUtility.GetLocationState` 对某个 package/location 当前清单与本地缓存的同步四态快照：PackageNotReady、Invalid、AvailableLocally、RequiresDownload。它是资源 Module 的高杠杆 Interface，替调用方收口“先守卫初始化，再拼地址有效性与下载需求”的重复编排；具体未就绪原因仍由正交的 `AssetInitState` 表达，YooAsset 的布尔查询与 Reader/Writer 协调保留在 Adapter Implementation 内。

## Config Readiness

`IConfigUtility<TTables>` 对一次自加载尝试的稳定就绪契约。响应式消费者订阅 `State`，命令式流程 `await EnsureReady(token)` 直接得到同一份 `Tables` 或原始失败；已证明 Ready 的同步热路径才直接读 `Tables`。调用方取消只脱离自己的 waiter，组件与 Context 共同拥有物理加载并在销毁时取消；失败后不隐式重试。该 Interface 把终态编排、根异常保存和共享所有权藏在 Config Module 内，业务不再复制 `WaitUntil(Ready or Failed)`。

## Game Flow

`IGameFlow` 是 System 层的宏观业务阶段 Interface；`GameFlow` Implementation 把 `FlowState` 当前状态、最新意图排队、协作取消和每状态子 Context 的所有权保持在同一个深 Module 内。View 在 Command Seam 表达流转意图，持续展示时由查询 Command 返回只读投影；System 与 FlowState 内部可直接解析该 System。项目侧 `FlowNav` 是只观察 fire-and-forget 终态的 Adapter：正常顶替/销毁取消静默，真实进入失败进入 Log Seam，它不拥有转换规则。`Current` 不单拆 Model，因为它只是转换不变量的一部分；保留在同一 Implementation 能提高 Locality，避免增加一条只做镜像同步的浅 Interface。

## HTTP Request Owner

`HttpUtility` 内一次物理 HTTP 交换的私有 owner：独占传给 Provider 的取消 token，并把 caller、Utility lifetime 与 realtime deadline 三种取消意图汇入该 token。外部 token 只触发 owner 的安全 Cancel，第三方取消回调异常不会逃逸到调用方 `CancellationTokenSource.Cancel()` 或 timer 线程；deadline 用独立 completion signal 与物理 outcome 显式竞速，不在 pending UniTask 上并发多 await，也不使用裸 `CancelAfter`。Provider 成功、失败或取消可在任意线程完成，但公共调用回到 Unity 主线程再交还业务。caller / lifetime 在公共 completion 前取消保持 OCE并优先于 deadline；scope 仍存活时 deadline 折叠 Timeout，Provider 在 owner token 未取消时自发 OCE 属 ConnectionError。

## WebSocket Connection Session

`WebSocketUtility` 内一次成功连接的私有 owner：独占该代接收 token、发送 token、FIFO 队尾、终态 claim 与 teardown barrier。公开 `State=Disconnected` 只表达业务不可用，不保证旧 socket 的每个 Receive continuation 已物理返回；后续 Connect 等旧 Close 与发送 owner 清场后再建立新 session，迟到 Receive 靠物理 socket 快照与 session identity 隔离。只有 current session 能发布一次 `WebSocketClosedEvent`，排队旧帧不得写入新连接；接收或发送传输失败都会终结 current session。该概念保持在 Network Module Implementation，不扩张业务 Interface，也不等同于框架 Context / scope。

## WebSocket Connect Attempt

`WebSocketUtility` 内一次在途建连的临时 owner：持有 linked cancellation token、Disconnect intent，以及只属于本 attempt 的 completion outcome（提交的 Connection Session 或 null）。Connecting 期 Disconnect 等的是这个本地 outcome；caller 取消只脱离等待，Attempt owner 仍会在逻辑发布前 Abort 物理 success-win。它不从可被响应式 State 同步重试改写的全局状态猜结果，因此旧 attempt 不会误关新 session；所有成功、失败与 Dispose 路径都必须在 finally 完成 outcome。

## Framework Module Audit

编辑器侧的 Module Catalog、删除计划与体积证据入口。它以当前目标平台的 Player 编译图确定候选 Module，再读 asmdef、当前已编译 DLL 快照的元数据引用、FrameworkHotUpdateProfile，以及项目 Assets 与已注册 Packages 的全部 `link.xml`，把“源码存在、参与编译、预定义程序集隐式引用规则、当前 DLL 快照消费、全 asmdef 删除阻塞、linker 根、热更完整 DLL 部署、最终 Player 证据”保持正交；`autoReferenced:false` 只关闭 Assembly-CSharp 等预定义程序集的隐式引用，不叫“按需启用”，也不代表 Module 退出 Player 编译图。Core / Boot 删除门禁同时比较 asmdef 声明与当前 DLL 元数据闭包：Core 不得接触任意可选 Framework Player Module（含 Boot），Boot 不得接触 Framework Runtime；闭包中的缺失目标也不能因未进入当前 Catalog 而假绿。审计还经只读反射接缝比较可删除 HybridCLR 热更新构建 Module 所拥有的 HybridCLRSettings、Generate stamp、当前热更拓扑 / AOT 补元数据清单与 DLL 中转 manifest；资源构建 Module 是否安装与这份热更证据保持正交。它不把当前 Editor 中可得的 DLL 变体冒充目标平台 Player，也不把文件存在冒充 DLL 内容相对源码新鲜或已部署，并区分空 Profile 的显式纯 AOT 与缺失 / 重复 Profile。它报告常用组合与任意 Module 入口闭包，并解释受热更依赖传播约束的安全移除事务；不提供含糊的 `SetEnabled(bool)`，也不接管 UPM 安装/版本管理。原始 DLL 字节只用于组合对比，最终包体仍以目标平台 Player BuildReport 为准。

## Framework Build Module Split

Editor 构建能力按真实第三方变化源分成单向两层：`Game.Framework.Build.Editor` 拥有 YooAsset 普通 AssetBundle 的 Profile、构建、部署、本地服务和安全产物路径，不引用 Boot、HybridCLR 或 dnlib；`Game.Framework.Build.HybridCLR.Editor` 作为可删除的下游 Module，拥有热更 Profile、Generate 新鲜度、目标 DLL 编译与 YooAsset RawFile 代码包配方，并复用资源构建侧的版本、部署、预检与路径安全 Implementation。资源 Module 不读取热更 Profile；RawFile 包若误在资源 Profile 启用，会在写产物前明确失败并指向专属构建 Module。删除热更新 Module 后资源构建继续成立；保留热更新则必须保留它实际依赖的资源构建、Boot 与 HybridCLR 工具链。

## External Dependency Evidence

Framework Module Audit 内部拥有的第三方依赖证据 Module。它以一方 Player / Editor 消费者与 what-if Profile 为种子，只沿平台范围相交的外部 AssemblyRef 扩展，把同一已注册 Package 的程序集聚合为一组；没有一方种子的 Package 内部边不进入目录。Player、Editor 与 Tests 快照边保持平台语义，Tests 可消费 Editor 依赖，Editor 边不冒充 Player 证据。Assets 预编译 DLL 保留可定位物理变体、Editor 兼容性与完整 BuildTarget 集合，无法还原或范围冲突的程序集显式产生证据问题。Package 安装来源与 manifest 直接/间接关系、当前 DLL 快照的结构化消费者、沿外部 AssemblyRef 链回溯到的首个一方引入者、完整 asmdef 的声明阻塞、按 Profile key 保存的 what-if 原始字节、去重物理文件的已安装二进制字节、静态移除候选和目标构建验证要求保持正交。`RemoveWithOptionalModuleCandidate` 只表示单一可选 Module 没有已知项目消费者，不是 `SafeToRemove` 承诺；可定位到程序集的问题只收紧相关依赖组，无法定位的全局扫描缺口才收紧全部组，且都不抹掉已成立的角色事实。窗口只消费这一份模型并提供定位/复制，不调用 Package Manager 安装或卸载。

## Framework Build Size Probe

Framework Module Audit 的真实玩家构建验证 Adapter。它在 `Library` 下创建隔离空工程，只复制某个审计组合的 Runtime Module Implementation 与当前版本依赖，再调用当前目标平台 Player Build；主工程的业务场景、未选 Module、`link.xml` 和 HybridCLR 生成物都不进入证据。实际 DLL 闭包与 asmdef 声明闭包共同决定需要复制、完整保留的 Framework Module，declared-only 依赖不会因当前 IL 未使用而缺席。外部依赖再由 Framework Module Source Catalog 唯一决定 Package 名、安装来源和物理目录：registry Package 复用主工程版本与 scoped registry，并在整轮启动时按档冻结 manifest 文本与指纹；Git / embedded / local / tarball Package 从已解析源码根整体复制，身份去掉本机路径与 Git URL 凭据并记录内容指纹；复制 Package 中的相对 `file:` 传递依赖 fail-fast；Assets 外部依赖或未知显式来源同样 fail-fast，不维护 Module 名称映射。运行目录与结果/日志路径不进入分享 JSON，恢复时由本机 latest-run 指针重建。聚合 `nuget-packages` 仍是一个物理复制边界，不冒充单 DLL 可卸载能力。所选程序集完整保留，因此结果是确定性的体积上界，不是假装成具体游戏实际用量的包体承诺。

## Framework Module Source Catalog

Editor 侧把 Unity 资产身份还原为物理源码与 Package 所有权的唯一 owner。它接受 `Assets/...`、`Packages/...` 或已解析的绝对路径，统一给出 canonical Asset Path、真实 Physical Path、源码根、package id、安装来源与 manifest 直接/间接关系；后两者只描述 Package 解析事实，不推导代码消费者或可移除性。Module Audit、隔离 Build Size Probe 和源码门禁都通过它读取 asmdef、`link.xml` 与模板。AssetDatabase 已知候选不可读时 fail-fast，不把证据缺失静默解释成没有规则；框架位于 Assets、嵌入包或 registry/Git PackageCache 时共享同一份证据，不各自猜测文件系统布局。Build Size Probe 另对实际复制的 Runtime Module 与 Git / embedded / local / tarball Package 生成内容指纹，使 Domain Reload 恢复能识别“路径和版本未变、源码已变”的漂移。

## Framework Editor Catalog

通用工具中心与配置中心使用的 Editor-only 导航 Catalog。可选 Module 分别通过 `FrameworkToolRegistry` 和 `FrameworkConfigRegistry` 登记自己的标题、说明、工作台路径，以及 Profile 的真实类型和数量语义；中央窗口只消费稳定快照，不维护可选程序集限定类型名，也不复制生成、构建或配置 Implementation。删除 Module 后，其注册随域重载自然消失；相同 id 的不同元数据 fail-fast，避免后加载 Adapter 静默覆盖卡片。这个 Seam 只负责发现与导航，不创建资产、不执行副作用，也不代替 Unity Package Manager。

## Mono Context Initialization Issue Group

Editor 诊断窗口把逐宿主初始化快照还原出的维护单元：有异常时，只有共享同一最深异常对象且存在实际 Mono 父子链的宿主才属于同一根因组；无异常的 `Uninitialized/Initializing` 父子链是“时序提醒”，不计入根因。它区分最先失败 / 最上游未就绪、受影响链和当前 Play / 历史证据，只用于解释与定位，不制造可用 `GameContext`，也不进入玩家运行时 Interface。
