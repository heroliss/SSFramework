# ADR-0023：游戏流程状态机 —— IGameFlow：显式 Flow + 每状态一个子 Context

**Status:** Accepted（2026-07-04）

## Context

roadmap 中期新模块第三项：游戏流程状态机——启动→登录→大厅→战斗的**显式 Flow**，圈定的核心思路是「每个状态一个子 Context，天然利用作用域树的整棵撤语义」。

要解决的真问题：没有显式流程时，"现在游戏在哪个阶段、这个阶段占用的资源/订阅/服务什么时候撤"散落在各场景脚本里——切阶段漏清理是最常见的泄漏来源，而框架已有的 `GameContext` 作用域树 + `DisposableBag` 恰好就是为「整棵撤」造的，缺的只是一个把"阶段"显式化并驱动子 Context 建/撤的编排者。

既有约束与先例：

- **GameContext 可嵌套可释放**：`ContainerBuilder.SetParent` + `GameContext.Dispose`（连带 `RegisterOwned` 实例与全部事件 Subject）已是现成原语——状态机不发明新作用域机制，只是编排既有机制。
- **注册即注入（ADR-0019）**：`RegisterOwned` 的纯 C# 实例在 GameContext 构造时统一 `Inject` + `AttachTo`（`IHasGameContext` 字段自动回填）——状态机作为纯 C# 服务注册即可拿到宿主 Context，无鸡生蛋问题。
- **全层可读服务走 Utility**（配置表先例，ADR-0009）：流程切换要能从 View（登录按钮）和 System（战斗结束）触发，`IUtility` 是现成的全层可达通道。
- **不过度设计**：罕见需求用现有原语组合，不加专门 API。

## Decision

### 1. API 形态：实例式 GoTo + 状态基类

```csharp
public interface IGameFlow : IUtility
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

### 2. 每状态一个子 Context：整棵撤

进入状态时 flow 用宿主 Context 做父级构建子 Context（`SetParent` + 状态的 `InstallBindings`），退出时整个 Dispose：

- 状态私有的 Model/System/Service 注册在子 Context（`RegisterOwned` 随撤释放），阶段结束不残留；
- 状态期间的订阅/资源/句柄全进状态 `Bag`（子 Context 的 bag），退出统一放掉——**切阶段漏清理**这一最大泄漏源被结构性消灭；
- 子 Context 解析未命中自动回退父链→全局：状态内代码照常 `GetUtility<IAudioUtility>()` 等取全局服务。

### 3. 载体与注册：纯 C# 进内核 `Core/Flow/`，注册为 Utility

- `GameFlow`（纯 C#，`IDisposable`，`IHasGameContext`）：零第三方依赖（只用 UniTask），进内核。注册 `builder.RegisterOwned(new GameFlow(), typeof(IGameFlow))`——ADR-0019 注入语义自动回填宿主 Context；宿主 Context Dispose → flow Dispose → 当前状态退出 + 子 Context 撤。
- **不做 Mono 版**：flow 没有 Inspector 可配项（状态是代码 new 的），也不该跟随场景节点（流程比场景活得长）。要看运行时状态，走后续的框架诊断面板（roadmap 中期⑥）。
- **子流程 = 组合**：战斗内的阶段机（准备→作战→结算）就是在 `BattleState.InstallBindings` 里再注册一个 `GameFlow`——作用域树天然嵌套，不做 HSM（分层状态机）专门支持。

### 4. 转换语义：串行化 + 最新意图胜

- 转换全程串行：`OnExit(旧) → Dispose(旧子 Context) → Build(新子 Context) → OnEnter(新)`。
- **转换进行中再 GoTo**：取消在途 `OnEnter` 的 ct、等它结束，直接 Dispose 半进入状态的子 Context（**不调它的 OnExit**——`OnExit` 只在 `OnEnter` 成功完成后才有资格被调；半进入状态的清理靠 Bag/子 Context 整棵撤，这正是 Bag 的本职）。排队槽只有一格、新请求顶替旧排队（最新意图胜：长加载中收到"掉线回登录"，登录赢，不排队）。被顶替/取消的 `GoTo` 返回的 UniTask 以取消结束。
- **同类状态 GoTo**：正常退旧进新（重开一局是刻意行为，不做幂等——与 PlayMusic 的同 clip no-op 语义相反，各自合理）。
- 转换完成后向宿主 Context `SendEvent(new FlowChangedEvent(from, to))`——loading 界面/埋点订阅这一个事件即可，不用侵入每个状态。

### 5. 失败语义：Enter 失败 = 明确的"无状态"，不静默

- `OnEnter` 抛异常/被取消：子 Context 立即撤（Bag 把已加载的部分资源放掉），`Current = null`，异常从 `GoTo` 的 UniTask 冒出——由调用方决定重试/进错误状态，框架不猜（对齐存储的 fail-fast：流程走错比音效丢一声严重得多）。
- `OnExit` 抛异常：LogException 后**继续转换**（离开失败不该把整个游戏卡死在旧阶段；旧子 Context 照撤）。
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
- demo 章做「启动→登录→大厅→战斗」四状态迷你 Flow：面板实时显示 Current / 流转日志（含 GoTo 三种结局），大厅注册阶段私有服务演示整棵撤，战斗带构造参数 + 1.5s 模拟加载供手动验证最新意图胜。
