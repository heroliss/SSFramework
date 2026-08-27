# Odin 可选集成与移除指南

Odin Inspector 是值得推荐的专业 Unity Editor 工具，但不是 SSFramework 的运行前置。框架采用“Unity
原生基线 + 项目级可选增强”：未购买 Odin 的开发者可以完整使用框架；已购买的团队仍可在业务代码中使用
Odin 的 Inspector、Validator、序列化与工作流能力。

## 当前边界

| 范围 | 是否依赖 Odin | 说明 |
|---|---:|---|
| `Game.Framework` Core | 否 | Mono 基类、Context 引用与运行时诊断均为 Unity 原生实现 |
| 通用 Framework Editor | 否 | 资源包下拉、原生 fallback Inspector、字段锁、Header 诊断和 Module Audit 不需要 Odin |
| 可选 Runtime Module | 否 | Fonts、UI、YooAsset Adapter、Protobuf Adapter 等各自只承担其真实第三方依赖 |
| 当前项目的业务代码 | 可选 | 可以自行使用 Odin，但依赖与许可证由项目拥有者负责 |
| `Game.Framework.Odin.Editor` | 是，可整体删除 | 用 OdinEditor 绘制业务字段并追加 Framework 诊断；只引用用户已安装插件，不携带 Odin 本体 |

## 为什么不在 Core 里保留条件编译

用 `#if ODIN_INSPECTOR` 切换 `SerializedMonoBehaviour` 看似兼容两种安装方式，实际会让同一个组件在不同目标
平台或不同开发机上拥有不同序列化布局。define 漂移后，场景和 Prefab 可能静默丢字段；而 Odin 位于
`Assets` 插件时也不能可靠使用 UPM `versionDefines` 探测。因此 Core 类型布局必须稳定，可选增强只能在独立
Editor Adapter 中叠加。

## 想移除 Odin 时

1. 先在项目源码和 asmdef 中搜索 `Sirenix`、`SerializedMonoBehaviour`、`OdinSerialize`；这些属于项目依赖，
   不会被 Framework 自动代换。
2. 检查场景/Prefab 是否保存了实际 Odin 数据。不要手改 YAML；应先在安装 Odin 的版本中导出或改填为
   Unity 原生字段，再由 Unity Editor 重序列化。尤其要搜索所有继承 `MonoViewBase` / `MonoModelBase` /
   `MonoSystemBase` / `MonoUtilityBase` / `MonoGameContextBase` 的业务类型：旧版本里标了 `[OdinSerialize]` 的
   Dictionary、接口或多态字段，不会因为项目仍安装 Odin 就继续随新的 `MonoBehaviour` 基类持久化。先导出旧数据，
   再按语义改为 Unity `[SerializeReference]`，或拆到业务自有的 Odin Component / ScriptableObject 数据宿主，
   重填并验证后才能删除旧字段。
3. 先删除 `Game.Framework.Odin.Editor` Module（未来独立 Package 时先卸载该 Package），确认原生 fallback
   Inspector 接管；再从项目中物理移除 Odin 插件。仅取消 define 或禁用 Inspector 不等于解除授权/分发依赖。
4. 逐个实际发布的 BuildTargetGroup 清理/核对 `ODIN_INSPECTOR*` scripting define，再刷新编译。插件已经
   删除但 define 残留时，业务侧 `#if ODIN_INSPECTOR` 内的 Sirenix 引用仍会被启用并导致编译失败；define 只是
   环境探测结果，不能替代物理安装与授权边界。
5. 运行 EditMode、PlayMode 和 `SSFramework/诊断与分析/模块与依赖`。Framework 的
   `FrameworkOptionalDependencyTests` 会从 CompilationPipeline 找到 Assets/Packages 中的 Framework 程序集，
   同时检查源码、asmdef 与已编译 DLL 的直接引用，并拒绝扫描数为零的假绿。审计窗口的“第三方依赖证据目录”
   会保留 Odin 随插件分发的 Editor / NoEditor / NoEmitAndNoEditor 同 AssemblyName 物理变体，并用完整
   BuildTarget 兼容集合验证它们确实互斥；展开后确认当前 DLL / asmdef 消费者只剩准备一并删除的 Odin
   Adapter、测试或项目侧工具。若显示项目 Runtime 消费者或 Unknown，先迁移/修复证据，不能把“定位到 DLL”
   当成可安全删除。
6. 在一个未安装 Odin 的干净工程安装未来的 Framework UPM 包并编译；这是发布前最终删除测试。

Odin 的具体授权以[官方价格页](https://odininspector.com/pricing)和
[EULA](https://odininspector.com/eula)为准。尤其不要把已购买项目中的 Odin DLL 复制进对外分发的 Framework
包，让下游“顺带获得”插件。

## 什么时候值得创建 Odin Adapter

至少满足以下条件再立项：有两个以上 Framework 工具需要同一套 Odin 增强；增强明显优于原生基线；删除
Adapter 后所有配置仍可编辑、运行时行为不变；能用测试证明 Adapter 没有反向污染 Core。典型候选是跨多个
Framework 配置类型复用的 Attribute Processor、复杂表格可视化或 Validator 规则，而不是给现有字段换颜色、
换标题。

当前 Adapter 已解决一个明确共存问题：Odin 会为具体业务类型注册精确 Editor，单靠 Framework 基类上的
`CustomEditor` 优先级无法稳定组合两者。Adapter 在每次 Domain Reload 后调用 Odin 官方
[`CustomEditorUtility`](https://odininspector.com/documentation/sirenix.odininspector.editor.customeditorutility)，
把“当前由 Framework 原生 fallback 接管、且 Odin 全局/逐类型设置允许绘制”的具体
Framework 组件临时映射到 `FrameworkOdinInspector`。它先追加 Context/服务诊断，再调用完整 Odin 绘制，
因此属性、分组和按钮仍可用。

这份映射只存在于 Editor 内存，不改写 `InspectorConfig` 或其它 Odin 资产；删除 Adapter 后下一次 Domain Reload
自然恢复原生 fallback。普通静态业务 Editor 不会被覆盖，Odin 的逐类型排除以及 User/Plugin/Unity/Other Types
分类开关会按其官方 `AssemblyUtilities` 结果执行。配置资产保存或重新导入后 Adapter 会延迟重应用；其它工具若也在
运行期动态改写同一类型的 CustomEditor 表，双方仍是最后写入者生效，应由项目明确选择其一，而不是假装可自动合并。
若 Odin 总开关或类型分类从启用改为禁用，重应用会只撤回本 Adapter 当前持有的映射并立即归还原生 Inspector，
不必等待 Domain Reload。
初始化字段仍由字段级 Drawer 在 PlayMode 禁改；遵循 Unity 默认 Header 流程的其它业务 Editor 可通过
`finishedDefaultHeaderGUI` 获得诊断入口。Fonts 等可选 Module 不依赖该回调是否被 Odin 触发，而是把专属诊断
注册到原生与 Odin Inspector 共用的 contributor 接缝。测试使用真实 `AssetSystemConfigModel` 创建 Editor 并断言 Adapter 所有权，
不只依赖文档推测。未来的 Attribute Processor、Validator 规则或专用数据宿主仍需独立价值与测试，不能顺手
堆进 Adapter。

完整架构取舍与迁移证据见 [ADR-0015](adr/0015-odin-decoupling-assessment.md)。
