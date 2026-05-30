# ADR-0007：自研对象池替代第三方库

**Status:** Accepted（C# 对象池 + GameObject/Prefab 池均已落地并测试）

## Context

项目曾引用第三方对象池 uPools，但实际从未使用（已移除，仅在 `Game.Framework.asmdef` 留过悬空引用）。游戏需要对象池（GC 友好、prefab 实例复用），但第三方库与框架的生命周期模型（`IDisposable` / `DisposableBag` / `IAssetUtility`）融合度不高。

## Decision

自研对象池，**深度融入框架生命周期**：

- `IPoolUtility : IUtility`，经 `this.GetUtility<IPoolUtility>()` 取用。
- C# 对象池：`Rent<T>()` / `Return`，配工厂委托 + 重置钩子（`IPoolable.OnRent/OnReturn` 或委托）。
- GameObject/Prefab 池：按 prefab 键控，`Spawn` / `Despawn` / 分帧 `await Prewarm(n)`（每帧实例化一个，把开销摊到加载界面期间）；实例挂 `PooledObject` 标记组件（回指源池 + 缓存 `IPoolable` 组件），`Despawn(go)` 据此自动路由回源池、无需调用方再传 prefab；重置钩子复用 `IPoolable`（实例上**任意组件**实现即生效）。
- **刻意不耦合 `IAssetUtility`（修订初版设计）**：初版设想"GameObject 池与 `IAssetUtility` 协同、按 location 加载 prefab"，落地时改为 `PoolUtility` **不依赖 Context**——按 location 异步加载交由调用方先 `Bag.Load<GameObject>(loc)` 取到 prefab 再建池。理由：把"加载"与"池化"两个关注点解耦，`PoolUtility` 得以保持纯净、可被父子 Context 经父级回退共享，不被资源系统绑死。
- **Bag 集成（核心卖点）**：C# 对象用 `Bag.Rent<T>()`、GameObject 用 `Bag.Spawn(prefab, …)`，宿主销毁/`bag.Dispose` 时自动归还 / Despawn，镜像 `Bag.Load`（借通道、自动释放）的心智。**直接返回对象本身**，归还经内部 tracked `Disposable` 完成——未暴露初版设想的 `PooledHandle` 类型，调用方更省心。
- 主线程独占，与 `Container` 一致。GameObject 空闲实例统一停放在惰性创建的停用 + `DontDestroyOnLoad` parking 节点下（不渲染、不 Update、跨场景存活）。

## Consequences

- ✅ 池化对象的生命周期纳入统一的 `IDisposable`/`Bag` 模型，业务无感知归还。
- ✅ 无第三方依赖，可按框架需要演进。
- ⚠️ 需自行保证线程与重置正确性；C# 对象池 8 个 + GameObject 池 12 个测试覆盖 Rent/Return/Spawn/Despawn/Prewarm/钩子/容量/路由/Bag 自动归还（PlayMode 136/136 全绿）。
- 已落地文件：`Scripts/Pool/`（`IObjectPool<T>` 与 `IPoolUtility` 同置于 `IObjectPool.cs`、`ObjectPool<T>`、`IPoolable`、`PoolUtility`、`IGameObjectPool`、`GameObjectPool`、`PooledObject`）+ `DisposableBag.Rent<T>()` / `DisposableBag.Spawn(...)`。Editor/Dev 构建下重复归还 / 外来实例（C# 与 GameObject 两侧）均有检测。
- 注：新建 `IPoolUtility.cs` 时踩到 MCP 新脚本导入坑（文件未进编译列表），最终把接口并入 `IObjectPool.cs` 解决，见 `docs/unity-mcp-tips.md §9`。
