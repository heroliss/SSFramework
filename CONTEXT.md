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

编辑器侧的 Module 删除测试与体积证据入口。它以当前目标平台的 Player 编译图确定候选 Module，再读已编译 DLL 的真实元数据引用，避免把 `auto-reference` 带来的“编译可见”误当成“运行时依赖”；报告 Core-only / 单 UI 后端 / 完整 / 当前热更档位的原始托管闭包。原始 DLL 字节只用于组合对比，最终包体仍以目标平台 Player BuildReport 为准。
