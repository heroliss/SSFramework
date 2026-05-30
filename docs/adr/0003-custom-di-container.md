# ADR-0003：自研精简 DI 容器 + 主线程独占契约

**Status:** Accepted

## Context

框架需要依赖解析与运行时注册，但完整 DI 框架（如 Zenject/VContainer）功能多、概念重、与本框架的"层标记 + Hierarchy 即依赖"模型不完全契合。游戏热路径（Awake/Command/Event）对分配和锁敏感。

## Decision

自研精简 `Container`：
- 三段解析顺序：运行时覆盖层 `_overrides`（Mono 层 Awake / `RegisterXxx` 写入）→ 构建时绑定 `_bindings`（`InstallBindings`，工厂首次 Resolve 懒构造并缓存）→ 父级容器递归 → `GameContext.Main` 全局回退。
- 按**精确类型键**查找，不做继承扫描。Mono 层自动注册"具体类型 + 派生接口（不含层标记本身）"。
- **主线程独占**：所有解析/注册不加锁；Editor/Development Build 下 `AssertMainThread` 兜底报错。
- `Container` 对业务**不可见**（`internal`）：业务只能走 `RegisterModel/System/Utility` 受控通道，保证注册一定带层标记。框架内部经 `ContextInternals.GetContainer` 访问。

## Consequences

- ✅ 热路径零锁零额外分配；解析逻辑可审计、可在脑内推演。
- ✅ Hierarchy 父子 = Context 父子，子级可覆盖父级注册，天然作用域隔离。
- ⚠️ 不支持跨线程访问（业务需 `await UniTask.SwitchToMainThread()` 后再发 Command）。
- ⚠️ 精确类型键意味着"注册 `IFoo` 不能用 `Foo` 解析"，Mono 路径自动两者都注册、手动 `InstallBindings` 需自己补。
