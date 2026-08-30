# ADR-0005：运行时不热替换已注册层

**Status:** Accepted

## Context

是否支持"运行时删除/替换已注册的 Model/System/Utility，让既有引用自动指向新实例"？

## Decision

**不支持。** 默认假设"注册到 Container 的层在 Context 生命周期内不变"。

原因：`[Inject]` 字段是 Awake/Execute 时的一次性快照、R3 订阅绑定到具体 `ReactiveProperty` 实例——容器反注册不会重定向它们；而 `ctx.GetXxx<T>()` 走容器实时解析，会按回退顺序找父级或抛异常。两条路径混用时，观察值与写入目标会分裂。

## Consequences

- 切换数据：**改 Model 内部状态**（重置字段、清空集合），不要 Destroy 整个 Model GameObject。
- 替换整层实例：**整个 Context 一并 Dispose 重建**（场景切换、关卡重置）。
- `IHasGameContext` 实例同样不能跨 Context 搬迁或共享：每个 Context 创建独立实例；确实无状态、需要共享的值不要持有 Context。
- 想做声明式热替换需要"绑定 + Container 注册事件"机制，超出当前范围；如未来引入再补 ADR。
- 详见 [`Assets/Game/AGENTS.md`「Mono 生命周期与 Context」](../../Assets/Game/AGENTS.md#mono-生命周期与-context)。
