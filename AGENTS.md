# SSFramework 仓库协作入口

本仓库只维护 Framework Package，不包含任何游戏、教程 Demo 或项目级 Unity 工程设置。

- 源码与程序集边界：`src/`
- 模块职责与删除测试：`docs/framework-module-map.md`
- 框架 API：`docs/framework-guide.md`
- 架构决策：`docs/adr/`

Framework 的 Editor 工具必须通过 Package/Asset Catalog 定位源码，不能假设安装位置一定是 `Assets/Game/Framework`。任何跨项目能力必须先经过真实消费方验证，再回流到本仓库。
