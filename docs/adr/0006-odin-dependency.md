# ADR-0006：Odin 硬依赖现状与未来解耦

**Status:** Accepted

## Context

`MonoViewBase` 与 `MonoLayerBase<TLayer>`（Model/System/Utility 基类）都继承 Odin 的 `SerializedMonoBehaviour`，靠 `[OdinSerialize]` 序列化接口类型字段 `IGameContext _targetContext`（Unity 原生序列化无法序列化接口引用）。Odin（Sirenix）是付费插件。

## Decision

**第一阶段接受 Odin 硬依赖。** 它直接解决"Inspector 拖拽/序列化接口类型的 Context 引用"这一核心需求，且项目已采购。

## Consequences

- ✅ `Target Context` 可在 Inspector 拖拽，支持 `MonoGameContextBase` 与运行时赋值的纯 C# `IGameContext`。
- ⚠️ 作为"面向广泛使用的框架"，硬依赖付费插件是复用障碍。
- 🔮 未来解耦方向（成本不低，非当前优先）：把 `SerializedMonoBehaviour` 降级为 `MonoBehaviour`。
  > **方向修正（见 [ADR-0015](0015-odin-decoupling-assessment.md)）**：原先设想的 `[SerializeReference]` 替代**不适用**于 `_targetContext` / `_parentContext`——这俩字段存的是 `MonoGameContextBase`（`UnityEngine.Object`），而 `[SerializeReference]` 只适用于纯托管对象。可行路径是把字段降为具体 `MonoGameContextBase` 类型（Unity 原生拖拽）+ 运行时 `IGameContext` 覆盖分离；真正的主成本是把 `[ShowInInspector]` 运行时诊断改写成自定义 Editor。详细改动面 / 风险 / 排期建议见 ADR-0015。
