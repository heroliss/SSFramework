# SSFramework 仓库协作入口

本仓库只维护 Framework Package，不包含任何游戏、教程 Demo 或项目级 Unity 工程设置。

- 源码与程序集边界：`src/`
- 模块职责与删除测试：`docs/framework-module-map.md`
- 框架 API：`docs/framework-guide.md`
- 架构决策：`docs/adr/`
- 文档索引与生命周期：`docs/README.md`

Framework 的 Editor 工具必须通过 Package/Asset Catalog 定位源码，不能假设安装位置一定是 `Packages/com.liss.ssframework/src`。任何跨项目能力必须先经过真实消费方验证，再回流到本仓库。

按任务读取当前指南和相关 ADR；提案、审查记录与历史证据不作为默认开发指令。完成修改时同步受影响的当前文档，并收尾对应临时方案，避免新增重复的进度文档。
