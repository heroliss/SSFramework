# ADR-0003：自研精简 DI 容器 + 主线程独占契约

**Status:** Accepted

## Context

框架需要依赖解析与运行时注册，但完整 DI 框架（如 Zenject/VContainer）功能多、概念重、与本框架的"层标记 + Hierarchy 即依赖"模型不完全契合。游戏热路径（Awake/Command/Event）对分配和锁敏感。

## Decision

自研精简 `Container`：
- 三段解析顺序：运行时覆盖层 `_overrides`（Mono 层 Awake / `RegisterXxx` 写入）→ 构建时绑定 `_bindings`（`InstallBindings`，工厂首次 Resolve 懒构造并缓存）→ 父级容器递归 → `GameContext.Main` 全局回退。
- 按**精确类型键**查找，不做继承扫描。Mono 层自动注册"具体类型 + 派生接口（不含层标记本身）"。
- 构建期字典的 value 是内部 `ContainerBinding`，显式区分现成值与 Factory；绑定自身管理 Singleton 缓存与诊断状态，不用 `object is Func<...>` 猜类型。构造时机与生命周期所有权的四种组合见 ADR-0035。
- **主线程独占**：所有解析/注册不加锁；Editor/Development Build 下 `AssertMainThread` 兜底报错。
- `Container` 对业务**不可见**（`internal`）：业务只能走 `RegisterModel/System/Utility` 受控通道，保证注册一定带层标记。框架内部经 `ContextInternals.GetContainer` 访问。
- 运行时分层注册以“具体类型 + 全部派生层 Interface”为一个提交单元：先检查所有精确键是否可写，再统一进入覆盖层。任一活实例冲突都会在写入前失败；已销毁的 Unity 对象仍允许整组替换，不留下半注册 contract。
- Builder、运行时 Register 与 Mono 自动挂接共用“实例恰好属于一个 Model / System / Utility 层”的校验；多层类型在任何 Container 写入前失败，不能靠换一个注册入口绕过权限模型。
- Context 初始化采用**提交式事务**：`InstallBindings → Build → GameContext 值注入/Attach → OnInitialized` 全部成功后，`MonoGameContextBase` 才发布 Ready。任一步失败都会释放 Builder/Container 已接管的 owned 资源、保留根异常并进入 Failed；后续调用得到带 inner exception 的明确 `InvalidOperationException`，不会继续在半初始化对象上制造 NRE。父 Context 递归初始化若形成环，也在 `Initializing` 状态边界 fail-fast。
- 值绑定进入 `GameContext` 时，先整批预检所有 `IHasGameContext` 实例的 **Context 归属（Context Affinity）**，再整批 `[Inject]`，全部成功后才整批 Attach。公开 `Inject`、运行时 `RegisterModel/System/Utility` 与 Mono 自动挂接也在字段或 Container 写入前执行同一预检。未绑定可继续装配、同一底层 Context 幂等、已属于其它 Context 则 fail-fast；真正无 Context 能力的共享值不受此限制。用户 `[Inject]` 方法 / setter 的任意外部副作用不可通用回滚，不属于该事务承诺。
- Mono 层自动挂接采用“注册计划预检 → provisional Context → Inject / AssetReference 绑定 → 存活与冲突复检 → 一次性发布注册”。因此初始化回调可使用自身合法的 Context 能力，但其他对象在完成前解析不到半初始化层；失败与 OnDestroy 共用幂等清理，已进入 Bag 的引用和 Container 注册都会撤销。Mono View 不注册自己，但沿用同一 provisional Context 与 Bag 回滚边界。

## Consequences

- ✅ 热路径零锁零额外分配；解析逻辑可审计、可在脑内推演。
- ✅ Hierarchy 父子 = Context 父子，子级可覆盖父级注册，天然作用域隔离。
- ✅ Context 对调用方只有“完整可用 / 明确失败”两种状态；构造期副作用与资源不会因异常悬空。
- ✅ 同一个持有 Context 的实例不会同时出现“注入依赖来自 B、扩展方法仍解析 A”的双重真源。
- ⚠️ 不支持跨线程访问（业务需 `await UniTask.SwitchToMainThread()` 后再发 Command）。
- ⚠️ 精确类型键意味着"注册 `IFoo` 不能用 `Foo` 解析"，Mono 路径自动两者都注册、手动 `InstallBindings` 需自己补。
- ⚠️ **依赖图绑定到场景图是双刃**：「拖动 GameObject = 改 Context 归属」便利，但场景组织还受渲染/裁剪/Prefab 工作流/团队分工牵引，可能与期望的 DI 作用域冲突；且这种改动**没有编译错误兜底**，`GameContext.Main` 全局回退反而会把"接错线"的层静默解析成功，把 wiring bug 推迟到运行期才暴露。
  - **何时改用显式 `_targetContext`（不依赖 Hierarchy 自动查找）**：多人协作、Context 边界与视觉/Prefab 边界不一致、或某层必须钉死在特定 Context（不随 Hierarchy 漂移）时——在 Inspector 显式拖入目标 Context。纯展示性、边界与视觉一致的嵌套才放心用自动向上查找。
  - **如何识别被 Main 掩盖的接线问题**：Editor 诊断把静态策略“`可→Main`”与实际成功证据“`→Main ×N`”分开；明细按契约显示最终来源和解析次数。`HasBinding`、失败探测与只读观察不记账，玩家包不含采集代码。实际 Main 回退不必然是 bug，但本应隔离的 Context 一旦出现该证据，应优先检查本地注册、显式父级和 Hierarchy。
