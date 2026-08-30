# ADR-0007：自研对象池替代第三方库

**Status:** Accepted（C# 对象池 + GameObject/Prefab 池均已落地并测试）

## Context

项目曾引用但未实际使用第三方对象池 uPools。游戏仍需要复用纯 C# 对象和 prefab 实例以减少分配、`Instantiate` / `Destroy` 尖峰，但池不能只是一个缓存容器：它还必须与 `GameContext`、`IDisposable` / `DisposableBag` 的所有权模型一致，并在用户钩子抛异常、同步重入或宿主关闭时保持对象不会被同时交给两个 owner。

这里把一次成功的 `Rent` / `Spawn` 称为一个 **lease（租借所有权）**。lease 发布后实例归调用方独占，`Return` / `Despawn` 会终止这份所有权；已经归还的引用不得继续使用。

## Decision

自研对象池，并把租借正确性作为所有构建都必须保留的行为契约：

- `IPoolUtility : IUtility` 是统一入口，经 `this.GetUtility<IPoolUtility>()` 取用。C# 对象池提供 `Rent<T>()` / `Return`，GameObject 池按 prefab 键控并提供 `Spawn` / `Despawn`、分帧 `Prewarm` / `TrimAsync`。
- C# 实例按**引用身份**而不是 `Equals` / `GetHashCode` 跟踪；值相等但引用不同的对象仍是两份独立 lease。工厂返回 `null`、返回本池已经管理的同一引用，或把另一个池的活动实例再次发布，都会在发布前失败。
- 正常可复用路径在所有构建中经历 `Inactive → Renting → Active → Returning → Inactive` 状态机；失败路径会从对应事务态清理后退出，不再回到 idle。`Renting` / `Returning` 是同步事务态，用于拒绝 `OnRent` / `OnReturn` 内对同一实例的重入归还；只有完整走到 `Active` 的实例才会返回调用方并计入 `CountActive`。
- `Rent` / `Return` / `Spawn` / `Despawn` 都是事务化操作。租借钩子失败时，池会 best-effort 执行对应归还清理，丢弃或 Destroy 半初始化实例，再按原始堆栈重抛首异常；归还钩子失败时仍让其余清理钩子获得机会，关闭 lease 并丢弃脏实例，最后重抛首异常。诊断日志可以按构建裁剪，但防重入、拒绝外来或重复归还、禁止复用脏实例不能裁剪。
- `PoolUtility` 为每个活动 C# lease 按引用记录**真实来源池**。因此派生对象上转型后调用 `Return<Base>(instance)` 仍会回到创建它的派生类型池；外来或重复实例不会因为一次 `Return` 而创建错误类型的新池。
- `PoolUtility.Dispose` 采用两阶段关闭：先封闭新 `GetPool` / `Rent` / `Spawn` / 预热维护并清掉 idle 缓存；Dispose 前已经发布的 lease 仍可做一次 terminal `Return` / `Despawn`，执行清理后直接丢弃或 Destroy，不再复活池缓存或 `DontDestroyOnLoad` parking 节点。
- **Bag 集成**：`Bag.Rent<T>()` / `Bag.Spawn(prefab, …)` 直接返回实例，同时把归还动作登记进 bag；宿主销毁或 `bag.Dispose` 时自动归还，镜像 `Bag.Load` 的“借通道、自动释放”心智。若用户 `OnRent` 在租借过程中同步关闭 bag，晚到 lease 会先归还源池再抛 `ObjectDisposedException`，不会把一个已经回池的引用发布给调用方。
- GameObject 实例挂 `PooledObject` 标记，记录来源池、事务状态并缓存实例树上的 `IPoolable` 组件。空闲对象停放在停用的 `DontDestroyOnLoad` parking 节点；停放点不可用或池已终止时直接 Destroy，不把对象散落到场景根。`Spawn(parent: null)` 会在激活前显式把 clone 迁回当前激活 Scene，指定 parent 则以 parent 的 Scene 为归属；parking 只是空闲缓存容器，不改变活动实例的场景生命周期。
- 新 GameObject clone 先创建在 inactive-in-hierarchy 的 parking 下，强制停用并完成来源标记 / 钩子缓存；正式 `Spawn` 设置最终 parent / pose 后才首次激活。这样默认激活的 prefab 在预热时不会误跑 Awake/OnEnable，首次生命周期也能看到完整接线。
- `maxSize` 是提交时不变量，不只是进入操作时的快照。factory、parking provider 和 `SetParent` 都可能同步重入池，因此预热或归还在最终 push 前会用最新 idle 状态复检容量；竞争失败的未发布实例直接丢弃 / Destroy。
- Unity 的 `Destroy` 延迟到帧末生效，空闲栈可能短暂保留之后变成 fake-null 的引用。`Spawn` 取用时会逐个跳过栈顶死槽，读取空闲计数与容量判断、预热、收缩则会完整压缩 idle 栈；死槽不占可复用容量。活动实例被外部 Destroy 后无法再归还，`CountActive` 保留在借出侧作为泄漏线索。
- **刻意不耦合 `IAssetUtility`**：按 location 异步加载由调用方先 `Bag.Load<GameObject>(location)` 取得 prefab，再交给池。加载与复用保持正交，`PoolUtility` 不依赖 Context 或资源系统，可以经父级回退共享。
- 主线程独占，与 `Container` 一致。

## Consequences

- 池化对象进入统一的 Context / Bag 生命周期；常规业务可以只表达“借用”，无需维护另一套清理列表。
- 引用身份、事务态和来源路由增加了少量常驻 bookkeeping，但换来 Release 与开发构建一致的所有权正确性，以及可依赖的 `CountActive`。
- 钩子异常不再留下可复用的半初始化或未清干净实例；代价是失败实例宁可丢弃，也不继续命中缓存。
- `Dispose` 不会让仍在调用方手中的旧 lease 失去清理出口，同时已关闭的 Context 也不会被一次晚到归还意外复活。
- GameObject 的 fake-null 清理需要在读取空闲数和容量相关操作前扫描 idle 栈；池仍是主线程基础设施，不能作为并发容器使用。
- 自动化测试持续覆盖引用身份、工厂错误、钩子异常与重入、来源路由、Bag 晚到租借、两阶段关闭、GameObject fake-null、容量和预热/收缩等契约；文档不绑定易腐的用例总数。
