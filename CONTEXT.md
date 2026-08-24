# SSFramework Domain Context

本文件只记录跨代码、测试与文档都需要稳定使用的领域词汇；架构取舍仍以 `docs/adr/` 为准。

## Demo Module

一个可自动发现的框架教学章节 Adapter。它贡献章节所需的容器绑定，并在被选中时构建可交互内容；同一实例可以经历多轮 Build / Teardown，但只属于一轮 Demo 根 Context。

## Demo Module Catalog

Demo 章节实例、排序校验与生命周期的唯一 owner。它一次构造所有 Demo Module，让同一实例按 Discover → InstallBindings → Initialize → Build / Teardown 执行，并持有当前章节 Host，保证取消发生在 Teardown 之前。`MonoDemoContext` 持有 Catalog，`DemoShellController` 只负责展示和选择。

## Demo Teaching Contract

Demo 教学内容与自动化之间的运行时 Seam。`DemoModuleHost` 在真实 Build 中记录定位、步骤、概念、动作、结果与源码引用等语义，`DemoModuleCatalog` 再按 Capability / Concept / Workflow 三种教学形态校验；缺少场景 Adapter 时改查“原因 → 恢复 → 继续学习”的结构化降级闭环。它验证实际执行的内容，不扫描源码 token，也不猜测 USS / VisualElement Implementation。
