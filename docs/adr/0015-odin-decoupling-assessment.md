# ADR-0015：Odin 解耦的可行路径与改动面评估

**Status:** Proposed（精化 [0006](0006-odin-dependency.md) 的「未来解耦方向」，不推翻其「第一阶段接受 Odin」的决策）

> **优先级决定（最新）：保留 Odin，本解耦降为「长远可选、最低优先级」。** 仅当 [ADR-0010](0010-framework-reusability-upm.md)「UPM 抽包 / 对外复用」真正提上日程时才考虑落地。本 ADR 的可行性评估**保留备查**——下文「现在做最便宜」的分析在技术上仍成立（越早越省），但**是否做取决于 reuse 目标是否成真**，当前不做。

## Context

[ADR-0006](0006-odin-dependency.md) 接受了第一阶段对 Odin 的硬依赖，并提了一个未来方向：「用 `[SerializeReference]` 替代 `[OdinSerialize]` 的接口字段，把 `SerializedMonoBehaviour` 降级为 `MonoBehaviour`」。

但 Odin 是框架里**唯一没被隔离、却承重**的第三方依赖（YooAsset 等都关在接口后，Odin 却在每个 Mono 基类的继承链里），且它是 [ADR-0010](0010-framework-reusability-upm.md) 「UPM 抽包、广泛复用」愿景的**前置闸门**——复用方被迫购买 Odin。本 ADR 先把"到底怎么解耦、改动面多大、风险在哪"评估清楚——结论见顶部优先级决定：**保留 Odin、解耦降为长远可选、最低优先级**。

评估发现：**ADR-0006 提的 `[SerializeReference]` 方案对关键字段不成立**，需要修正方向。

## Decision（评估结论）

### 1. 修正方向：`[SerializeReference]` 不适用于 `_targetContext` / `_parentContext`

这两个字段类型是 `IGameContext`，但 Inspector 里拖进去的值是 `MonoGameContextBase`——一个 `UnityEngine.Object`（MonoBehaviour）。而 **`[SerializeReference]` 只适用于纯托管对象（plain C# class），不能用于 `UnityEngine.Object` 引用**（Unity 对 UnityEngine.Object 用 instance-id 引用序列化，不走 managed-reference）。所以「`[OdinSerialize] IGameContext` → `[SerializeReference] IGameContext`」会失败/语义错乱。Odin 之所以能撑住，是它把接口字段背后的 UnityEngine.Object 引用收进自己的序列化侧信道。

### 2. 真正可行的原生路径：字段降为具体类型 + 运行时覆盖分离

把"可序列化拖拽"与"运行时纯 C# 赋值"两个职责拆开：

```csharp
// 序列化部分：具体 UnityEngine.Object 字段，Unity 原生可拖拽，无需 Odin
[SerializeField] private MonoGameContextBase _targetContextObject;

// 运行时部分：纯 C# GameContext 由代码赋值（本就不能拖拽——它不是 UnityEngine.Object）
[NonSerialized] private IGameContext _runtimeContext;

// 解析顺序：运行时覆盖 → Inspector 拖入 → GetComponentInParent → Main
```

功能上**无损**：Inspector 本就只能拖 `MonoGameContextBase`（拖不了纯 C# `GameContext`），纯 C# 路径一直是代码赋值。`MonoGameContextBase._parentContext` 同理处理。基类即可从 `SerializedMonoBehaviour` 降为 `MonoBehaviour`。

### 3. 真正的成本不在序列化，在 `[ShowInInspector]` 运行时诊断

去掉 `SerializedMonoBehaviour` 会同时失去 Odin 的 `[ShowInInspector] / [FoldoutGroup] / [ReadOnly]`——而框架用它显示了一批**仅运行时、无 backing field** 的只读诊断：

| 位置 | 诊断项 |
|---|---|
| `MonoLayerBase` | 解析到的 Context、注册契约 |
| `MonoGameContextBase` | 解析到的父级、本地注册 |
| `AssetUtility` | 运行模式、默认包、各包初始化状态、模拟断网开关 |
| `MonoPoolUtility` | 当前各池概要 |

这些是排查 DI 注册 / 初始化失败的重要抓手。去 Odin 后要保留它们，须为这几个类写**自定义 `UnityEditor.Editor`**（放 `Game.Framework.Editor`，在 `OnInspectorGUI` 里 Play 模式下画这些只读值）。这是机械但实打实的工作量，也是本次解耦的**主要成本**，不是 `[OdinSerialize]` 那几个字段。

### 4. 数据迁移风险

基类从 `SerializedMonoBehaviour` 改为 `MonoBehaviour`、字段从 `[OdinSerialize] IGameContext` 改为 `[SerializeField] MonoGameContextBase`，**序列化格式变了**：既有场景 / prefab 里用 Odin 存的 `_targetContext` / `_parentContext` 赋值会丢，需要重新拖。实际影响有限（绝大多数 `_targetContext` 留空走自动查找），但**非零**——迁移须在真实场景上验证，不能盲改。

### 5. 研究补充：另两处成本 + 最佳时机

- **`[ValueDropdown]` 也要替代**：`AssetSystemConfigModel._defaultPackageName` 与 `AssetPackageConfig._name` 用 Odin 的 `[ValueDropdown]`（从方法取候选项的下拉）——Odin 编辑器专属功能。去 Odin 后退化成普通文本框，要保留下拉须写自定义 `PropertyDrawer`（候选来自现有的 `EditorPackageNames()` / `EditorBuildPackageNamesProvider` 钩子）。
- **业务继承的连带影响（最广、最易被忽略）**：`SerializedMonoBehaviour` 由所有业务 Mono 层经 `MonoXxxBase` **继承**——业务 Model/System/View 现在「免费」拿到 Odin 序列化（字典 / 接口 / 多态字段）。把基类降为 `MonoBehaviour` 会**移除这项能力**：任何依赖 Odin 序列化自有字段的业务代码都会失效，须改用 Unity 可序列化字段或 `[SerializeReference]`。这不是「工作量」，是一次**能力收窄**，要在解耦文档里讲清并约束业务「只用 Unity 可序列化字段」。
- **若将来要做，「现在」成本最低（但当前决定不做）**：当前仓库只有框架 + demo，demo 字段全是 Unity 可序列化（`RP<T>` / `List` / 基元），业务侧连带影响为零；每新增一个用 Odin 特性的业务类，迁移成本涨一分。此点记于此，仅为将来若重启此事时知道「越早越省」——按当前优先级决定（保留 Odin、最低优先级），暂不做。

## Consequences

- ✅ 厘清了「Odin 解耦」的真实形状：**序列化字段改造（小、可行）+ 诊断改自定义 Editor（中、是主成本）+ 场景数据迁移（小但需验证）**，而非 ADR-0006 设想的「换个 attribute」。
- ✅ 给出功能无损的原生替代（具体类型字段 + 运行时覆盖分离），不牺牲拖拽体验与纯 C# 路径。
- ⚠️ `[SerializeReference]` 不是本场景的解，ADR-0006 的该句方向应以本 ADR 为准。
- ⚠️ 解耦后短期内 Inspector 诊断体验会回退，除非同步补自定义 Editor——这是排期时要一并算进去的工作量。
- 🔮 **优先级：长远可选、最低**。保留 Odin（[ADR-0006](0006-odin-dependency.md) 不变）。本解耦是 [ADR-0010](0010-framework-reusability-upm.md)「UPM 抽包 / 对外复用」的前置——**仅当那个愿景真正提上日程才考虑**。届时本 ADR 转 Accepted、按上面三步落地并补迁移记录。在那之前不投入。
- 关联：[0006](0006-odin-dependency.md)（被本 ADR 精化）、[0010](0010-framework-reusability-upm.md)（UPM 愿景，本解耦是其前置）、[0004](0004-assembly-structure-and-rp-location.md)（Editor 程序集边界）。
