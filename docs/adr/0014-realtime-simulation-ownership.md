# ADR-0014：实时仿真 / 逐帧逻辑的归属与编排

**Status:** Accepted

## Context

MVCS 的写链 `View → Command → System → Model` 天然适合**离散的、用户/网络意图驱动**的状态变更（背包、商店、对话、回合制操作）。但游戏还有大量**连续的、逐帧推进的仿真**：AI tick、移动/物理、技能结算、寻路、计时器、状态机轮询。

此前文档对这一半着墨偏薄，留下歧义：逐帧逻辑该住在哪？谁驱动每帧？要不要也包成 Command？若误把逐帧逻辑包成 Command 每帧 `Execute`，会有两个坏处：

1. 每帧产生无谓的命令分发（即便 struct 零分配，单泛型查询重载仍可能装箱，见 AGENTS §5）；
2. 命令流被仿真噪音淹没，削弱可插拔 `CommandSystem` 的日志/回放/撤销价值（那套机制假设"命令 = 离散意图"）。

## Decision

**Command 只承载离散意图，不用于逐帧仿真。逐帧仿真逻辑归 System，用既有原语驱动，不新增框架 API。**

- **Command 的边界**：一次用户/网络/事件触发的行为。逐帧的东西不要走 Command。
- **逐帧仿真住在 System**，两条路径任选：
  - **Mono 路径**：`MonoSystemBase` 子类直接写 `Update` / `FixedUpdate` / `LateUpdate`（它本就是 `MonoBehaviour`）。需要 Inspector 配置或 Hierarchy 可见时用。
  - **纯 C# 路径**：System 在初始化时用 R3 `Observable.EveryUpdate()`（或 `Observable.Interval` / `Observable.Timer`）订阅进自己的 `Bag`，宿主/Context 释放时自动退订。无需 MonoBehaviour 也能逐帧。
- **写入仍走正规通道**：逐帧逻辑里 System 直接改 Model（它是 Model 的合法写入者），需要广播时 `SendEvent`。逐帧**不要**绕 Command（System 本就无 `ICanSendCommand`）。View 仍只订阅、不参与仿真。
- **tick 顺序编排**：同类 System 之间若有 tick 先后依赖，用 `[DefaultExecutionOrder]`（Mono）或在一个"编排 System"里显式按序调用子步骤（纯 C#），**不要**依赖容器注册顺序。
- **不引入专门的 `ITickable` / `UpdateManager`**：`MonoBehaviour.Update` + `Observable.EveryUpdate()` 已覆盖常见需求；集中调度器属过度设计（见 memory「no-over-engineering」）。仅当出现明确的"集中控制 tick 频率 / 全局暂停 / 时间缩放 / 固定步长仿真"需求时，再补 ADR 引入。

## Consequences

- ✅ 明确了游戏"仿真那一半"的归属，消除"逐帧逻辑该不该走 Command"的歧义；框架不再只对 UI/元状态半边强、对实时半边失语。
- ✅ 两条路径（`MonoSystemBase.Update` / R3 `EveryUpdate`）都复用既有原语，**零新增框架 API**。
- ✅ Command 流保持"离散意图"语义，回放 / 日志 / 撤销装饰器不被逐帧噪音淹没。
- ⚠️ R3 `EveryUpdate` 等逐帧订阅**务必登记进 `Bag`**（随宿主 / Context 释放），否则泄漏每帧回调。
- ⚠️ 逐帧高频路径注意分配：`SendEvent` 用 `record struct` 事件、避免每帧 LINQ/闭包；需要时用对象池（ADR-0007）。
- 🔮 固定步长 / 时间缩放 / 全局暂停若成为需求，引入轻量 tick 调度（仍藏在 System 后），届时补 ADR。
- 关联：[0001](0001-five-layers-and-permission-interfaces.md)（五层与权限）、[0005](0005-no-runtime-hot-swap-of-layers.md)（层不可热替换）。
