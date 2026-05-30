# ADR-0006：Odin 硬依赖现状与未来解耦

**Status:** Accepted

## Context

`MonoViewBase` 与 `MonoLayerBase<TLayer>`（Model/System/Utility 基类）都继承 Odin 的 `SerializedMonoBehaviour`，靠 `[OdinSerialize]` 序列化接口类型字段 `IGameContext _targetContext`（Unity 原生序列化无法序列化接口引用）。Odin（Sirenix）是付费插件。

## Decision

**第一阶段接受 Odin 硬依赖。** 它直接解决"Inspector 拖拽/序列化接口类型的 Context 引用"这一核心需求，且项目已采购。

## Consequences

- ✅ `Target Context` 可在 Inspector 拖拽，支持 `MonoGameContextBase` 与运行时赋值的纯 C# `IGameContext`。
- ⚠️ 作为"面向广泛使用的框架"，硬依赖付费插件是复用障碍。
- 🔮 未来解耦方向（成本不低，非当前优先）：用 `[SerializeReference]`（Unity 2019.3+ 原生支持序列化接口/抽象引用）替代 `[OdinSerialize]` 的接口字段，把 `SerializedMonoBehaviour` 降级为 `MonoBehaviour`。届时需评估对现有 Inspector 体验与既有序列化数据的影响，并补充 ADR。
