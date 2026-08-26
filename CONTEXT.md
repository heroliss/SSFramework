# SSFramework Domain Context

本文件只记录跨代码、测试与文档都需要稳定使用的领域词汇；架构取舍仍以 `docs/adr/` 为准。

## Demo Module

一个可自动发现的框架教学章节 Adapter。它贡献章节所需的容器绑定，并在被选中时构建可交互内容；同一实例可以经历多轮 Build / Teardown，但只属于一轮 Demo 根 Context。

## Demo Module Catalog

Demo 章节实例、排序校验与生命周期的唯一 owner。它一次构造所有 Demo Module，让同一实例按 Discover → InstallBindings → Initialize → Build / Teardown 执行，并持有当前章节 Host，保证取消发生在 Teardown 之前。`MonoDemoContext` 持有 Catalog，`DemoShellController` 只负责展示和选择。

## Demo Teaching Contract

Demo 教学内容与自动化之间的运行时 Seam。`DemoModuleHost` 在真实 Build 中记录定位、步骤、概念、动作、结果与源码引用等语义，`DemoModuleCatalog` 再按 Capability / Concept / Workflow 三种教学形态校验；缺少场景 Adapter 时改查“原因 → 恢复 → 继续学习”的结构化降级闭环。故意失败或带持久/共享副作用的动作使用 Experiment Notice + Experiment Action，在同一小节形成“影响范围 → 预期证据 → 恢复方式 → 可执行动作”的机器可读顺序；预期异常由 Module 本地精确捕获，Host 兜底只表示真实 Demo 缺陷。契约验证实际执行的内容，不扫描源码 token，也不猜测 USS / VisualElement Implementation。

## Asset Location Snapshot

`IAssetUtility.GetLocationState` 对某个 package/location 当前清单与本地缓存的同步四态快照：PackageNotReady、Invalid、AvailableLocally、RequiresDownload。它是资源 Module 的高杠杆 Interface，替调用方收口“先守卫初始化，再拼地址有效性与下载需求”的重复编排；具体未就绪原因仍由正交的 `AssetInitState` 表达，YooAsset 的布尔查询与 Reader/Writer 协调保留在 Adapter Implementation 内。

## Framework Module Audit

编辑器侧的 Module Catalog、删除计划与体积证据入口。它以当前目标平台的 Player 编译图确定候选 Module，再读 asmdef、当前已编译 DLL 快照的元数据引用、FrameworkHotUpdateProfile，以及项目 Assets 与已注册 Packages 的全部 `link.xml`，把“源码存在、参与编译、当前 DLL 快照消费、全 asmdef 删除阻塞、linker 根、热更完整 DLL 部署、最终 Player 证据”保持正交；并经只读反射接缝比较可删除 Build Editor Module 所拥有的 HybridCLRSettings、Generate stamp、当前热更拓扑 / AOT 补元数据清单与 DLL 中转 manifest。它不把当前 Editor 中可得的 DLL 变体冒充目标平台 Player，也不把文件存在冒充 DLL 内容相对源码新鲜或已部署，并区分空 Profile 的显式纯 AOT 与缺失 / 重复 Profile。它报告常用组合与任意 Module 入口闭包，并解释受热更依赖传播约束的安全移除事务；不提供含糊的 `SetEnabled(bool)`，也不接管 UPM 安装/版本管理。原始 DLL 字节只用于组合对比，最终包体仍以目标平台 Player BuildReport 为准。

## External Dependency Evidence

Framework Module Audit 内部拥有的第三方依赖证据 Module。它以一方 Player / Editor 消费者与 what-if Profile 为种子，只沿平台范围相交的外部 AssemblyRef 扩展，把同一已注册 Package 的程序集聚合为一组；没有一方种子的 Package 内部边不进入目录。Player、Editor 与 Tests 快照边保持平台语义，Tests 可消费 Editor 依赖，Editor 边不冒充 Player 证据。Assets 预编译 DLL 保留可定位物理变体、Editor 兼容性与完整 BuildTarget 集合，无法还原或范围冲突的程序集显式产生证据问题。Package 安装来源与 manifest 直接/间接关系、当前 DLL 快照的结构化消费者、沿外部 AssemblyRef 链回溯到的首个一方引入者、完整 asmdef 的声明阻塞、按 Profile key 保存的 what-if 原始字节、去重物理文件的已安装二进制字节、静态移除候选和目标构建验证要求保持正交。`RemoveWithOptionalModuleCandidate` 只表示单一可选 Module 没有已知项目消费者，不是 `SafeToRemove` 承诺；可定位到程序集的问题只收紧相关依赖组，无法定位的全局扫描缺口才收紧全部组，且都不抹掉已成立的角色事实。窗口只消费这一份模型并提供定位/复制，不调用 Package Manager 安装或卸载。

## Framework Build Size Probe

Framework Module Audit 的真实玩家构建验证 Adapter。它在 `Library` 下创建隔离空工程，只复制某个审计组合的 Runtime Module Implementation 与当前版本依赖，再调用当前目标平台 Player Build；主工程的业务场景、未选 Module、`link.xml` 和 HybridCLR 生成物都不进入证据。所选程序集完整保留，因此结果是确定性的体积上界，不是假装成具体游戏实际用量的包体承诺。

## Framework Module Source Catalog

Editor 侧把 Unity 资产身份还原为物理源码与 Package 所有权的唯一 owner。它接受 `Assets/...`、`Packages/...` 或已解析的绝对路径，统一给出 canonical Asset Path、真实 Physical Path、源码根、package id、安装来源与 manifest 直接/间接关系；后两者只描述 Package 解析事实，不推导代码消费者或可移除性。Module Audit、隔离 Build Size Probe 和源码门禁都通过它读取 asmdef、`link.xml` 与模板。AssetDatabase 已知候选不可读时 fail-fast，不把证据缺失静默解释成没有规则；框架位于 Assets、嵌入包或 registry/Git PackageCache 时共享同一份证据，不各自猜测文件系统布局。Build Size Probe 另对实际复制的 Runtime 文件生成内容指纹，使 Domain Reload 恢复能识别“路径和版本未变、源码已变”的漂移。

## Mono Context Initialization Issue Group

Editor 诊断窗口把逐宿主初始化快照还原出的维护单元：有异常时，只有共享同一最深异常对象且存在实际 Mono 父子链的宿主才属于同一根因组；无异常的 `Uninitialized/Initializing` 父子链是“时序提醒”，不计入根因。它区分最先失败 / 最上游未就绪、受影响链和当前 Play / 历史证据，只用于解释与定位，不制造可用 `GameContext`，也不进入玩家运行时 Interface。
