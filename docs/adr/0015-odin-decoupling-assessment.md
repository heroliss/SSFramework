# ADR-0015：Unity 原生基线与 Odin 可选增强边界

**Status:** Accepted（Unity 原生基线已落地；Odin Adapter 不属于当前 Framework Package）

## Context

早期 Framework 的 Mono 基类和 Editor 工具直接依赖 Odin Inspector。这个选择能快速提供 Inspector 体验，却把商业插件的安装、授权和版本耦合带进 Core、Editor 与测试程序集，也阻碍 Framework 作为独立 UPM Package 分发。

Odin 的专业 Inspector 仍然有价值，但“项目可以选择使用”不等于“Framework 必须携带”。可复用包应先保证没有 Odin 时仍能编译、诊断和维护，再由消费工程或独立扩展包自行承担插件接入。

## Decision

### 1. Framework 基线不依赖 Sirenix

- `Game.Framework` 的 Mono Context、Model、System、Utility、View 以 Unity `MonoBehaviour` 为基类。
- Inspector 可拖拽的 Context 引用使用具体 `MonoGameContextBase` 字段；纯 C# 父 Context 仍走初始化前的 `IGameContext` 代码装配。
- 资源包下拉、UI 代码生成目录选择器、Context/服务运行时诊断和自检按钮由 Unity 原生 `PropertyDrawer` / `Editor` 提供。
- Core、Fonts、通用 Editor、UGUI Editor、Build Size Probe 与测试程序集不得声明 Sirenix 引用。当前仓库不包含 `Game.Framework.Odin.Editor`；若未来需要同类能力，应作为独立扩展包验证删除边界，不能回写 Core 的公共依赖。

这不是重新实现 Odin。原生工具只覆盖 Framework 自己稳定、明确的编辑需求；业务对象的通用字典、接口和多态绘制可由消费工程自行选择 Odin 或 Unity `[SerializeReference]` 等方案。

### 2. Odin 是消费工程的可选工具

消费工程可以自行安装 Odin，并在自己的 Editor 程序集或独立扩展包中接入。该接入必须：

- 只引用已安装的 Odin，不把插件 DLL、源码或许可证文件复制进 Framework Package；
- 保持与 Framework 原生 Inspector 的清晰所有权，不依赖隐式 CustomEditor 优先级；
- 能在移除 Odin 后恢复 Framework 的原生 Inspector 和诊断；
- 为实际需要提供独立测试和删除证据，再考虑长期维护为公共扩展。

### 3. 不按平台 define 切换序列化基类

不采用 `#if ODIN_INSPECTOR` 在 `SerializedMonoBehaviour` 与 `MonoBehaviour` 间切换。切换基类会改变场景/Prefab 的序列化布局，使“换平台”变成隐含的数据迁移。可选能力只能位于消费工程或独立 Editor 扩展，不改变 Core 类型布局。

### 4. 旧资产迁移由 Unity Editor 负责

从旧项目基线迁移时，使用 `AssetDatabase`、`PrefabUtility` 和 `ForceReserializeAssets` 处理序列化引用，禁止手改 YAML。外部消费工程若保存过非空 Odin Context 引用，应在升级前记录引用关系，升级后改填 Framework 原生的 Parent Context / Target Context 字段。

业务派生类也不应从 Framework 基类“顺带继承”Odin 序列化；业务自己的 Odin 字段应显式放在消费工程的数据对象或自有 Odin 宿主中。

## Consequences

- ✅ Framework Core 与通用工具可在未安装 Odin 的项目中使用，商业授权不再是框架入门门槛。
- ✅ Framework Package 不分发付费插件，UPM、Git 和本地接入的边界清晰。
- ✅ 消费工程仍可保留完整 Odin 能力，但安装、版本、授权、升级和删除由消费工程负责。
- ⚠️ 业务代码不能假设继承 `MonoXxxBase` 就自动获得 Odin 序列化；这是明确的边界。
- ⚠️ 独立 Odin 扩展若要进入公共维护范围，必须补充真实消费者、删除测试和干净工程安装证据。

## Related

- [ADR-0006：第一阶段接受 Odin 硬依赖](0006-odin-dependency.md)
- [ADR-0010：框架复用边界与 UPM 包形态](0010-framework-reusability-upm.md)
- [可选 Odin 集成](../optional-odin-integration.md)
- [Framework Module 地图](../framework-module-map.md)