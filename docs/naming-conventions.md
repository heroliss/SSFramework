# 命名约定

仓库名、Unity Package ID、程序集名和 C# 命名空间承担不同职责。保持这几层分开，可以让包升级和业务工程重组互不牵连。

- **Package ID** 使用反向域名：`com.liss.ssframework`。只有在模块需要独立安装、版本化和发布时，才新增 `com.liss.ssframework.<module>`；普通目录和程序集不为了凑前缀单独包装。
- **Framework C# 命名空间** 当前保持 `Game.Framework.*`。它已经被公共 API、测试和程序集引用，改名应放在明确的主版本迁移中，并提供兼容层。
- **程序集名** 用职责表达边界，例如 Core、UI、Asset、Config 和 Build；测试程序集用对应生产程序集加 `.Tests` 后缀。
- **类型名** 使用清晰的英文领域名；`Model`、`System`、`View`、`Command`、`Event` 和 `Utility` 只在类型确实承担对应职责时使用。
- **消费工程的命名** 由消费方自行决定，不能反向进入 Framework 的公共命名空间、包路径或默认配置。

命名的目标是让代码、程序集和文档可以相互检索，而不是强迫每个项目采用同一套产品名称。需要兼容已有序列化资产时，应通过 Unity Editor 完成迁移，不手改场景或 Prefab YAML。
