# 命名约定

仓库名、Unity Package ID 和 C# 命名空间承担不同职责，保持分层可以减少迁移时的破坏面。

- **仓库名**使用产品或工程名：`SSFramework`、`Outpost`、`FrameworkTutorial`、`NomadWorkshop`。
- **UPM Package ID** 使用反向域名：当前框架是 `com.liss.ssframework`。只有在模块需要独立安装、版本化和发布时，才新增 `com.liss.ssframework.<module>`；普通目录和程序集不为凑前缀单独包装。
- **Framework C# 命名空间**暂时保持 `Game.Framework.*`。它已经被公共 API、测试和消费方使用，立即改成 `Liss.SSFramework.*` 会制造无功能收益的迁移成本；未来若要改名，应放在明确的主版本迁移中，并提供兼容层。
- **FrameworkTutorial** 的公开仓库名已从 `DemoScene` 改为教程语义。其 Unity 资产路径和 `Game.Framework.Demo` 程序集标识暂时保留，待通过 Unity Editor/MCP 完成序列化迁移后再改为 `Tutorial`，不手改场景或 Prefab YAML。
- **消费方命名空间**只归属各自工程，不能反向进入 Framework；跨仓库可复用能力通过包 API 提供。

这套约定允许仓库名称先表达产品用途，同时保留现有 Unity 序列化与 API 的兼容性。
