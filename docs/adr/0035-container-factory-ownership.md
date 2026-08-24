# ADR-0035：Container 工厂所有权 —— 构造时机与生命周期正交，显式 RegisterOwnedFactory

**Status:** Accepted（2026-08-23）

## Context

容器原有两组注册语义没有覆盖一个常见组合：

- `RegisterOwned` 能让服务随 `GameContext.Dispose` 释放，但要求调用方先构造实例；
- `RegisterFactory` 能在 Lazy 首次解析或 Eager 构建时解析其他依赖再构造，却不拥有工厂产物。

Outpost 的本地化 adapter 需要先从 Container 解析异步注册的配置表服务，因此使用 Factory；但 `LocalizationUtility` 实现 `IDisposable`，普通 Factory 的产物不在 owned 列表中，根 Context 结束时不会释放。demo 与 guide 还把这条路径当推荐写法，说明问题不只是单点遗漏，而是公开 API 缺了一种生命周期表达。

## Decision

新增 `ContainerBuilder.RegisterOwnedFactory`，让“怎么构造”与“谁负责释放”成为两条正交轴：

| 注册 | 构造 | 所有权 |
|---|---|---|
| `RegisterValue` | 调用方已构造 | 外部 |
| `RegisterOwned` | 调用方已构造 | Context |
| `RegisterFactory` | Lazy / Eager 工厂 | 外部 |
| `RegisterOwnedFactory` | Lazy / Eager 工厂 | Context |

具体契约：

- 两类 Factory 都只构造一次并按多个 contract 共享同一 Singleton；返回 null、返回类型不符合 contract、循环解析都 fail-fast。
- OwnedFactory 要求产物实现 `IDisposable`；首次成功构造后由 **Container** 接管，按对象引用去重，Context Dispose 时逆序且最多释放一次。
- Factory 产物仍不自动 `[Inject]` / `AttachTo`。工厂参数 `Container` 是显式接线 Seam；所有权不能改变注入时机。
- Eager 构建中若后续工厂失败，临时 Container 立即释放此前已接管的 owned 产物，再把原异常抛出；失败的 Builder 已消费，不能重用。
- `ContainerBuilder` 本身实现 `IDisposable`：`RegisterOwned` 成功到 `Build` 提交前，Builder 是资源的**临时 owner**；`Build` 后同一 ownership registry 交给 Container。生产代码手工创建 Builder 时用 `using var`，这样 `InstallBindings` 或注册逻辑在 Build 前抛异常也会逆序回滚；Build 成功后 Builder.Dispose 为 no-op，不会提前释放已移交资源。
- `GameContext` 从接收 Container 起承担构造事务的最后一段：构建期值绑定的 Inject / Attach 或诊断初始化抛异常时，构造函数主动 Dispose Container 后重抛。调用方不会拿到需要自己猜测如何清理的半成品。
- Context / Container Dispose 后，解析、订阅、注入与动态注册 fail-fast，避免懒工厂在生命周期结束后“复活”服务。发送事件保留原有的幂等忽略语义，便于异步收尾。
- Dispose 的级联清理遵循异常隔离：取消回调或单个 owned 服务抛异常时记录错误并继续释放其余资源，避免局部失败放大成整棵 Context 泄漏。

未选择“普通 Factory 只要返回 `IDisposable` 就自动拥有”，因为 Factory 也可能返回外部共享实例；隐式接管会导致重复释放或提前释放。也未要求业务改为 Eager 手工构造，因为这会丢掉依赖顺序解耦与按需构造。

## Consequences

- Outpost 本地化与 Container demo 改用 OwnedFactory，真实业务切片和教学内容共同验证新 Seam。
- 普通 Factory 返回 `IDisposable` 仍合法，但调用方必须明确外部所有者；API 不猜测生命周期。
- Builder 与 Container 共享内部 `OwnedDisposables` Module：引用去重、逆序释放、异常隔离与幂等语义只有一个实现。Builder 管 Build 前回滚，Container 承接 Build 后产生的 Lazy 实例与 Context 生命周期；Build 后 Builder 不再参与运行期生命周期。
- 构建期绑定由内部 `ContainerBinding` 显式建模值 / 工厂，并集中管理 Singleton 缓存与诊断状态；不再拿 `object` 的运行时类型充当 tag，因此 `Func<Container, object>` 本身也可作为普通值注册，多 contract 的解析状态不会分叉。
- 注册边界与首次解析边界增加参数、类型和循环依赖校验，错误更早、更接近根因暴露。
- 新语义由容器契约测试覆盖：Lazy 单例、多 contract、引用去重、非 IDisposable、Build 前 Builder 回滚、所有权移交、Eager 失败清理、GameContext 构造失败回滚、Dispose 后禁止复活。
