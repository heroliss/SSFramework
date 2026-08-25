# ADR-0015：Unity 原生基线与 Odin 可选增强边界

**Status:** Accepted（取代 [ADR-0006](0006-odin-dependency.md)）

## Context

框架开始按 Module 做删除测试并准备 UPM 抽包后，Odin 的依赖深度暴露为 D3：Core 的 Mono 基类继承
`SerializedMonoBehaviour`，Core、Fonts、UGUI Editor 与多组测试程序集直接引用 Sirenix；任意使用
Framework Mono 基类的业务程序集都会间接承受该依赖。

Odin 的专业 Inspector、文档与第三方维护仍然有价值，但“推荐使用”不等于“框架运行必需”。此外，Odin
按项目中的 Editor 使用席位授权；不使用 Odin 的框架分发物也不能附带付费插件本体。授权与分发边界应以
[官方定价说明](https://odininspector.com/pricing)和 [Odin EULA](https://odininspector.com/eula) 为准。

## Decision

### 1. Framework 基线不依赖 Sirenix

- `Game.Framework` 的 Mono Context、Model、System、Utility、View 全部以 Unity `MonoBehaviour` 为基类。
- Inspector 可拖拽的 Context 引用使用具体 `MonoGameContextBase` 字段；纯 C# 父 Context 仍走初始化前的
  `IGameContext` 代码装配，两种所有权不混进同一个序列化字段。
- 资源包下拉、UI 代码生成目录选择器、Context/服务运行时诊断和自检按钮由 Unity 原生
  `PropertyDrawer` / `Editor` 提供。
- Core、Fonts、通用 Editor、UGUI Editor、Build Size Probe 与测试程序集不得声明 Sirenix 引用；唯一例外是
  可整体删除的 `Game.Framework.Odin.Editor` Adapter。`FrameworkOptionalDependencyTests` 会递归检查基线的
  已编译依赖闭包，并禁止任何反向指向 Adapter/Sirenix 的路径。

这不是重新实现 Odin。原生工具只覆盖 Framework 自己稳定、明确的编辑需求；任意业务对象的通用字典、
接口、多态绘制仍可由开发者自行选择 Odin 或 Unity `[SerializeReference]` 等方案。

### 2. Odin 保留为项目级可选专业工具

当前仓库可以继续安装 Odin，业务开发者也可以使用其完整能力；删除 `Assets/Plugins/Sirenix` 后，Framework
源码和通用工具仍应编译、配置和诊断。未来发布 Framework Package 时不携带 Odin DLL、源码或许可证文件。

已建立独立 `Game.Framework.Odin.Editor` Adapter Module，因为“同时保留 Odin 属性绘制与 Framework 运行时诊断”
是可验证的真实增量，而非装饰：

- 单向依赖 `Game.Framework.Editor` 与用户已安装的 Odin；
- Domain Reload 后通过 Odin 官方
  [`CustomEditorUtility`](https://odininspector.com/documentation/sirenix.odininspector.editor.customeditorutility)
  临时接管满足条件的具体 Framework Mono 类型，组合完整
  Odin Inspector 与 Framework 诊断；不修改 Odin 配置资产，也不靠 CustomEditor 优先级猜测；
- 只替换当前 Framework 原生 fallback，尊重普通静态业务 Editor、Odin 逐类型排除和按程序集分类的全局开关；
  InspectorConfig 保存/导入后双向同步，禁用时归还原生 Inspector，不把临时映射写回第三方资产；
- 删除整个 Adapter 后原生体验完整可用；
- 不复制、不重打包、不转售 Odin 本体。

通用 Editor 保留五个原生 fallback；安装 Adapter 时测试实际 `Editor.CreateEditor` 的所有权，未安装时原生接管。
后续仍不为目录对称添加空 Attribute/Drawer。

### 3. 不按平台 define 切换序列化基类

不采用 `#if ODIN_INSPECTOR` 在 `SerializedMonoBehaviour` 与 `MonoBehaviour` 间切换。Odin 当前位于 Assets
插件而非标准 UPM 依赖，define 可能因目标平台或安装状态漂移；更关键的是切换基类会改变场景/Prefab 的
序列化布局，使“换平台”变成隐含的数据迁移。可选能力只能位于独立 Editor Adapter，不改变 Core 类型布局。

### 4. 旧资产由 Unity 迁移

落地前审计了 6 个场景/Prefab 中 34 段 Odin Context Entry（29 个 `_targetContext`、5 个
`_parentContext`）：这些业务字段均为空，没有对象引用或字节数据；另发现并通过 `PrefabUtility` 清理了 1 条
Odin 自引用 metadata override（`serializationData.Prefab`），它不是业务 Context 引用。其余迁移使用 Unity
`AssetDatabase.ForceReserializeAssets` 精确重序列化，禁止手改 YAML。若外部项目曾实际保存非空 Odin Context
引用，升级前必须先记录引用关系，升级后改填原生 `Parent Context` / `Target Context` 字段。

业务派生类也不再从 Framework 基类“顺带继承”Odin 序列化。业务自己的 Odin 字段应显式继承/组合自己的
Odin 宿主或放在业务侧数据对象中，不应让 Framework Core 的继承树承担插件所有权。

## Consequences

- ✅ Framework Core 与通用工具可在未安装 Odin 的项目中使用，付费授权不再是框架入门门槛。
- ✅ 保留 Odin 作为项目级专业工具，不牺牲愿意购买插件团队的完整能力。
- ✅ 原生 fallback 保证无插件可用；可选 Odin Adapter 以无持久化的临时 Editor 映射组合 Odin 绘制和
  Framework 诊断。字段 Drawer 与 Header hook 继续覆盖对应场景，真实业务组件的 Editor 所有权有实际测试。
- ✅ 隔离构建探针不再偷偷复制 Odin，Core-only 体积证据更诚实。
- ⚠️ 业务代码不能再假设继承 `MonoXxxBase` 就自动获得 Odin 序列化；这是明确的迁移边界。
- ⚠️ 本次只证明 Framework 代码依赖已清零；真正发布无 Odin 包时仍需在干净工程做安装/编译验证。
- 🔮 [Odin Serializer](https://github.com/TeamSirenix/odin-serializer) 虽为 Apache-2.0 开源，但把它继续留在 Core
  会引入独立序列化器的版本、AOT 与维护责任，因此当前不采用。

关联：[ADR-0010](0010-framework-reusability-upm.md)、[ADR-0038](0038-isolated-framework-build-size-probe.md)、
[Framework Module 地图](../framework-module-map.md)。
