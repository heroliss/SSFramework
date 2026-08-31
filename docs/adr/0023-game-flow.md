# ADR-0023：游戏流程状态机 —— IGameFlow：显式 Flow + 每状态一个子 Context

**Status:** Accepted（2026-07-04；2026-08-30 分层修订）

## Context

roadmap 中期新模块第三项：游戏流程状态机——启动→登录→大厅→战斗的**显式 Flow**，圈定的核心思路是「每个状态一个子 Context，天然利用作用域树的整棵撤语义」。

要解决的真问题：没有显式流程时，"现在游戏在哪个阶段、这个阶段占用的资源/订阅/服务什么时候撤"散落在各场景脚本里——切阶段漏清理是最常见的泄漏来源，而框架已有的 `GameContext` 作用域树 + `DisposableBag` 恰好就是为「整棵撤」造的，缺的只是一个把"阶段"显式化并驱动子 Context 建/撤的编排者。

既有约束与先例：

- **GameContext 可嵌套可释放**：`ContainerBuilder.SetParent` + `GameContext.Dispose`（连带 `RegisterOwned` 实例与全部事件 Subject）已是现成原语——状态机不发明新作用域机制，只是编排既有机制。
- **注册即注入（ADR-0019）**：`RegisterOwned` 的纯 C# 实例在 GameContext 构造时统一 `Inject` + `AttachTo`（`IHasGameContext` 字段自动回填）——状态机作为纯 C# 服务注册即可拿到宿主 Context，无鸡生蛋问题。
- **v1 曾借 Utility 获得全层可达性**：最初认为 View（登录按钮）与 System（战斗结束）都要直接触发流程，因此让 `IGameFlow : IUtility`。后续五层边界收紧后，真实项目的 View 已统一经 Command 表达流转意图；继续保留 Utility 只会让带写能力的 `GoTo` 对所有 View 和 Utility 开放。
- **语义比可达性更重要**：GameFlow 拥有业务阶段转换、最新意图排队、取消与子 Context 生命周期，是“如何切换宏观阶段”的业务编排；它不是不黏业务的基础设施，也不是只保存数据的 Model。
- **不过度设计**：罕见需求用现有原语组合，不加专门 API。

## Decision

### 1. API 形态：实例式 GoTo + 状态基类

```csharp
public interface IGameFlow : ISystem
{
    FlowState Current { get; }              // 当前状态；转换中/未启动为 null 或旧状态（见 §4）
    bool IsTransitioning { get; }
    UniTask GoTo(FlowState next);           // 进入新状态实例；转换中再调 = 最新意图胜（见 §4）
    bool IsIn<TState>() where TState : FlowState;
}

public abstract class FlowState
{
    protected IGameContext Context { get; } // 本状态的子 Context（进入前由 flow 构建）
    protected DisposableBag Bag { get; }    // 随状态退出统一释放（订阅/资源/池/句柄）

    public virtual void InstallBindings(ContainerBuilder builder) { }      // 状态私有服务
    protected internal virtual UniTask OnEnter(CancellationToken ct) => UniTask.CompletedTask;
    protected internal virtual UniTask OnExit() => UniTask.CompletedTask;
}
```

- **状态是一次性实例，不是注册的单例**：`flow.GoTo(new BattleState(levelId))`——传参走构造函数（类型安全、零泛型体操），每次进入全新对象（无残留字段脏状态），"重进同类状态"（下一关）天然支持。对照方案「类型注册 + `GoTo<T>()`」需要额外解决传参（双泛型或 object 装箱）与实例复用脏状态两个问题，放弃。
- **不做转换表/守卫**：任意 `GoTo` 合法。哪些转换允许是业务 if 的事（登录未完成不给进大厅=按钮不可点/Command 里查状态），框架不做规则引擎。
- **分层选择 System，不拆 Model**：Flow 的深度来自“状态 + 转换 + 子作用域所有权”位于同一 Implementation；把 `Current` 单独搬进 Model 会新增同步接缝，却不减少转换复杂度，反而降低 Locality。View 经 Command 发起转换，需要持续展示时由查询 Command 返回只读投影；Command、System 与 FlowState 内部经 `GetSystem<IGameFlow>()` 访问。

### 2. 每状态一个子 Context：整棵撤

进入状态时 flow 用宿主 Context 做父级构建子 Context（`SetParent` + 状态的 `InstallBindings`），退出时整个 Dispose：

- 状态私有的 Model/System/Service 注册在子 Context（`RegisterOwned` 随撤释放），阶段结束不残留；
- 状态期间的订阅/资源/句柄全进状态 `Bag`（子 Context 的 bag），退出统一放掉——**切阶段漏清理**这一最大泄漏源被结构性消灭；
- 子 Context 解析未命中自动回退父链→全局：状态内代码照常 `GetUtility<IAudioUtility>()` 等取全局服务。

### 3. 载体与注册：纯 C# 进内核 `Core/Flow/`，契约属于 System

- `GameFlow`（纯 C#，`IDisposable`，`IHasGameContext`）：零 Unity 对象依赖（异步只用 UniTask），进内核。`IGameFlow : ISystem` 决定编译期访问权限；用层感知的 `builder.RegisterOwnedSystem(new GameFlow())` 自动登记具体类型与 Interface，并同时表达 Context 所有权，ADR-0019 注入语义自动回填宿主 Context。宿主 Context Dispose → flow Dispose → 当前 / 进入中 / 退出中状态的子 Context 撤除（不会为了销毁补调尚未开始的 `OnExit`）。低层 `RegisterOwned(value, contracts)` 仍可用于刻意限制解析契约的高级接线。
- **不做第二份 Mono 流程 Implementation**：flow 没有 Inspector 可配项（状态是代码 new 的），也不需要 Unity 逐帧回调。全局性由它注册在持久根 Context 决定；`DontDestroyOnLoad` 只负责根 Mono Context 的 Unity 载体。只为 Inspector 再实现一套会制造双注册方式与双状态真源；框架诊断窗口直接读取默认 Implementation 的 Editor-only Current / 进入中 / 退出中 / 待处理快照，并用 Context 树展示状态子 Context。若未来出现真实 Inspector 配置或 Unity 生命周期需求，再增加委托同一流程内核的 Mono Adapter，而不是复制状态机。
- **子流程 = 组合**：战斗内的阶段机（准备→作战→结算）就是在 `BattleState.InstallBindings` 里再注册一个 `GameFlow`——作用域树天然嵌套，不做 HSM（分层状态机）专门支持。

### 4. 转换语义：串行化 + 最新意图胜

- 转换全程串行：`OnExit(旧) → Dispose(旧子 Context) → Build(新子 Context) → OnEnter(新)`。
- **转换进行中再 GoTo**：取消在途 `OnEnter` 的 ct、等它结束，直接 Dispose 半进入状态的子 Context（**不调它的 OnExit**——`OnExit` 只在 `OnEnter` 成功完成后才有资格被调；半进入状态的清理靠 Bag/子 Context 整棵撤，这正是 Bag 的本职）。排队槽只有一格、新请求顶替旧排队（最新意图胜：长加载中收到"掉线回登录"，登录赢，不排队）。被顶替/取消的 `GoTo` 返回的 UniTask 以取消结束。
- **同类状态 GoTo**：正常退旧进新（重开一局是刻意行为，不做幂等——与 PlayMusic 的同 clip no-op 语义相反，各自合理）。
- 转换完成后向宿主 Context `SendEvent(new FlowChangedEvent(from, to))`——loading 界面/埋点订阅这一个事件即可，不用侵入每个状态。事件链只记录**完整进入成功**的状态：连续转换 `A →（B 被顶替/失败）→ C` 只发布 `A → C`，不会把从未成为 `Current` 的 B 写进历史，也不会因 A 已先退出而误报 `null → C`。若一次失败结束后流程已稳定处于无状态，之后另起的转换才从 `null` 开始。
- **宿主在 `OnExit` 期间释放**：当前 GoTo 与正在退出的状态不是异步循环里的临时局部量，而是 flow 显式持有的 active transition。Dispose 立即让 GoTo 以取消终止并撤掉退出状态的子 Context；`IsTransitioning` 对外立即为 false，也不会进入下一状态或发布迟到事件。`OnExit` 刻意没有 token，框架不能强杀已经开始的业务任务，因此内部物理 owner 继续观察它到终态；迟到异常仍进入 `Log` Seam，但不再触碰 flow 状态。
- **所有用户边界都允许同步重入**：结束旧排队 task、`InstallBindings`、Context 注入/附着、`FlowChangedEvent` 与 `GoTo` await continuation 都可能在当前调用栈里再次 `GoTo` 或释放宿主。默认实现先发布新 pending owner、再结束被顶替 task；构建 scope 后重新确认宿主仍存活且请求仍是最新；发布事件或 task 终态前先摘掉 entering / active owner。这样重入的新意图不会被外层旧调用覆盖，陈旧 scope 不会继续 `OnEnter`，下一轮 runner 也不会被上一轮 finally 错误停掉。
- **公共提交只在 Unity 主线程**：`OnEnter` / `OnExit` 允许业务 await 后在 worker 物理完成，但默认实现先捕获结果并切回主线程，再分类取消/异常、撤 scope、更新 `Current`、发送 `FlowChangedEvent` 与完成 `GoTo` task。状态 hook 可以离开主线程做纯计算；触碰 Context / Bag / Unity 对象前仍须自行回主线程，迟到的 `OnExit` 同样不得使用已撤 scope。

### 5. 失败语义：Enter 失败 = 明确的"无状态"，不静默

- `OnEnter` 抛异常/被 flow 顶替或销毁取消：子 Context 立即撤（Bag 把已加载的部分资源放掉），`Current = null`。flow 自己请求的取消让 `GoTo` 以取消结束；其它异常从 UniTask 冒出，由调用方决定重试/进错误状态，框架不猜（对齐存储的 fail-fast：流程走错比音效丢一声严重得多）。
- 下游操作若在 flow token 未取消时自行抛 `OperationCanceledException`，不能冒充“最新意图胜”的正常取消；默认实现把它包装为进入失败，并把 UniTask 交回的取消异常保留为 InnerException。UniTask 的 async builder 可能规范化原 OCE，因此不承诺对象身份或原 message。否则项目导航 Adapter 会把真实连接/资源故障静默吞掉，让流程停在无状态却没有线索。
- `GoTo` 的 UniTask 必须被 `await` 或由导航边界显式观察：UI 不关心完成时机，不代表可以丢弃进入失败；`OnEnter` 内转向因不能 await，应交给一个捕获取消、记录其它异常的 fire-and-forget Adapter。
- 状态依赖的主页面是进入成功的不变量：当 UI Adapter 允许 `Open<T>` 以 null 表示无法创建时，`OnEnter` 应使用 UI Module 的 `OpenRequired<T>` 严格入口。这样开窗失败沿 `GoTo` 冒出并保持 `Current = null`；可选提示窗仍可使用宽松入口就地降级。
- `OnExit` 抛异常：经统一 `Log` Seam 记录 Error 后**继续转换**（离开失败不该把整个游戏卡死在旧阶段；旧子 Context 照撤，文件 / 遥测 sink 也能拿到同一异常）。
- `OnExit` 是尽力而为的“优雅告别”，不是资源所有权边界。宿主销毁时，尚未开始的退出不会补调；已经开始但不结束的退出也不能阻止整棵撤。所有可靠清理必须进入状态 `Bag` 或子 Context 的 owned 服务，迟到的 `OnExit` 代码不得再依赖已撤的 Context / Bag。
- Dispose 后调用 `GoTo`：抛 `ObjectDisposedException`（对齐 GameContext.ExecuteCommand 语义）。

### 6. 刻意不做

- **场景绑定**：状态 ≠ 场景（多状态共享一场景、一状态加载多场景都常见）。状态在 `OnEnter` 里自己 `Bag.LoadScene(...)`，退出随 Bag 卸载——组合既有原语。
- **转换表 / 守卫 / 历史栈（pushdown）**："返回上一状态"是业务记一个变量再 GoTo 的事；UI 返回栈已由 UI 框架管（ADR-0016），流程层再来一个栈会打架。
- **加载进度聚合**：`OnEnter` 内自己汇报（事件/Model），转场 UI 订阅——各游戏转场表现差异太大，不值得抽象。
- **场景内 Mono Context 自动挂到状态子 Context**：加载场景里的 `MonoGameContextBase` 默认父链回退 Global；要挂状态子 Context 可运行时赋 `Parent Context`（时序要求在其 Awake 前，v1 不内建这条自动线，观察实际需求再说）。

## Consequences

- 业务获得「阶段 = 类型 + 作用域」的显式结构：看 `FlowState` 子类列表即知游戏有哪些阶段，每阶段占用什么一目了然（都在它的 InstallBindings/OnEnter 里）。
- 转换语义（最新意图胜、Enter 失败无状态）是**框架拍板**的约定——换取业务不必自己处理竞态；不合口味的项目自己包一层排队策略。
- 状态机自身无 Unity 对象，PlayMode 测试可全程无场景跑（转换/取消/失败/事件全可同步或短 await 断言），batchmode 无风险。
- 没有取消 token 的 `OnExit` 不再拥有 flow 的逻辑寿命：宿主释放可立即完成 flow 收尾，同时由一个窄的物理 owner 保留异常观察。这增加了一条明确边界，但避免第三方上报、存档等退出任务把 Context 永久挂住。
- `IGameFlow` 从 Utility 修订为 System 是一次有意的源码兼容性调整：运行时所有权不变，注册可用层感知入口简化为 `RegisterOwnedSystem(new GameFlow())`；调用端将 `GetUtility<IGameFlow>()` 改为 `GetSystem<IGameFlow>()`，View 端改走 Command / 只读投影。换来的是编译器重新阻止 View 与 Utility 直接驱动业务流程。
- demo 章做「启动→登录→大厅→战斗」四状态迷你 Flow：面板实时显示 Current / 流转日志（含 GoTo 三种结局），大厅注册阶段私有服务演示整棵撤，战斗带构造参数 + 1.5s 模拟加载供手动验证最新意图胜。
