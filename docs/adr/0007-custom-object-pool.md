# ADR-0007：自研对象池替代第三方库

**Status:** Accepted（C# 对象池 MVP 已落地并测试；Prefab/GameObject 池为后续）

## Context

项目曾引用第三方对象池 uPools，但实际从未使用（已移除，仅在 `Game.Framework.asmdef` 留过悬空引用）。游戏需要对象池（GC 友好、prefab 实例复用），但第三方库与框架的生命周期模型（`IDisposable` / `DisposableBag` / `IAssetUtility`）融合度不高。

## Decision

自研对象池，**深度融入框架生命周期**：

- `IPoolUtility : IUtility`，经 `this.GetUtility<IPoolUtility>()` 取用。
- C# 对象池：`Rent<T>()` / `Return`，配工厂委托 + 重置钩子（`IPoolable.OnRent/OnReturn` 或委托）。
- Prefab/GameObject 池与 `IAssetUtility` 协同：prefab 加载一次、实例复用；`await pool.Prewarm(n)` 异步预热。
- **Bag 集成（核心卖点）**：`Bag.Rent<T>(...)` 返回对象，宿主销毁/`bag.Dispose` 时自动归还，镜像 `Bag.Load`（借通道、自动释放）的心智；归还句柄 `PooledHandle : IDisposable`。
- 主线程独占，与 `Container` 一致。

## Consequences

- ✅ 池化对象的生命周期纳入统一的 `IDisposable`/`Bag` 模型，业务无感知归还。
- ✅ 无第三方依赖，可按框架需要演进。
- ⚠️ 需自行保证线程与重置正确性；已用 8 个测试覆盖 Rent/Return/Prewarm/钩子/容量/Bag 自动归还（PlayMode 124/124 全绿）。
- 已落地文件：`Scripts/Pool/`（`IObjectPool<T>` 与 `IPoolUtility` 同置于 `IObjectPool.cs`、`ObjectPool<T>`、`IPoolable`、`PoolUtility`）+ `DisposableBag.Rent<T>()`。Editor/Dev 构建下 ObjectPool 有重复归还/外来实例检测。
- Prefab/GameObject 池（接 `IAssetUtility` 异步预热）为后续工作，届时补充 API。
- 注：新建 `IPoolUtility.cs` 时踩到 MCP 新脚本导入坑（文件未进编译列表），最终把接口并入 `IObjectPool.cs` 解决，见 `docs/unity-mcp-tips.md §9`。
