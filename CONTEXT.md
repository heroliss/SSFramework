# SSFramework Domain Context

本文件只记录跨代码、测试与文档都需要稳定使用的领域词汇；架构取舍仍以 `docs/adr/` 为准。

## Demo Module

一个可自动发现的框架教学章节 Adapter。它贡献章节所需的容器绑定，并在被选中时构建可交互内容；同一实例可以经历多轮 Build / Teardown，但只属于一轮 Demo 根 Context。

## Demo Module Catalog

Demo 章节实例、排序校验与生命周期的唯一 owner。它一次构造所有 Demo Module，让同一实例按 Discover → InstallBindings → Initialize → Build / Teardown 执行，并持有当前章节 Host，保证取消发生在 Teardown 之前。`MonoDemoContext` 持有 Catalog，`DemoShellController` 只负责展示和选择。

## Demo Teaching Contract

Demo 教学内容与自动化之间的运行时 Seam。`DemoModuleHost` 在真实 Build 中记录定位、步骤、概念、速记（Tip）、注意边界（Caution）、动作、结果与源码引用等语义，`DemoModuleCatalog` 再按 Capability / Concept / Workflow 三种教学形态校验；缺少场景 Adapter 时改查“原因 → 恢复 → 继续学习”的结构化降级闭环。Tip 只提炼心智模型与延伸阅读，Caution 表示忽略后可能产生错误 / 泄漏 / 误判但本身不授权实验动作；故意失败或带持久 / 共享副作用的动作使用 Experiment Notice + Experiment Action，在同一小节形成“影响范围 → 预期证据 → 恢复方式 → 可执行动作”的机器可读顺序。预期异常由 Module 本地精确捕获，Host 兜底只表示真实 Demo 缺陷。契约验证实际执行的内容，不扫描源码 token，也不猜测 USS / VisualElement Implementation。

## Demo Server Physical Task Owner

`DemoGameServer` Implementation 内统一登记并观察 HTTP / WebSocket accept、HTTP handler 与 connection task 的私有 owner。`Stop` 先发布逻辑停止并取消 server token，再关闭 listener 与活动 socket；慢 HTTP 不占用睡眠线程，connection 只有在内部 tick loop 收尾后才到物理终态。公共 `IDemoGameServer` Interface 保持同步停止，因为章节调用方只关心“后续连接失败”的可见语义；物理 drain 是 Demo 测试和 Domain Reload 卫生使用的内部 Seam，不扩张到 Core Network Module。

## UI Async Action Binding

`Game.Framework.UI.Toolkit` Adapter 中连接 `Button.clicked` 与 View 生命周期所有权的窄 Interface。`Bag.SubscribeClickAsync` 负责解绑、把取消 token 交给 handler，并把未处理异常送到 `Log` Seam；生命周期取消静默收口。它不决定按钮禁用、去抖、single-flight 或面向玩家的错误呈现。通常异步操作跟随 Bag 取消；若包下载等物理操作必须在 View 消失后走到终态，handler 可明确不透传 View token，但仍由绑定观察完成，且不得向旧 UI 发布。该能力保持在 Toolkit Module，以免 Core `DisposableBag` 获得渲染后端语义。

## UI Loading Ownership

`IUIUtility.AcquireLoading` 是业务占用全局 Loading 的唯一推荐 Interface；返回的 `LoadingHandle` 代表一个 owner，最后一个有效 handle 释放后才关闭共享窗口，`Close` / `CloseAll` / Context 销毁会让旧 handle 陈旧安全。`ShowLoading/HideLoading` 只保留旧源码迁移所需的单 owner Implementation，已标记为非破坏性 `Obsolete`，Framework 生产源码门禁禁止新增调用；未来破坏性版本删除它时，并发所有权复杂度仍完整留在 lease Module 内，不会重新散落到调用方。

## UI Back Input Wiring

项目 composition layer 把物理返回输入（Input Action、Esc、Android Back 或平台事件）映射到 `IUIUtility.Back()` 的浅接线。UI Module 只拥有 Popup → Window → Page、`BackClosable` 与过渡中吞键等深导航语义，不依赖或探测任何输入 Package。Demo 的 `DemoInputSystemBackKeyDriver` 是可搬走的 Input System 样板，不是 Framework Runtime API；项目可按自己的输入路由替换它，而无需新增 UI Core Seam。

## Object Pool Lease

一次成功的 `Rent` / `Spawn` 会发布一份 lease（租借所有权），实例在 `Return` / `Despawn` 前只归当前调用方使用；池在所有构建中按引用身份维护 Inactive → Renting → Active → Returning 状态，钩子异常或重入只能让事务补偿并丢弃脏实例，不能发布半初始化对象或把同一引用交给两个 owner。`PoolUtility` 按实例的真实来源路由 C# 归还，并以两阶段关闭封闭新工作、保留旧 lease 的 terminal 归还出口；`DisposableBag` 不会发布关闭过程中晚到的 lease。GameObject 池还负责剔除 Unity fake-null 空闲槽、在可重入回调后复核提交时容量，并把活动 clone 从 `DontDestroyOnLoad` parking 显式迁回目标 Scene；已被外部 Destroy 的活动实例则保留在借出计数侧作为未正常结束 lease 的诊断证据。

## Asset Location Snapshot

`IAssetUtility.GetLocationState` 对某个 package/location 当前清单与本地缓存的同步四态快照：PackageNotReady、Invalid、AvailableLocally、RequiresDownload。它是资源 Module 的高杠杆 Interface，替调用方收口“先守卫初始化，再拼地址有效性与下载需求”的重复编排；具体未就绪原因仍由正交的 `AssetInitState` 表达，YooAsset 的布尔查询与 Reader/Writer 协调保留在 Adapter Implementation 内。

## Asset Runtime Setup

场景中资源基础设施的唯一正式 Mono 入口：`AssetUtility` 内嵌 `AssetRuntimeSettings`，拥有 provider、包状态机、自动初始化批次与加载/维护能力；配置不是业务 Model，启动薄编排也不是业务 System。场景路径在 Awake 应用设置、Start 自动初始化；代码引导在 Start 前 `Configure` 即接管启动，随后显式 `Initialize`。首场景引导早于场景 Settings，普通 AssetBundle 的 `FileOffset` 因而从构建 Profile 生成到业务 `AssetPackages.AssetBundleFileOffset`；构建前校验生成物新鲜度，修改后仍须重编实际部署的 Game.Main / Player。内置偏移共享 1 MiB 上限，Web 文件系统以内存解密读取；该常量不作用于独立 RawFile / CodePackage。WebGL 的 Boot 与业务首场景引导均强制使用 Web 模式。业务只依赖 `IAssetUtility`；Editor/Demo 从具体 Utility 读取的 `Settings` 是 Inspector 场景创作配置，其集合是结构只读视图。代码路径的 `Configure` 提交独立运行快照且不回写 `Settings`，它会深拷贝调用方 DTO 并给 Provider 独立快照，避免调用方、Utility 与 Adapter 之间的配置所有权分叉。运行配置不支持热换：场景设置在 Play 前编辑，代码设置在 Start 前提交。`AssetSystemConfigModel` / `AssetInitSystem` 仅是可迁移兼容层，不得用于新场景。Editor 迁移器先按显式 `_targetContext` 或最近父 Context 验证完整旧接线：同一 Scene/Context 的兄弟 Init 会一起删除，多份 Config / Utility、跨 Scene/跨 Context 或无宿主歧义等已识别风险会在写 Utility 前失败。Project 中的持久化 Prefab 必须先进入 Prefab Mode，候选扫描按 Main Stage / 当前 Prefab Stage 隔离，不把其它预览 Scene 与当前运行作用域混在一起。

## Asset Reference Package Resolution

`AssetReference.PackageName` 为空不是“编辑时绑定到场景里第一个默认包”，而是加载时由该引用已经绑定的 `IAssetUtility` 解析默认包；各宿主独立持有的引用实例因此可以随所属 Context 使用不同配置。Inspector 只把已加载配置中的包名作为点击下拉后的录入候选，不在每次重绘时扫描并猜测全局唯一默认值，也不把候选冒充运行时作用域验证。单个引用实例仍只有一份 `BoundUtility`、`HostToken` 与 handle 所有权，不能同时登记进多个 Context/Bag；共享配置需由单一 owner 持有，或为各 owner 创建独立实例/副本。

## Config Readiness

`IConfigUtility<TTables>` 对一次自加载尝试的稳定就绪契约。响应式消费者订阅 `State`；命令式流程通常经 Context 感知的 `await EnsureConfig<TTables>(token)` 得到同一份 `Tables` 或原始失败；已证明 Ready 的同步路径用 `GetConfig<TTables>()`，高频调用再缓存返回值。两个短入口都解析当前精确 Context，不引入全局静态表。调用方取消只脱离自己的 waiter，组件与 Context 共同拥有物理加载并在销毁时取消；只有 owner token 已取消时，下游 OCE 才是生命周期控制流，Provider / Adapter 自发取消会包装成带 inner 的失败并发布 Failed。活跃组件可在 Unity 调用 Start 前先等待；disabled / inactive 且仍为 Idle 的组件会立即报告接线错误，但不污染 completion，修正状态后 Start 仍可完成首次加载。失败后不隐式重试。该 Interface 把终态编排、根异常保存和共享所有权藏在 Config Module 内，业务不再复制 `WaitUntil(Ready or Failed)`。

## Game Flow

`IGameFlow` 是 System 层的宏观业务阶段 Interface；`GameFlow` Implementation 把 `FlowState` 当前状态、最新意图排队、协作取消和每状态子 Context 的所有权保持在同一个深 Module 内。它用 `RegisterOwnedSystem(new GameFlow())` 进入宿主 Context：层感知注册自动登记具体类型与 Interface，宿主拥有 flow，flow 再拥有当前状态子 Context；全局性来自注册在持久根 Context，不来自另造 Mono 状态机。View 在 Command Seam 表达流转意图，持续展示时由查询 Command 返回只读投影；System 与 FlowState 内部可直接解析该 System。最新意图采用“先发布新 owner、再结束旧 task”，并在任何 task / Event 终态交付前清理内部 owner；因此同步 UniTask continuation、`InstallBindings`、注入或事件回调重入 `GoTo/Dispose` 时，最终意图不会被外层旧调用覆盖，已提交状态也不会被回头取消。`OnEnter/OnExit` 可在 worker 结束，但 scope 撤销、`Current`、Event 与 `GoTo` 公共终态只在 Unity 主线程提交。项目侧 `FlowNav` 是只观察 fire-and-forget 终态的 Adapter：正常顶替/销毁取消静默，真实进入失败进入 Log Seam，它不拥有转换规则。`Current` 不单拆 Model，因为它只是转换不变量的一部分；保留在同一 Implementation 能提高 Locality，避免增加一条只做镜像同步的浅 Interface。Editor 诊断直接读取同一 Implementation 的 Current / 进入中 / 退出中 / 待处理快照，并用 Context 树展示状态子 Context，不制造第二份状态真源。

## Command Dispatcher Seam

`ICommandSystem` 是 Context 内统一执行 Command 的可替换命令分发器 Interface；`CommandSystem` 是默认 Implementation，`LoggingCommandSystem` 是已被真实使用的装饰器 Adapter。类型名中的 `System` 是兼容保留的早期公共命名，不代表五层业务 `ISystem`：它不获得 System 层能力，也必须用精确契约 `RegisterValue(..., typeof(ICommandSystem))` 注册，而不是 `RegisterSystem`。异步 Command 可以下 worker 做纯计算，但 dispatcher 的成功、失败与取消公共终态必须回 Unity 主线程后交付；默认实现封闭这条边界，日志装饰器也独立兜底自定义 inner，避免无锁流水和调用方续体落到 worker。这个 Interface 通过日志、回放、撤销和测试拦截保持有价值的 Seam；当前不新增 `ICommandDispatcher` 别名，因为容器按精确类型键解析，双 Interface 会制造两份可分叉注册。若未来破坏性版本改名，应一次性迁移 Interface、默认 Implementation、装饰器与 DI key。

## Layer-Aware Composition Registration

手写 `InstallBindings` 中的普通纯 C# 分层对象统一走 `RegisterModel/System/Utility` 或对应 `RegisterOwnedXxx`：由运行时具体类型一次推导“具体 Implementation + 所有派生自该层标记的 Interface”，不登记层标记本身。低层 `RegisterValue/RegisterOwned(value, contracts)` 只留给 `ICommandSystem` 等非分层基础设施、刻意选择性暴露契约和需要在 `.g.cs` 中展示最终清单的生成安装器；Factory 是显式接线 Seam，继续显式列 contract。该约定让手写、Mono 自动注册与生成路径共享契约口径，同时避免调用点重复维护 `typeof(I...Utility)`。运行时分层注册会先预检完整 contract 集再一次提交；任一共享 Interface 冲突都不会留下具体类型或其它 Interface 的部分覆盖。

## Context Affinity

持有 `IHasGameContext` 的实例只能处于“尚未绑定”或“属于唯一底层 `GameContext`”两种状态。同一底层 Context（包括它的 Mono 代理）重复附着是幂等操作，跨 Context 附着则 fail-fast。构建期值组合会在任何 `[Inject]` 前预检整批实例；公开 `Inject`、运行时 `RegisterModel/System/Utility` 与 Mono 自动挂接也在字段或 Container 写入前执行同一检查。失败时回滚已接管的 owned 资源且不留下动态 override，避免出现“字段快照来自 Context B、扩展方法仍通过 Context A 解析”的双重真源。真正不读取 Context 的无状态值可不实现 `IHasGameContext` 并由多个作用域共享；需要 Context 能力的服务则应按作用域创建独立实例。

## Context Resolution Evidence

`Container` 在 Editor 内为每个请求 Context 惰性聚合的实际成功解析回退：区分正常父链与 `GameContext.Main` 兜底，并记录契约、最终来源和解析次数。“可→Main”只是构造策略；只有本地与父链未命中、且 Main 真正返回实例后才成为“→Main ×N”运行证据。`HasBinding`、失败的 `TryResolve` 与诊断读取都不制造证据；工厂内部跨父级解析会记在真正发起请求的 Context，中间 Container 不重复记账。次数表示 Resolve 次数，不是业务使用次数或静态依赖图。来源采用弱引用，避免旧 Main 因诊断被延寿；整个采集与字段在玩家程序集编译消除。

## Luban Generation Transaction

`Game.Framework.Config.Editor` 内一套 Luban Profile 的可恢复发布 owner。CLI 只写工程临时区；Implementation 先验证 `cs-bin` 代码、根目录 `*.bytes` 数据与由其生成的 manifest，再为正式代码 / 数据两棵独占目录计算同一份差量提交。未变化文件不写并保留 `.meta`，陈旧文件连配对 `.meta` 清理；首次修改前备份两棵树，当前进程内任一步失败就同时恢复，回滚失败则保留 recovery 目录。正式写盘前会重新核对 Generated Output Claim Catalog，并拒绝输出路径现存链上的 symlink / junction；强杀 Editor 或断电不在跨目录原子保证内。这个深 Module 保持在 Config Editor，本身不是 Protobuf 单目录后缀同步的通用 Interface。

## HTTP Request Owner

`HttpUtility` 内一次物理 HTTP 交换的私有 owner：独占传给 Provider 的取消 token，并把 caller、Utility lifetime 与 realtime deadline 三种取消意图汇入该 token。外部 token 只触发 owner 的安全 Cancel，第三方取消回调异常不会逃逸到调用方 `CancellationTokenSource.Cancel()` 或 timer 线程；deadline 用独立 completion signal 与物理 outcome 显式竞速，不在 pending UniTask 上并发多 await，也不使用裸 `CancelAfter`。Provider 成功、失败或取消可在任意线程完成，但公共调用回到 Unity 主线程再交还业务。caller / lifetime 在公共 completion 前取消保持 OCE并优先于 deadline；scope 仍存活时 deadline 折叠 Timeout，Provider 在 owner token 未取消时自发 OCE 属 ConnectionError。

## WebSocket Connection Session

`WebSocketUtility` 内一次成功连接的私有 owner：独占该代接收 token、发送 token、FIFO 队尾、终态 claim 与 teardown barrier。公开 `State=Disconnected` 只表达业务不可用，不保证旧 socket 的每个 Receive continuation 已物理返回；后续 Connect 等旧 Close 与发送 owner 清场后再建立新 session，迟到 Receive 靠物理 socket 快照与 session identity 隔离。只有 current session 能发布一次 `WebSocketClosedEvent`，排队旧帧不得写入新连接；接收或发送传输失败都会终结 current session。该概念保持在 Network Module Implementation，不扩张业务 Interface，也不等同于框架 Context / scope。

## WebSocket Connect Attempt

`WebSocketUtility` 内一次在途建连的临时 owner：持有 linked cancellation token、Disconnect intent，以及只属于本 attempt 的 completion outcome（提交的 Connection Session 或 null）。Connecting 期 Disconnect 等的是这个本地 outcome；caller 取消只脱离等待，Attempt owner 仍会在逻辑发布前 Abort 物理 success-win。它不从可被响应式 State 同步重试改写的全局状态猜结果，因此旧 attempt 不会误关新 session；所有成功、失败与 Dispose 路径都必须在 finally 完成 outcome。

## Framework Module Audit

编辑器侧的 Module Catalog、删除计划与体积证据入口。它以当前目标平台的 Player 编译图确定候选 Module，再读 asmdef、当前已编译 DLL 快照的元数据引用、FrameworkHotUpdateProfile，以及项目 Assets 与已注册 Packages 的全部 `link.xml`，把“源码存在、参与编译、预定义程序集隐式引用规则、当前 DLL 快照消费、全 asmdef 删除阻塞、linker 根、热更完整 DLL 部署、最终 Player 证据”保持正交；`autoReferenced:false` 只关闭 Assembly-CSharp 等预定义程序集的隐式引用，不叫“按需启用”，也不代表 Module 退出 Player 编译图。Core / Boot 删除门禁同时比较 asmdef 声明与当前 DLL 元数据闭包：Core 不得接触任意可选 Framework Player Module（含 Boot），Boot 不得接触 Framework Runtime；闭包中的缺失目标也不能因未进入当前 Catalog 而假绿。审计还经只读反射接缝比较可删除 HybridCLR 热更新构建 Module 所拥有的 HybridCLRSettings、Generate stamp、当前热更拓扑 / AOT 补元数据清单与 DLL 中转 manifest；资源构建 Module 是否安装与这份热更证据保持正交。它不把当前 Editor 中可得的 DLL 变体冒充目标平台 Player，也不把文件存在冒充 DLL 内容相对源码新鲜或已部署，并区分空 Profile 的显式纯 AOT 与缺失 / 重复 Profile。它报告常用组合与任意 Module 入口闭包，并解释受热更依赖传播约束的安全移除事务；不提供含糊的 `SetEnabled(bool)`，也不接管 UPM 安装/版本管理。结论固定分为 Error（结构错误）、Warning（证据缺口或派生状态漂移）、Advisory（已知无条件 linker 保留成本）和 Clear；Module 自有 `preserve="all"` 只会形成蓝色说明，不再把“依赖一致但有意完整保留”渲染成长期黄色故障。窗口打开只绘制轻量说明或会话缓存，明确点击“采集当前证据”才扫描；缓存结果中的全量 Module、第三方目录、全局 linker 规则、进阶 Profile 与原始报告也只先创建 Foldout 导航壳，第一次展开才构建对应子树，关闭再打开复用同一实例；默认展开的风险证据不延迟，懒建响应式行会立即应用当前窗口宽度。一次采集冻结并复用 Asset 路径、PluginImporter 和 Player / Editor 编译图快照，热更新 stamp 校验继续消费同一份 Asset / Player 输入，并仅在本轮复用已读取文件。Player linker 根按“根集合 + 可达依赖并集”一次批量查询，同一共享闭包不再按每个 Resources 根重复遍历；任何下一轮采集仍重新读取当前状态。审计报告阶段耗时，缓存随工程、Package、构建场景、目标平台或编译图变化失效。原始 DLL 字节只用于组合对比，最终包体仍以目标平台 Player BuildReport 为准。

## Framework Build Module Split

Editor 构建能力按真实第三方变化源分成单向两层：`Game.Framework.Build.Editor` 拥有 YooAsset 普通 AssetBundle 的 Profile、构建、部署、本地服务、安全产物路径，以及“包名 + 普通 AssetBundle 引导偏移”的业务代码生成物；构建前以同一渲染函数校验生成物，避免首场景使用陈旧偏移。它不引用 Boot、HybridCLR 或 dnlib。`Game.Framework.Build.HybridCLR.Editor` 作为可删除的下游 Module，拥有热更 Profile、Generate 新鲜度、目标 DLL 编译与 YooAsset RawFile 代码包配方，并复用资源构建侧的版本、部署、预检与路径安全 Implementation。资源 Module 不读取热更 Profile；RawFile 包若误在资源 Profile 启用，会在写产物前明确失败并指向专属构建 Module，也不会继承普通 AssetBundle 的偏移。删除热更新 Module 后资源构建继续成立；保留热更新则必须保留它实际依赖的资源构建、Boot 与 HybridCLR 工具链。

## External Dependency Evidence

Framework Module Audit 内部拥有的第三方依赖证据 Module。它以一方 Player / Editor 消费者与 what-if Profile 为种子，只沿平台范围相交的外部 AssemblyRef 扩展，把同一已注册 Package 的程序集聚合为一组；没有一方种子的 Package 内部边不进入目录。Player、Editor 与 Tests 快照边保持平台语义，Tests 可消费 Editor 依赖，Editor 边不冒充 Player 证据。Assets 预编译 DLL 保留可定位物理变体、Editor 兼容性与完整 BuildTarget 集合，无法还原或范围冲突的程序集显式产生证据问题。Package 安装来源与 manifest 直接/间接关系、当前 DLL 快照的结构化消费者、沿外部 AssemblyRef 链回溯到的首个一方引入者、完整 asmdef 的声明阻塞、按 Profile key 保存的 what-if 原始字节、去重物理文件的已安装二进制字节、静态移除候选和目标构建验证要求保持正交。`RemoveWithOptionalModuleCandidate` 只表示单一可选 Module 没有已知项目消费者，不是 `SafeToRemove` 承诺；可定位到程序集的问题只收紧相关依赖组，无法定位的全局扫描缺口才收紧全部组，且都不抹掉已成立的角色事实。窗口只消费这一份模型并提供定位/复制，不调用 Package Manager 安装或卸载。

## Framework Build Size Probe

Framework Module Audit 的真实玩家构建验证 Adapter。它在 `Library` 下创建隔离空工程，只复制某个审计组合的 Runtime Module Implementation 与当前版本依赖，再调用当前目标平台 Player Build；主工程的业务场景、未选 Module、`link.xml` 和 HybridCLR 生成物都不进入证据。实际 DLL 闭包与 asmdef 声明闭包共同决定需要复制、完整保留的 Framework Module，declared-only 依赖不会因当前 IL 未使用而缺席。外部依赖再由 Framework Module Source Catalog 唯一决定 Package 名、安装来源和物理目录：registry Package 复用主工程版本与 scoped registry，并在整轮启动时按档冻结 manifest 文本与指纹；Git / embedded / local / tarball Package 从已解析源码根整体复制，身份去掉本机路径与 Git URL 凭据并记录内容指纹；复制 Package 中的相对 `file:` 传递依赖 fail-fast；Assets 外部依赖或未知显式来源同样 fail-fast，不维护 Module 名称映射。任何递归读取、复制、指纹与清理先验证完整物理目录树，遇到 symbolic link、junction 或其它 reparse point 会在改动前 fail-fast，避免词法上仍在工程内的路径穿透到外部目录。窗口打开与档位预览不计算全矩阵内容指纹；真正启动时重新采集 Audit，并只冻结所请求档位，预览缓存绝不充当执行证据。运行目录与结果/日志路径不进入分享 JSON，恢复时由本机 latest-run 指针重建。聚合 `nuget-packages` 仍是一个物理复制边界，不冒充单 DLL 可卸载能力。所选程序集完整保留，因此结果是确定性的体积上界，不是假装成具体游戏实际用量的包体承诺。

## Framework Module Source Catalog

Editor 侧把 Unity 资产身份还原为物理源码与 Package 所有权的唯一 owner。它接受 `Assets/...`、`Packages/...` 或已解析的绝对路径，统一给出 canonical Asset Path、真实 Physical Path、源码根、package id、安装来源与 manifest 直接/间接关系；后两者只描述 Package 解析事实，不推导代码消费者或可移除性。Module Audit、隔离 Build Size Probe 和源码门禁都通过它读取 asmdef、`link.xml` 与模板。AssetDatabase 已知候选不可读时 fail-fast，不把证据缺失静默解释成没有规则；框架位于 Assets、嵌入包或 registry/Git PackageCache 时共享同一份证据，不各自猜测文件系统布局。Build Size Probe 另对实际复制的 Runtime Module 与 Git / embedded / local / tarball Package 生成内容指纹，使 Domain Reload 恢复能识别“路径和版本未变、源码已变”的漂移。

## Framework Editor Catalog

通用工具中心与配置中心使用的 Editor-only 导航 Catalog。可选 Module 分别通过 `FrameworkToolRegistry` 和 `FrameworkConfigRegistry` 登记自己的标题、说明、工作台路径，以及 Profile 的真实类型和数量语义；中央窗口只消费稳定快照，不维护可选程序集限定类型名，也不复制生成、构建或配置 Implementation。`FrameworkEditorProfileCatalog` 缓存“某类型有哪些资产路径”的发现快照，按 revision 在 `projectChanged` 时统一失效，并供配置中心、所有 Framework Profile owner 与只读审计复用；通用 stable-first loader 遇到“非空首路径已无法加载”这类确定陈旧证据时，只刷新该类型并重试一次。单例 owner 仍负责每 revision 一次的多份 Warning，多份 owner、默认初始化、创建与业务校验同样留在所属 Module。固定路径自动创建先强制刷新类型、确认默认目录与目标没有 reparse / 异类型 / 未导入文件碰撞，创建后再刷新并验证新资产就是稳定生效项；显式类型刷新本身不清空其它快照，但 Unity 随后的全局 `projectChanged` 仍可统一失效它们。删除 Module 后，其注册随域重载自然消失；相同 id 的不同元数据 fail-fast，避免后加载 Adapter 静默覆盖卡片。这个 Seam 只负责发现与安全加载，不创建资产、不执行副作用，也不代替 Unity Package Manager。

## Framework Generated Output Claim Catalog

可选 Editor 生成器之间共享的输出与清理边界 Seam。每个 Module 自注册只读 collector，把已成立的目标描述成独占目录（Exclusive Directory）、递归文件后缀（Recursive File Suffix）或精确文件（Exact File）；Catalog 只比较经 `FrameworkProjectPath` 规范化的声明，不读取生成器 Profile 类型，也不接管输入、工具链或写盘 Implementation。工作台预览严格只消费已有外部快照；冷启动或 `projectChanged` 后缺少的 Module 会明确标成“待写盘前重采”，不会在窗口绘制链暗中执行 collector。任何创建、覆盖或清理动作前必须强制重采集；collector 失败会 fail-fast，不能把证据缺失解释成没有冲突。删除一个 Module 后，其声明来源随域重载自然消失。

## UI Binding Prefab Catalog

`Game.Framework.UI.UGui.Editor` 内用于输出 claim 的候选 Prefab 索引。首次需要完整写盘证据或人工点击“重新扫描”时加载全工程 Prefab 一次；之后由 AssetPostprocessor 检查导入、移动和删除的 Prefab，并沿会话内记录的 Prefab Variant 依赖图重验后代，即使 Unity 回调只报告基 Prefab 也不会留下陈旧候选。候选路径与 Variant 依赖一起用 `SessionState` 跨脚本域重载复用。索引只回答“根上是否有 `UIBindingData`”，不缓存条目、Profile 或目录覆盖；collector 仍重新加载命中的少量 Prefab 并解析当前输出。预览缺少快照时只报告待采，不暗中扫描；真实写盘证据缺失时必须回退完整扫描，不能把不完整增量集合冒充安全。这个深 Implementation 留在 UI.UGui Editor Module；删除该 Module 后索引、Postprocessor 与 claim 来源一起消失，不为单一消费者抽中央资产引用 Interface。

## Protobuf Generation Preview

`Game.Framework.Network.Proto.Editor` 工作台对一组 `ProtoConfigProfile` 的只读输入快照。只有人工点击“重新扫描”才递归读取 `.proto` 目录；IMGUI Layout / Repaint 只消费快照。`FrameworkEditorProfileCatalog` revision 或 Profile 的 protoc / 源 / 输出路径变化会廉价标记快照失效，不在重绘中自动重扫。这份预览只决定卡片与按钮状态；真正生成仍绕过缓存，重新验证当前磁盘、工具和输出 claim。

## Mono Context Initialization Issue Group

Editor 诊断窗口把逐宿主初始化快照还原出的维护单元：有异常时，只有共享同一最深异常对象且存在实际 Mono 父子链的宿主才属于同一根因组；无异常的 `Uninitialized/Initializing` 父子链是“时序提醒”，不计入根因。它区分最先失败 / 最上游未就绪、受影响链和当前 Play / 历史证据，只用于解释与定位，不制造可用 `GameContext`，也不进入玩家运行时 Interface。
