# SSFramework Domain Context

本文件只记录跨代码、测试与文档都需要稳定使用的领域词汇；架构取舍仍以 `docs/adr/` 为准。

## Demo Module

一个可自动发现的框架教学章节 Adapter。它贡献章节所需的容器绑定，并在被选中时构建可交互内容；同一实例可以经历多轮 Build / Teardown，但只属于一轮 Demo 根 Context。

## Demo Module Catalog

Demo 章节实例、排序校验与生命周期的唯一 owner。它一次构造所有 Demo Module，让同一实例按 Discover → InstallBindings → Initialize → Build / Teardown 执行，并持有当前章节 Host，保证取消发生在 Teardown 之前。`MonoDemoContext` 持有 Catalog，`DemoShellController` 只负责展示和选择。

## Demo Teaching Contract

Demo 教学内容与自动化之间的运行时 Seam。`DemoModuleHost` 在真实 Build 中记录定位、步骤、概念、动作、结果与源码引用等语义，`DemoModuleCatalog` 再按 Capability / Concept / Workflow 三种教学形态校验；缺少场景 Adapter 时改查“原因 → 恢复 → 继续学习”的结构化降级闭环。它验证实际执行的内容，不扫描源码 token，也不猜测 USS / VisualElement Implementation。

## Asset Location Snapshot

`IAssetUtility.GetLocationState` 对某个 package/location 当前清单与本地缓存的同步四态快照：PackageNotReady、Invalid、AvailableLocally、RequiresDownload。它是资源 Module 的高杠杆 Interface，替调用方收口“先守卫初始化，再拼地址有效性与下载需求”的重复编排；具体未就绪原因仍由正交的 `AssetInitState` 表达，YooAsset 的布尔查询与 Reader/Writer 协调保留在 Adapter Implementation 内。

## Framework Module Audit

编辑器侧的 Module Catalog、删除计划与体积证据入口。它以当前目标平台的 Player 编译图确定候选 Module，再读 asmdef、已编译 DLL 的真实元数据引用、FrameworkHotUpdateProfile 和全部 `Assets/**/link.xml`，把“源码存在、参与编译、Player 真实消费、全 asmdef 删除阻塞、linker 根、热更完整 DLL 部署、最终 Player 证据”保持正交；并经只读反射接缝比较可删除 Build Editor Module 所拥有的 HybridCLRSettings、Generate stamp、当前热更拓扑 / AOT 补元数据清单与 DLL 中转 manifest。它不把文件存在冒充 DLL 内容相对源码新鲜或已部署，并区分空 Profile 的显式纯 AOT 与缺失 / 重复 Profile。它报告常用组合与任意 Module 入口闭包，并解释受热更依赖传播约束的安全移除事务；不提供含糊的 `SetEnabled(bool)`，也不接管 UPM 安装/版本管理。原始 DLL 字节只用于组合对比，最终包体仍以目标平台 Player BuildReport 为准。

## Framework Build Size Probe

Framework Module Audit 的真实玩家构建验证 Adapter。它在 `Library` 下创建隔离空工程，只复制某个审计组合的 Runtime Module Implementation 与当前版本依赖，再调用当前目标平台 Player Build；主工程的业务场景、未选 Module、`link.xml` 和 HybridCLR 生成物都不进入证据。所选程序集完整保留，因此结果是确定性的体积上界，不是假装成具体游戏实际用量的包体承诺。
