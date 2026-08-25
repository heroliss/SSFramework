# ADR-0006：第一阶段接受 Odin 硬依赖

**Status:** Superseded by [ADR-0015](0015-odin-decoupling-assessment.md)

## Context

早期 `MonoViewBase`、`MonoLayerBase<TLayer>` 与 `MonoGameContextBase` 继承 Odin 的
`SerializedMonoBehaviour`，用 `[OdinSerialize]` 保存接口类型的 Context 引用，并用 Odin Attribute
显示运行时诊断。项目当时已经采购 Odin，先用成熟插件快速建立可观察的 Inspector 体验是合理的阶段性选择。

## Decision

第一阶段接受 Odin 硬依赖，不为尚未开始的跨项目分发提前维护第二套 Inspector。

## Superseded reason

UPM 抽包与轻量组合已经进入实际实施阶段，付费插件硬依赖开始直接阻塞分发、授权和删除测试。
[ADR-0015](0015-odin-decoupling-assessment.md) 已落地 Unity 原生基线，并把 Odin 调整为项目级可选专业增强。
本 ADR 只保留为历史背景，不再代表当前架构。
