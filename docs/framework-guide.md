# SSFramework 用户手册

## 目录

1. [框架理念](#1-框架理念)
2. [架构总览](#2-架构总览)
3. [快速开始](#3-快速开始)
4. [Context（上下文）](#4-context上下文)
5. [Model 与 Event（数据层）](#5-model-与-event数据层)
6. [System（逻辑层）](#6-system逻辑层)
7. [Utility（工具层）](#7-utility工具层)
8. [View（视图层）](#8-view视图层)
9. [Command（命令）](#9-command命令)
10. [多上下文：层级与平行](#10-多上下文层级与平行)
11. [容器注册与解析规则](#11-容器注册与解析规则)
12. [纯代码上下文](#12-纯代码上下文)
13. [AssetReference（资源引用）](#13-assetreference资源引用)
14. [数据流：异步原语的统一抽象](#14-数据流异步原语的统一抽象)

---

## 1. 框架理念

---

### 🔧 从一个问题开始

传统 MVC 里，Controller 是一个矛盾的存在：它既要响应用户输入、又要协调数据、还要驱动业务逻辑，随着项目规模增长，Controller 几乎必然变成难以维护的"垃圾桶"。

这个框架的出发点是把 Controller 一分为二：

| | 职责 | 归属 |
|---|---|---|
| **System** | 封装"怎么做"——修改数据、协调逻辑、发出通知 | 逻辑开发者 |
| **Command** | 封装"做什么"——用户意图的原子化表达，连接 View 与 System | 视图开发者 |

两者之间有一条清晰的接缝：视图开发者定义 Command 接口，逻辑开发者实现 System，通过 Command 对接，互不干扰。Command 的典型职责来自两个方向——**向下**整理参数调用 System，**向上**取数适配后返回给 View。

---

### ➡️ 数据流动的方向

有了这个分工，整条流程的方向就自然确定了：

```
调用方向：  View ──→ Command ──→ System ──→ Model
数据方向：  View ←── 查询 Command 返回订阅源 ←── System / Model
```

> **核心原则** — View 只发 Command：不获取 System，不持有 Model，不写 Model，也不发送 Event。数据反向流动时，View 通过只读查询 Command 拿到 `Observable<T>` / `ReadOnlyReactiveProperty<T>` 等只读订阅源，再用 `Bag` 管理订阅生命周期；需要当前值时优先返回 `ReadOnlyReactiveProperty<T>`。这条单向约束保证了任何改动都有迹可查，也杜绝了逻辑环路。

---

### 📊 重新理解"数据"

沿着这条数据流思考下去，会遇到一个有趣的问题：**事件算是数据吗？**

把数据拆成两个维度——*是否有状态*（能随时查询当前值）和*是否可观察*（能订阅变化）——就能得到一张完整的地图：

|  | 不可观察 | 可观察 |
|---|---|---|
| **有状态**（保留当前值） | 普通字段 `int HP` | `SerializableReactiveProperty<int> HP` |
| **瞬时**（无当前值可查） | — 无意义，不存在 | 事件 `IEvent` |

事件正好落在"瞬时可观察"这格。它和 Model 中的字段**同属数据层**，只是没有"当前值"——这不只是概念整理，它直接推导出了权限规则：

> **设计推论** — View 可以订阅由查询 Command 返回的 `Observable<T>` / `ReadOnlyReactiveProperty<T>`，也可以监听事件，因为两者都是在"观察"数据。但 View **不能发出事件**，就如同不能直接写 Model 字段——View 只读不写，System 才是数据变化的生产者。

**选哪个？** 问自己一个问题就够了：*新订阅者需要立刻知道当前状态吗？* 需要 → `ReactiveProperty` / `SerializableReactiveProperty`，对外暴露只读的 `ReadOnlyReactiveProperty`；不需要 → `Event`。

---

### 🔁 生命周期的统一管理

把这些概念落到代码，会发现它们共享同一个问题：**订阅需要取消，引用需要释放**。框架中一切有生命周期的资源都实现 `IDisposable`，通过同一套模式管理。`MonoViewBase` 内置 `Bag`（`DisposableBag`），所有订阅经它统一登记，`OnDestroy` 时批量清理：

```csharp
protected override void Awake()
{
    base.Awake();
    var hp = this.ExecuteCommand(new GetHPStateCommand());
    Bag.Subscribe(hp, OnHPChanged);                   // ReadOnlyReactiveProperty 订阅（R3 自动推 current value）
    Bag.Subscribe<PlayerHurtEvent>(OnHurt);            // 事件订阅
    Bag.Subscribe(_btn.onClick, OnClick);              // UnityEvent 监听
}
// 无需手动 OnDestroy —— MonoViewBase 已经处理
```

| 资源类型 | Dispose 行为 |
|---|---|
| `GameContext` | 释放事件总线，触发 `CancellationToken` 取消 |
| 事件 / 属性订阅 | 取消订阅 |
| `AssetReference` | 释放已加载的资产 |

> **提示** — Context 的 Dispose 会级联取消所有使用 `ctx.CancellationToken` 的异步操作，不需要手动处理。View 销毁时 `Bag.Dispose()` 同样会自动级联清理所有注册的订阅。

---

### 🏷️ 用类型代替字符串和枚举

代码里的字符串和枚举都是"廉价标识"——用一个值区分不同事物。但**类本身就是一种类型**，很多时候这种区分直接用类型来表达更安全、更易维护。

以事件系统为例：

```csharp
// ❌ 字符串事件：重构时漏改一处就出 bug，IDE 无法追踪引用
eventBus.Send("player_hurt");

// ❌ 枚举事件：添加新事件要改枚举定义，监听方要写 switch-case
eventBus.Send(EventType.PlayerHurt);

// ✅ 类型事件：每种事件本身就是一个类型，IDE 可找到所有引用，重命名安全
public record struct PlayerHurtEvent(int Damage) : IEvent;
this.SendEvent(new PlayerHurtEvent(10));
this.RegisterEvent<PlayerHurtEvent>(e => TakeDamage(e.Damage));
```

> **延伸思考** — 这个思路贯穿整个框架：Model/System/Utility 用类型区分而非枚举分发，`AssetReference<AudioClip>` 用泛型类型替代字符串路径，Command 用具体类型区分操作。**代码里能少一个字符串或枚举，就少一处潜在的错误来源和重构负担。**

框架**不限制**你的使用方式——如果某个场景确实需要字符串或枚举驱动的事件，包一层即可：

```csharp
// 字符串驱动：
public record struct StringEvent(string Type) : IEvent;
this.SendEvent(new StringEvent("scene_loaded"));

// 枚举驱动：
public record struct UIEvent(UIActionType Action) : IEvent;
this.SendEvent(new UIEvent(UIActionType.Open));
```

---

### 🔌 面向接口，按需替换

层与层之间的依赖一律通过**接口**表达，调用方不感知背后的具体类。View 不直接持有这些接口；它只执行 Command，由 Command 在上下文中解析接口：

```csharp
public readonly struct HealCommand : ICommand
{
    public void Execute(ICommandContext ctx) => ctx.GetSystem<IPlayerSystem>().Heal();
}
```

这带来一个关键能力：在子 Context 的 `InstallBindings` 里注册不同实现，整个上下文的行为就换掉了，调用方代码**零修改**。测试场景、平台差异化实现、运行时切换——都能用同一个机制解决。

> **典型场景** — 测试时在 `TestContext` 注册 `MockPlayerSystem`，主场景注册 `PlayerSystem`，View 和 Command 的代码完全不变。

---

### 🧩 多上下文：组合而非配置

多个 Context 可形成父子关系，子 Context 自动继承父 Context 的服务，只需注册自己独有的部分：

```
MainContext               ← 全局服务：Audio、Save、Config（注册一次）
├── LobbyContext          ← 只注册大厅相关内容，全局服务自动继承
└── BattleContext         ← 只注册战斗相关内容，全局服务自动继承
    └── BossContext       ← Boss 专属逻辑，继承整条链
```

这一层级结构直接带来两个实际收益：

> **多上下文的优势**
>
> **模块复用** — 全局服务（Audio、Save、Config）在根 Context 注册一次，所有子上下文自动继承，不重复注册、不互相耦合。
>
> **最小测试环境** — 为被测模块搭一个只含必要依赖的 Context，其余用 Mock 填充，不需要启动整个游戏。

---

### 🗂️ 让 Hierarchy 替你说话

大多数框架用代码声明依赖关系；这里直接用 **Unity 的场景层级**表达 Context 的父子关系——把子 Context 的 GameObject 放在父 Context 节点下，框架自动识别，无需任何注册代码。**改变继承关系只需拖动 GameObject。**

Model 和 System 也可以直接挂在 GameObject 上，出现在 Hierarchy 和 Inspector 里：

```
Hierarchy                        Inspector（运行时）
MainContext
├── PlayerModel       →          HP: 87  Stamina: 42
├── InventorySystem   →          Resolved Context: MainContext
└── Canvas
    └── HudView       →          [Inject] _fmt: CounterFormatter
```

运行时可以直接在 Inspector 里看到 Model 字段的当前值，拖动节点即可把 Model 换到另一个 Context 验证不同组合——这是大多数框架做不到的。

> **澄清误解** — "挂在 GameObject 上 = 视图层"是一个普遍但错误的直觉。MonoBehaviour 的本质只是让 Unity 引擎识别这个对象并参与生命周期调度，**一个对象属于哪个架构层，由设计逻辑决定，与是否继承 MonoBehaviour 无关。**
>
> 正因如此，你可以直接在 System 或 Model 里写可视化调试代码——`OnDrawGizmosSelected` 绘制 AI 路径、`OnGUI` 叠加数值面板，这些完全属于 System/Model 内部，不影响架构定位，调试完删掉，其他代码零影响。

---

### 🪢 视觉是核心，引擎组件可跨层

游戏与普通业务代码有一个根本差异：**视觉本身就是产品**。这让很多 Unity 引擎组件天然同时承担"数据"、"逻辑写入对象"、"视图源头"三重身份——`Rigidbody` 是物理状态（位置、速度、约束），是物理仿真的写入接口（`AddForce` 修改它），也是渲染管线每帧读取的位置源（玩家看到的"球在飞"）。`Transform`、`Collider`、`Animator`、`ParticleSystem`、`LineRenderer` 同属此类。

如果僵化地把它们塞进单一层，反而要在层间复制冗余数据、手工同步。框架的回答是：**这类引擎组件正交于五层，可以同时被多个层共享**——Model 直接存放，System 直接写入，View 由引擎自动渲染。

```csharp
// Model：直接持有 Rigidbody，把它当作"物理状态"
public class ProjectileModel : MonoModelBase
{
    [field: SerializeField] public Rigidbody Body   { get; private set; }
    [field: SerializeField] public RP<float>   Damage { get; private set; } = new(10f);
}

// System：直接写入引擎组件，效果等价于改 Model 字段
public class ProjectileSystem : MonoSystemBase, IProjectileSystem
{
    [Inject] private ProjectileModel _model;
    public void Launch(Vector3 dir, float speed)
        => _model.Body.AddForce(dir * speed, ForceMode.Impulse);
}

// View：什么都不用写——Unity 渲染管线每帧自动把 Body 的 transform 画出来
```

数据流依然是单向的：System 写、View 看，Model 持有载体。只是这次"写入 → 显示"的链路由 Unity 引擎自己闭环，框架不需要在中间再转一道手——硬要把位置抄到 `RP<Vector3>`、再让 View 订阅后写回 `transform.position`，等于绕开引擎做一遍重复工作。

> **何时拥抱跨层** — 看一个引擎组件是否同时满足：(1) Inspector 可直接观察当前值；(2) 由引擎或 System 修改；(3) Unity 自带渲染或反映机制。满足就让它直接作为 Model 字段，让引擎做它最擅长的事。

> **边界** — 这不是普通业务字段的"逃逸通道"。金币、等级、回合状态等纯逻辑数据仍走"Model `RP<T>` → System 写 → Command 返回只读源 → View 订阅"的完整链路。跨层只属于那些天生就被引擎本身贯穿的组件。

---

## 2. 架构总览

框架由五层组成，自上而下依次是 **View / Command / System / Model / Utility**：

![SSFramework 五层架构图](SSFramework-architecture.png)

### 各层职责与内部结构

| 层 | 职责 | 内部结构 |
|---|---|---|
| **View** | 观察数据、响应用户、不直接写状态 | 严格树状（沿 Unity Hierarchy，父 View → 子 View） |
| **Command** | 封装"做什么"——一次用户意图的原子化表达，View 写入数据的唯一入口 | 本质网状、主体树状 |
| **System** | 封装"怎么做"——修改 Model、协调规则、发出事件，Model 的唯一合法写入者 | 本质网状、主体树状 |
| **Model + Event** | 数据层。Model 持有当前值，Event 是无当前值的瞬时通知（详见 §1.3） | 本质网状、主体树状 |
| **Utility** | 无状态工具函数，不依赖任何业务层 | 独立基础层，所有层均可调用 |

> **Command 与 System 是拆开的 Controller** —— 视图开发者声明 Command 接口，逻辑开发者实现 System，两边通过 Command 类型对接，互不耦合。Command 的典型职责：**向下**整理参数调用 System，**向上**从 Model 取数适配后返回给 View。

### 数据流向

```
调用方向（写入）：
  View ──(ExecuteCommand)──→ Command ──(调用)──→ System ──(修改)──→ Model

数据方向（读取 / 订阅）：
  View ←──── Command ←──── System / Model / Event
```

- **写入必须完整走链** —— View 任何状态变化都要通过 `Command → System → Model`，View 自身不直接写
- **读取也通过 Command** —— View 不直接获取 Model/System；需要一次性取值或持续订阅时，用只读 Command 返回值或订阅源

### MonoBehaviour 与 Rigidbody：贯穿五层的引擎能力

架构图右侧的 `MonoBehaviour / Rigidbody / Transform / ...` 是 Unity 引擎层面的**完整解决方案**——任意业务层都可以继承 `MonoBehaviour` 获得生命周期与 Inspector 序列化，Model 可以直接持有 `Rigidbody` 引用把物理对象当数据载体使用。它们正交于五层架构，**不参与依赖判定**（详见 §1.8"视觉是核心，引擎组件可跨层"）。

### 各层权限速查

各层的权限由接口在编译期约束，不靠口头约定：

| 层 | 可获取（`this.GetXxx<T>()`） | 可操作 |
|---|---|---|
| **Model** | Utility | — |
| **System** | Model、System、Utility | 修改 Model、发送/监听事件 |
| **Utility** | — | — |
| **View** | Utility | 发送 Command、监听事件 |
| **Command** | 通过 `Execute(ICommandContext ctx)` 参数访问一切层 | 调用 System、读取 Model、发送事件 |

> View 不在权限矩阵里直接访问 Model/System/EventBus，是为了强制所有外发动作只走 Command。需要 View 显示状态时，用只读查询 Command 返回值；持续状态用只读查询 Command 返回 `ReadOnlyReactiveProperty<T>` / `Observable<T>` 订阅源。

---

## 3. 快速开始

看一个最小可运行的例子，把上面提到的所有层串起来：

```
Scene
└── MainContext          ← 全局根（MonoGlobalContext 子类）
    ├── PlayerModel      ← 数据层（MonoModelBase 子类）
    ├── PlayerSystem     ← 逻辑层（MonoSystemBase 子类）
    └── Canvas
        └── HudView      ← 视图层（MonoViewBase 子类）
```

```csharp
// Context — 注册无需挂节点的纯 C# 服务
public class MainContext : MonoGlobalContext
{
    protected override void InstallBindings(ContainerBuilder builder)
    {
        builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
    }
}

// Model — 持有数据，RP<int> 可订阅且 Inspector 可见
public class PlayerModel : MonoModelBase
{
    [field: SerializeField] public RP<int> HP { get; private set; } = new(100);
}

// System — 封装逻辑，是修改数据的合法来源
public interface IPlayerSystem : ISystem
{
    ReadOnlyReactiveProperty<int> HP { get; }
    void TakeDamage(int amount);
}

public class PlayerSystem : MonoSystemBase, IPlayerSystem
{
    [Inject] private PlayerModel _model;

    ReadOnlyReactiveProperty<int> IPlayerSystem.HP => _model.HP;

    public void TakeDamage(int amount)
    {
        _model.HP.Value -= amount;
        this.SendEvent(new PlayerHurtEvent(amount));  // 同步通知感兴趣的监听者
    }
}

// Command — 封装一次用户意图，连接 View 和 System（推荐 struct：零分配）
public readonly struct TakeDamageCommand : ICommand
{
    public void Execute(ICommandContext ctx) => ctx.GetSystem<IPlayerSystem>().TakeDamage(10);
}

public readonly struct GetHPStateCommand : ICommand<ReadOnlyReactiveProperty<int>>
{
    public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx) => ctx.GetSystem<IPlayerSystem>().HP;
}

// View — 只发 Command，不直接获取 Model/System/EventBus
public class HudView : MonoViewBase
{
    protected override void Awake()
    {
        base.Awake();
        var hp = this.ExecuteCommand(new GetHPStateCommand());
        Bag.Subscribe(hp, hpValue => _hpText.text = hpValue.ToString());  // 订阅即得 current value
        Bag.Subscribe<PlayerHurtEvent>(e => PlayHurtAnim());
        Bag.Subscribe(_btn.onClick, () => this.ExecuteCommand(new TakeDamageCommand()));
    }
    // Bag 由 MonoViewBase.OnDestroy 自动释放
}
```

点击按钮，数据流完整走一圈：Command 调用 System，System 修改 Model 并发出事件，View 响应式更新文本，同时播放受击动画。文本更新和动画播放互相不知道对方——这就是第 1 章"单向数据流"在代码里的具体形状。

---

## 4. Context（上下文）

Context 是整个框架的容器，它持有 DI 容器、事件总线和命令系统，是所有层注册和解析依赖的根据地。理解 Context，是理解后续一切的前提。

> **术语口径：Context vs 作用域。** 对象统一叫 **Context / 上下文**（`GameContext` 实例——持有容器、事件、命令的能力环境，回答「它是什么」）。**作用域 / scope** 只描述「生命周期 / 解析边界」这一侧面：多个 Context 嵌套成一棵**作用域树**做解析回退，`Bag.CreateChild()` 开一个**更短的作用域**。同一对象两面都成立，但命名时——对象 = Context，结构与寿命 = 作用域；别把某个 Context 实例直接叫成「作用域」。

### 全局根上下文

每个项目需要一个全局根上下文，继承 `MonoGlobalContext`，挂在场景根节点：

```csharp
public class MainContext : MonoGlobalContext
{
    protected override void InstallBindings(ContainerBuilder builder)
    {
        // 在这里注册纯 C# 服务——不需要挂在场景节点上的对象
        builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
        builder.RegisterValue(new JsonUtility(),   typeof(IJsonUtility));
    }
}
```

`MonoGlobalContext` 会自动将自身设为 `GameContext.Main`、开启 `DontDestroyOnLoad`，并检测重复实例。项目中只应有一个。

### Awake 执行顺序

框架通过 `DefaultExecutionOrder` 保证各层初始化顺序，让每一层 Awake 时它所需的上层都已就绪：

```
-2000  MonoGlobalContext    建容器，设置 GameContext.Main
-1000  MonoGameContextBase  建容器，识别父级（子/平行上下文用）
 -400  MonoUtilityBase      注册到容器
 -300  MonoModelBase        注册到容器
 -200  MonoSystemBase       注册到容器
 -100  MonoViewBase         注入 [Inject] 字段
```

> **提示** — 实际编写时几乎感知不到这个顺序。在任何 `MonoXxxBase` 的 `Awake()` 里调用 `base.Awake()` 后，当前层的注入已完成；若需要引用其他同级服务，在 `Start()` 或第一次调用时懒加载即可，不要在 `Awake()` 里直接访问兄弟节点的服务。

---

## 5. Model 与 Event（数据层）

Context 搭好之后，先思考数据——游戏状态放在 Model 里，状态变化的通知则通过 Event 广播。两者共同构成数据层，只是生命周期不同：Model 的数据随 Context 持续存在，Event 是一次性的瞬时信号。

### Model：有状态数据

Model 持有需要随时查询的游戏状态。需要让外部订阅变化的字段，用框架提供的 `RP<T>`（`using R3;`——框架的 RP 定义在 R3 命名空间下）——它是 `SerializableReactiveProperty<T>` 的泛型包装类，支持 Unity 序列化，Inspector 直接显示值（配有专用 `[CustomPropertyDrawer]`，不会多套一层），可用于任意类型。

推荐写成一行 auto-property，并把 `[SerializeField]` 标到 backing field 上：

```csharp
[field: SerializeField] public RP<int>        HP    { get; private set; } = new(100);
[field: SerializeField] public RP<float>      Speed { get; private set; } = new(5f);
[field: SerializeField] public RP<Vector3>    Pos   { get; private set; } = new();
[field: SerializeField] public RP<PlayerData> Stats { get; private set; } = new();
```

只读返回类型统一用 `ReadOnlyReactiveProperty<T>`。因为 `RP<T>` 继承链为 `RP<T>` → `ReactiveProperty<T>` → `ReadOnlyReactiveProperty<T>`，System/Command 实现可直接把 `RP<T>` 赋给 `ReadOnlyReactiveProperty<T>` 接口属性，**零分配无转换**，无需额外的 `ROP<T>` 包装类。不引入 `ReadOnlyReactiveProperty<int>` 之类的别名：C# 不支持泛型 `using` 别名，闭合别名（`using ReadOnlyReactiveProperty<int> = ReadOnlyReactiveProperty<int>;`）只能 per-assembly 声明、跨程序集失效，且 `ROP` 缩写不如全名自解释，对人和 AI 都更难追溯。

> **`RP<T>` 位置说明**：`RP<T>` 在 `Game.Framework` 程序集内（`Scripts/Reactive/RP.cs`），业务无论在 Assembly-CSharp 还是独立 asmdef 引用框架后都能使用；其 Inspector 绘制器 `RPDrawer` 在 `Game.Framework.Editor` 程序集，注册到 `RP<>`。

对外不要把可写的 `ReactiveProperty<T>` 暴露给 View。View 只执行 Command；需要显示状态时，由只读查询 Command 返回 `ReadOnlyReactiveProperty<T>` 或 `Observable<T>`。优先返回 `ReadOnlyReactiveProperty<T>`，因为它既能订阅变化，也能读取 `CurrentValue` 初始化 UI。

#### 常见 R3 类型分工

| 类型 | 角色 | 是否有当前值 | 是否可写 | 典型用途 |
|---|---|---:|---:|---|
| `Observable<T>` | 只读时间流基类 | 不保证 | 否 | View 只需要订阅变化，不关心当前值 |
| `ReactiveProperty<T>` | 可写响应式状态 | 是，`Value` / `CurrentValue` | 是 | Model/System 内部持有和修改状态 |
| `RP<T>` | 泛型可写可序列化响应式状态 | 是 | 是 | **Mono Model 字段首选**，任意类型，Inspector 可见且不多套层 |
| `SerializableReactiveProperty<T>` | Unity 可序列化响应式状态 | 是 | 是 | `RP<T>` 的基类；不直接用，用 `RP<T>` 代替 |
| `ReadOnlyReactiveProperty<T>` | 只读响应式状态 | 是，`CurrentValue` | 否 | Command/System 返回给 View 的状态源（`RP<T>` IS-A `ReadOnlyReactiveProperty<T>`） |
| `ISubject<T>` / `Subject<T>` | 手动推送的冷热桥接流 | 否 | 可 `OnNext` | 封装外部回调、临时事件流；不要当长期状态用 |
| `Observer<T>` | 订阅端回调对象 | — | — | 需要处理 `OnErrorResume` / `OnCompleted` 时使用 |

#### Mono 路径（推荐）

挂在 Context 子节点下，Awake 时自动注册：

```csharp
public class InventoryModel : MonoModelBase
{
    [field: SerializeField] public RP<int> Gold { get; private set; } = new(0);
    public List<ItemData> Items = new();
}
```

#### 纯 C# 路径

适合配置类数据，或不需要出现在 Hierarchy 中的数据：

```csharp
public class ConfigModel : IModel { public string Language = "zh"; }

ctx.RegisterModel(new ConfigModel());
```

### Event：瞬时可观察数据

Event 用于"发生了某件事"的一次性通知，不保留历史。推荐用 `record struct` 定义，零堆分配：

```csharp
public record struct GoldChangedEvent(int Delta) : IEvent;
public record struct ItemAddedEvent(ItemData Item) : IEvent;
```

System 在修改 Model 后发出对应事件，View 或其他 System 根据需要监听：

```csharp
// System 中发送
this.SendEvent(new GoldChangedEvent(delta));

// View 中监听（推荐用 Bag 统一管理生命周期）
Bag.Subscribe<GoldChangedEvent>(e => UpdateUI(e.Delta));

// 任意 System/Command 中监听并自行管理 IDisposable
var sub = this.RegisterEvent<GoldChangedEvent>(e => UpdateUI(e.Delta));
```

> 选择时问自己：*新订阅者需要立刻知道当前状态吗？* 需要就用 `SerializableReactiveProperty`，不需要就用 `Event`。

---

## 6. System（逻辑层）

数据有了，接下来需要操作它的逻辑。System 是修改 Model、发出 Event 的合法来源，业务逻辑的主要归宿。

#### Mono 路径（推荐）

挂在 Context 子节点下，`[Inject]` 字段由框架在 Awake 时自动填充：

```csharp
public class InventorySystem : MonoSystemBase, IInventorySystem
{
    [Inject] private InventoryModel _model;

    public void AddGold(int amount)
    {
        _model.Gold.Value += amount;
        this.SendEvent(new GoldChangedEvent(amount));
    }

    public void AddItem(ItemData item)
    {
        _model.Items.Add(item);
        this.SendEvent(new ItemAddedEvent(item));
    }
}
```

#### 纯 C# 路径

当 System 不需要出现在场景中，或需要在代码里精确控制初始化顺序时使用：

```csharp
public class AudioSystem : ISystem, IHasGameContext, IAudioSystem
{
    [Inject] private ConfigModel _config;
    private GameContext _ctx;
    IGameContext IHasGameContext.Context => _ctx;
}

var audio = new AudioSystem();
ctx.RegisterSystem(audio);
ctx.Inject(audio);    // 解析 [Inject] 字段
ctx.AttachTo(audio);  // 回写 _ctx 字段，让扩展方法可以使用
```

---

## 7. Utility（工具层）

有一类代码既不是状态数据，也不承载业务逻辑——它们是纯粹的工具函数，比如格式化、加密、序列化。这类代码放进 Utility，所有层（Model、System、View）都可以使用，但 Utility 本身不依赖任何层。

#### 纯 C# 路径（推荐）

Utility 通常是无状态的纯函数，不需要出现在场景里，直接在 `InstallBindings` 中注册：

```csharp
public interface IEncryptUtility : IUtility { string Encrypt(string data); }
public class EncryptUtility : IEncryptUtility { public string Encrypt(string data) => /* ... */; }

builder.RegisterValue(new EncryptUtility(), typeof(IEncryptUtility));
```

#### Mono 路径

当 Utility 需要访问 Unity API，或希望在 Inspector 中配置参数时，可以挂在节点上：

```csharp
public class EncryptUtility : MonoUtilityBase, IEncryptUtility
{
    [SerializeField] private string _key;  // Inspector 中配置
    public string Encrypt(string data) => /* ... */;
}
```

### 对象池（IPoolUtility）

框架自带对象池（`Game.Framework.Pool`），是一个 `IUtility`，与 `DisposableBag` 生命周期融合，替代第三方池库。

**注册有三种路径，按池的生命周期选：**

```csharp
// 1) 纯 C# · 跟随 Context：随 GameContext.Dispose 一起清池（推荐——不靠 DontDestroyOnLoad 残留）
builder.RegisterOwned(new PoolUtility(), typeof(IPoolUtility));
// 2) 纯 C# · 不关心释放（全局唯一、随进程退出）：RegisterValue 即可（不被 Context 拥有，不会被 Dispose）
builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility));
// 3) Mono · Inspector 配置：在 Context 子节点挂 MonoPoolUtility，可视化配各 prefab 容量/预热，随该 GameObject/场景销毁自动清池
```

`MonoPoolUtility` 继承 `MonoUtilityBase`、内部复用同一套 `PoolUtility` 逻辑——它在 Inspector 暴露「prefab 池容量 / 预热数」配置，启动时按配置建池并分帧预热，宿主销毁时 Dispose 底层池（销毁停放节点与空闲实例）。需要按池配参数、或希望池跟随某个 Context 节点 / 场景生命周期时用它；全局共享、纯代码配置用上面的 `RegisterOwned`。

最常用的是 `Bag.Rent<T>()`——租借一个对象，宿主销毁 / `bag.Dispose` 时**自动归还**，和 `Bag.Load` 一样无感知：

```csharp
public class HitNumberView : MonoViewBase
{
    void ShowDamage(int n)
    {
        var label = Bag.Rent<DamageLabel>();   // 用完随 View 销毁自动归还，无需手动 Return
        label.Set(n);
    }
}
```

需要自定义工厂、租借/归还钩子或容量上限时，先配置一次池，再手动 `Rent`/`Return`：

```csharp
var pool = this.GetUtility<IPoolUtility>().GetPool<Bullet>(
    factory: () => new Bullet(),
    onReturn: b => b.Reset(),     // 归还时清理状态
    maxSize: 256);
var b = pool.Rent();
// ...
pool.Return(b);                   // 手动管理时显式归还
```

池化对象可实现 `IPoolable`，在 `OnRent` / `OnReturn` 收到回调。要点：

- **状态在归还时清理**（`IPoolable.OnReturn` 或 `onReturn` 委托），避免脏数据被下一个租借者看到。
- **已 `Return` 的对象不要再用**——它可能已被取走。
- 主线程独占；Editor / Development Build 下检测"重复归还 / 归还外来实例"。

#### GameObject / Prefab 池

同一个 `IPoolUtility` 也按 **prefab** 管理 GameObject 池，复用实例、避免频繁 `Instantiate`/`Destroy`。最常用的是 `Bag.Spawn(prefab)`——和 `Bag.Rent` / `Bag.Load` 一样，宿主销毁 / `bag.Dispose` 时**自动 Despawn（归还）**：

```csharp
public class EnemySpawnerView : MonoViewBase
{
    [SerializeField] GameObject _enemyPrefab;

    void SpawnAt(Vector3 pos)
    {
        // 取一个实例并定位；本 View 销毁时随 Bag 自动归还入池
        var enemy = Bag.Spawn(_enemyPrefab, pos, Quaternion.identity);
    }
}
```

要点与心智：

- **键控**：每个 prefab 对应一个池，`Spawn` 复用空闲实例（重置 local transform、`SetActive(true)`），`Despawn` 停用并挂回一个停用的 parking 节点。
- **手动管理**：`this.GetUtility<IPoolUtility>().Spawn(prefab, parent)` 取、`.Despawn(go)` 还（实例自带 `PooledObject` 标记，归还时自动路由回源池，无需再传 prefab）。
- **预热**：`await pool.Prewarm(n, perFrame)` 分帧实例化 `n` 个（每帧 `perFrame` 个，默认 1），把开销摊到多帧（适合加载界面期间调用）。
- **收缩 / 分帧销毁**：`await pool.TrimAsync(target, perFrame)` 把空闲实例分帧收缩到 `target` 个、`await pool.ClearAsync()` 分帧销毁全部空闲（要瞬时全销用 `Clear()`）；C# 池用同步 `pool.Trim(target)`。内存吃紧时回收过度预热的实例。
- **停放点自愈**：内部 `[Game.Framework PooledObjects]` 停放节点若被外部销毁，下次归还会自动重建，归还实例不会散落到场景根。
- **重置钩子**：实例上**任意组件**实现 `IPoolable`，即在 `OnRent` / `OnReturn` 收到回调（`OnReturn` 里清状态）。
- **位置加载组合**：池本身不做按 location 的异步加载——先 `var prefab = await Bag.Load<GameObject>("...")` 取到 prefab 再 `Bag.Spawn(prefab)`，刻意让 `PoolUtility` 不依赖资源系统（保持可被父子 Context 共享、不绑 Context）。
- 主线程独占；Editor / Dev 构建下检测"重复 Despawn / 归还非池化对象"。

---

## 8. View（视图层）

数据层和逻辑层都准备好了，现在需要把它们呈现给用户，并响应用户的操作。View 只做两件事：**观察数据**（订阅由 Command 返回的状态源、监听 Event），**发出意图**（通过 Command）。它不直接获取 System/Model/EventBus，也不直接修改任何状态。

`MonoViewBase` 内置 `Bag`（`DisposableBag`），所有订阅走同一个 `Subscribe` 方法，按参数类型分派，`OnDestroy` 时自动批量释放——不需要手动声明 `_bag` 或覆写 `OnDestroy`：

```csharp
public class InventoryView : MonoViewBase
{
    [Inject] private IEncryptUtility _enc;     // 工具

    protected override void Awake()
    {
        base.Awake();  // 必须先调用，[Inject] 字段在这里完成注入

        // ReadOnlyReactiveProperty
        var gold = this.ExecuteCommand(new GetGoldStateCommand());
        _goldText.text = gold.CurrentValue.ToString();
        Bag.Subscribe(gold, g => _goldText.text = g.ToString());

        // Framework Event（带数据）
        Bag.Subscribe<ItemAddedEvent>(e => RefreshItemList());

        // UnityEvent —— UnityEvent / C# delegate 等没有 IDisposable 的注册，
        // DisposableBag 内部自动包装为 Disposable.Create(...)
        if (_buyBtn != null)
            Bag.Subscribe(_buyBtn.onClick, () => this.ExecuteCommand(new BuyItemCommand(_selectedItemId)));

        // C# event/delegate（两侧对称，第一个 Action 立即执行，第二个 Dispose 时执行）
        Bag.Subscribe(
            () => _shopSystem.OnPriceChanged += RefreshPrice,
            () => _shopSystem.OnPriceChanged -= RefreshPrice);

        // 异步 Command —— 无参重载自动绑定 View + Context 双重生命周期
        if (_saveBtn != null)
            Bag.Subscribe(_saveBtn.onClick, async () =>
                await this.ExecuteCommandAsync(new SaveProgressCommand()));
    }

    // Bag 由 MonoViewBase.OnDestroy 自动释放，无需覆写
}
```

`Bag.Subscribe` 通过参数类型分派，覆盖所有常见订阅场景：

| 参数形态 | 用途 |
|---|---|
| `(Observable<T> source, Action<T> handler)` | R3 Observable / ReactiveProperty（RP 订阅时自动推一次 current value） |
| `<T>(Action<T> handler)` where `T : IEvent` | Framework Event（带数据） |
| `<T>(Action handler, bool invokeImmediately = false)` where `T : IEvent` | Framework Event（忽略数据，可选订阅时立即触发一次） |
| `(UnityEvent evt, UnityAction handler, bool invokeImmediately = false)` | UnityEvent（按钮点击等，可选订阅时立即触发一次） |
| `(UnityEvent<T> evt, UnityAction<T> handler)` | UnityEvent\<T\>（滑条、Toggle 等） |
| `(Action subscribe, Action unsubscribe)` | C# event/delegate（两侧对称） |
| `(IDisposable disposable)` | 已有 IDisposable 的逃生舱口 |

所有重载返回 `IDisposable`，需要提前取消某个订阅时可保存返回值并单独 Dispose。

**订阅时初始化**（与 R3 统一）：
- 状态流（`ReactiveProperty` / `ReadOnlyReactiveProperty`）订阅即得 current value（R3 内置）；想跳过初值用 `.Skip(1)`
- 无数据通知（无参 Framework Event / 无参 UnityEvent）传 `invokeImmediately: true` 在注册后立即跑一次
- 带数据事件需要订阅时初始化，**走 Observable 桥接 + `.Prepend(value)`**（不再为这种场景加重载）：
  ```csharp
  // Framework Event：用扩展方法 OnEvent<T>() 桥接为 Observable<T>
  Bag.Subscribe(this.OnEvent<GoldChangedEvent>().Prepend(new GoldChangedEvent { NewGold = currentGold }), OnGoldChanged);
  // UnityEvent<T>：R3 已提供 AsObservable() 扩展
  Bag.Subscribe(_slider.onValueChanged.AsObservable().Prepend(_slider.value), OnSlide);
  ```
  进入 Observable 后所有 R3 操作符（`Where` / `Throttle` / `CombineLatest` 等）均可用，便利重载之外的复杂订阅一律走这条路径。

### 多生命周期作用域

`Bag` 跟随 View 的 `OnDestroy` 释放，覆盖 80% 的场景。如果某些订阅只在 Enable 期间或某段业务期间存活，按需自行 `new` 一个 `DisposableBag`，用前缀区分清理时机：

```csharp
public class BattleView : MonoViewBase
{
    private DisposableBag _enableBag;   // OnDisable 清理
    private DisposableBag _roundBag;    // 回合结束清理

    protected override void Awake()
    {
        base.Awake();
        var hp = this.ExecuteCommand(new GetHPStateCommand());
        Bag.Subscribe(hp, OnHPChanged);  // 整个 View 寿命（订阅时 R3 自动推 current HP）
    }

    protected virtual void OnEnable()
    {
        _enableBag = Bag.CreateChild();  // 父级 Bag 的子作用域，OnDisable 时 Dispose 自动级联清理
        var isTargeted = this.ExecuteCommand(new GetTargetedStateCommand());
        _enableBag.Subscribe(isTargeted, ShowMark);
    }

    protected virtual void OnDisable()
    {
        _enableBag?.Dispose();
        _enableBag = null;
    }
}
```

有时 View 需要一次性读取某个数据而不是持续订阅，这时用带返回值的 Command，将取数逻辑也封装起来：

```csharp
public readonly struct GetGoldCommand : ICommand<int>
{
    public int Execute(ICommandContext ctx)
        => ctx.GetModel<InventoryModel>().Gold.Value;
}

int gold = this.ExecuteCommand(new GetGoldCommand());
```

View 在 Awake 时按以下顺序查找自己所属的 Context，通常不需要手动设置：

1. Inspector 中显式设置的 `Target Context`
2. Transform 父链中最近的 `MonoGameContextBase`
3. `GameContext.Main` 全局兜底

---

## 9. Command（命令）

View 和 System 之间需要一个"翻译"——View 知道用户想做什么，但不知道怎么做；System 知道怎么做，但不关心谁触发的。Command 就是这个翻译，把一次用户操作封装成一个原子行为，交给 System 执行。

### struct Command（推荐，零 GC 压力）

同步 Command 的默认选择。声明为 `readonly struct`，栈上分配，不产生 GC 压力。依赖通过 `ctx` 参数实时获取，简洁且直接：

```csharp
public readonly struct AddGoldCommand : ICommand
{
    public readonly int Amount;
    public AddGoldCommand(int amount) => Amount = amount;

    public void Execute(ICommandContext ctx)
        => ctx.GetSystem<IInventorySystem>().AddGold(Amount);
}
```

### class Command（支持 `[Inject]`）

当依赖项多、希望框架自动注入时使用。框架在执行前填充 `[Inject]` 字段，但每次 `new` 都会堆分配：

```csharp
public class BuyItemCommand : ICommand
{
    private readonly int _itemId;
    public BuyItemCommand(int itemId) => _itemId = itemId;

    [Inject] private IInventorySystem _inventory;
    [Inject] private IShopSystem _shop;

    public void Execute(ICommandContext ctx)
    {
        var price = _shop.GetPrice(_itemId);
        _inventory.SpendGold(price);
        _inventory.AddItem(_itemId);
    }
}
```

### 带返回值的 Command

当 View 需要取数而非触发操作时，Command 也可以有返回值：

```csharp
public readonly struct GetGoldCommand : ICommand<int>
{
    public int Execute(ICommandContext ctx)
        => ctx.GetModel<InventoryModel>().Gold.Value;
}

int gold = this.ExecuteCommand(new GetGoldCommand());
```

### 异步 Command

涉及 IO、网络或带延时的操作时，使用异步版本。`cancellationToken` 参数已由框架合并好——无需在命令内部再访问 `ctx.CancellationToken`，直接用这一个参数即可：

```csharp
// 异步命令默认也用 readonly struct——struct 一样可以有 async 方法，经 ctx 取依赖
public readonly struct SaveProgressCommand : IAsyncCommand
{
    public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
    {
        // UniTask 的异步 API 都接受 cancellationToken，直接传入
        await ctx.GetSystem<ISaveSystem>().WriteAsync(cancellationToken);
    }
}
```

View 调用无参版本时，框架自动检测 `MonoBehaviour`，把 View 销毁令牌（`GetCancellationTokenOnDestroy`）与 Context 生命周期令牌链接，任一方取消即中止命令：

```csharp
// 从 View 调用：自动绑定 View 销毁 + Context 销毁双重生命周期
await this.ExecuteCommandAsync(new SaveProgressCommand());

// 需要自定义取消源时显式传入
await this.ExecuteCommandAsync(new SaveProgressCommand(), customToken);
```

非 View 路径（如 System / 纯 C# 持有者）调用无参版本时，只会绑定 Context 生命周期。

### 选型建议

| 场景 | 选择 |
|---|---|
| 绝大多数同步场景（默认） | `readonly struct` + `ctx.GetXxx` |
| 依赖项多、需要 `[Inject]` 自动注入 | `class` + `[Inject]` |
| 带返回值，避免装箱 | `readonly struct ICommand<T>` + 可推断调用 `ExecuteCommand(new Cmd())` |
| 异步操作 | `readonly struct` + `IAsyncCommand`（同步异步同款；要 `[Inject]` 才用 `class`） |

### 可插拔 CommandSystem：日志、回放、撤销、自动化测试

`ICommandSystem` 是一个普通接口注册，默认实现就是无状态的 `CommandSystem`。需要插入横切逻辑时，写一个装饰器实现替换默认注册即可——**所有命令一处统一拦截，业务代码零修改**。

```csharp
public sealed class LoggingCommandSystem : ICommandSystem
{
    private readonly ICommandSystem _inner = new CommandSystem();
    public List<string> History { get; } = new();

    public void ExecuteCommand<T>(T command, GameContext ctx) where T : ICommand
    {
        History.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {typeof(T).Name}");
        _inner.ExecuteCommand(command, ctx);
    }
    // ... 其余 5 个重载同样转发到 _inner
}

// MainContext.InstallBindings
builder.RegisterValue(new LoggingCommandSystem(), typeof(ICommandSystem));
```

这个拦截点能承载很多典型需求：

- **操作日志 / 行为分析** —— 记录命令类型与参数，落本地或上报后端
- **撤销 / 重做** —— 命令实现 `IReversible`，CommandSystem 维护历史栈，逆向派发
- **回放与自动化测试** —— 录制时序列化命令流，测试时按同样序列重新派发，断言最终状态
- **优先级 / 队列化** —— 命令进队列、按规则出队执行（同步语义会变异步，需调用方配合）
- **调试拦截 / 权限校验** —— Debug 构建里给特定命令开关、Editor 下补一层合法性检查
- **跨命令事务** —— 装饰器内启动事务，子命令全部成功才提交、否则回滚

由于 Context 解析按精确类型查找、子级可覆盖父级（详见 §11），**作用域天然隔离**：根 Context 注册带日志的版本，子 Context 自动继承；某个子上下文想要本地、不带日志的版本，重新注册一个 `new CommandSystem()` 覆盖即可。

---

## 10. 多上下文：层级与平行

到目前为止，所有示例都在同一个 Context 里运行。真实项目往往需要多个相互独立又有所关联的模块——这时多上下文设计就派上用场了。

### 层级上下文

把子 Context 的 GameObject 放在父 Context 的节点树内，框架自动建立继承关系。子 Context 可以覆盖父级的某个注册，也可以什么都不注册，完全继承父级：

```
MainContext                  ← 全局根，注册 CommandSystem 等公共服务
├── PlayerModel
├── PlayerSystem
└── Canvas
    ├── HudView              ← 属于 MainContext
    └── BossContext          ← 节点树内，自动识别 MainContext 为父级
        ├── BossModel        ← 覆盖父级同类型注册
        ├── BossSystem
        └── BossView
```

BossView 执行 Command 时用 BossContext，操作的是 BossModel；但 `CommandSystem` 这类公共服务没有在 BossContext 注册，框架会自动向父级容器查找。每个 Context 还有**独立的事件总线**，BossSystem 发出的事件只在 BossContext 内传播，不会影响外部。

```csharp
public class BossContext : MonoGameContextBase
{
    protected override void InstallBindings(ContainerBuilder builder)
    {
        // 只注册本上下文独有的服务，其余自动继承父级
    }
}
```

### 平行上下文

当两个模块需要完全隔离——不共享任何 Model 或事件——把它们的 Context 放在场景同一层级，各自作为根节点：

```
Scene
├── MainContext          ← GameContext.Main
│   └── ...
│
└── MiniGameContext      ← 根节点，无父级，数据完全独立
    └── ...
```

两个上下文的 Model 和事件总线互不影响。如果 MiniGameContext 里需要用到全局工具（比如 `IJsonUtility`），不需要重复注册——`inheritFromGlobal = true`（默认开启）会自动回退到 `GameContext.Main` 查找。

### 同序初始化保证

子 Context 和父 Context 同为 `MonoGameContextBase`，共享 `DefaultExecutionOrder(-1000)`，Unity 不保证它们的 Awake 先后顺序。

> **提示** — 这个细节由框架自动处理：子 Context 初始化时会递归确保父级先完成，无论场景里 Awake 的实际调度顺序如何。**你只需要按正确的 Hierarchy 层级摆放节点，不需要任何额外代码。**

---

## 11. 容器注册与解析规则

了解了多上下文之后，有必要清楚地知道容器在解析时精确地做了什么。

容器按**精确类型键**查找，不做继承扫描——查 `ISystem` 不会找到 `IPlayerSystem` 的注册，反之亦然。

### Mono 路径自动注册的键

`MonoModelBase` / `MonoSystemBase` / `MonoUtilityBase` Awake 时自动注册两类键：

| 键 | 是否注册 |
|---|---|
| 具体类型（如 `PlayerSystem`） | ✅ |
| 派生自层标记的接口（如 `IPlayerSystem`） | ✅ |
| 层标记接口本身（`ISystem` / `IModel` / `IUtility`） | ❌ |

```csharp
ctx.GetSystem<PlayerSystem>()    // ✅
ctx.GetSystem<IPlayerSystem>()   // ✅
ctx.GetSystem<ISystem>()         // ❌ 层标记本身不注册
```

### InstallBindings 手动注册

手动注册只写入你显式传入的 contracts，没有自动推导。注册了什么类型，就只能用什么类型解析：

```csharp
builder.RegisterValue(new JsonUtility(), typeof(IJsonUtility));
ctx.GetUtility<IJsonUtility>()  // ✅
ctx.GetUtility<JsonUtility>()   // ❌ 具体类型未注册
```

这与 Mono 路径不同——Mono 路径会同时注册具体类型和接口，而手动路径完全由你控制。如有需要可以手动补上具体类型，但通常调用方依赖接口就够了。

### 运行时动态注册

```csharp
ctx.RegisterModel(model);
ctx.UnregisterModel(model);
```

业务的合法注册通道只有 `RegisterModel/System/Utility` 这三对，以及构建期的 `InstallBindings(builder)`。

### Container 不对外暴露

`IGameContext` 接口故意**不暴露 `Container`**。`GameContext.Container` 和 `MonoGameContextBase.Container` 都是 `internal`，业务再也拿不到容器直接 `Container.RegisterFor<TLayer>(...)`。这是为了保证：

- 注册一定带上层标记（`IModel` / `ISystem` / `IUtility`），不会出现"挂了一个 Model 但没注册到 Model 层"的偏差；
- 解析路径、动态注册、生命周期都走同一组受控 API，便于审计与重构；
- 框架内部如果确实需要 Container（如 `MonoLayerExtensions` / `ContainerBuilder.SetParent`），通过 `internal` 的 `ContextInternals.GetContainer(ctx)` 取，业务程序集看不到。

> 想自定义"绕过层标记"的注册方式时，建议先想清楚是不是 Model/System/Utility 的概念偏差，而不是开个口子。

### 线程契约

容器是 **Unity 主线程独占** 的——`Resolve` / `TryResolve` / 工厂缓存 / 运行时 Register/Unregister 都不加锁。框架的 Awake/OnDestroy/Command/Event 全部在主线程跑，热路径不付并发开销；Editor / Development Build 下 `Container` 内部有 `Debug.Assert` 兜底，跨线程访问会输出 error 日志。

业务如果需要从工作线程调框架，请先 `await UniTask.SwitchToMainThread()` 再发 Command。

### 不可在运行时热替换层

框架**不支持**运行时删除/替换已注册的 Model/System/Utility 后让既有引用自动指向新实例——`[Inject]` 字段是 Awake/Execute 时一次性快照、R3 订阅绑定到具体 `ReactiveProperty` 实例，容器反注册不会重定向它们。

需要切换数据时，**改 Model 内部状态**（重置字段、清空集合）而不是 Destroy 整个 Model GameObject；需要整层换实例时，**Context 一并 Dispose 重建**（场景切换、关卡重置）。这条规则的详细推论与示例见 `Assets/Game/AGENTS.md §21`。

---

## 12. 纯代码上下文

前面所有示例都借助 Unity 的 MonoBehaviour 生命周期管理 Context。有时你需要更精确的控制——比如自动化测试、不依赖场景的工具模块，或者需要在代码里控制初始化时机。这时可以完全用代码创建和管理 Context：

```csharp
// 构建容器，注册服务
var builder = new ContainerBuilder();
builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));

// 创建 Context，inheritFromGlobal: false 表示完全自给自足
var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

// 注册并初始化 Model / System
var model  = new InventoryModel();
var system = new InventorySystem();
ctx.RegisterModel(model);
ctx.RegisterSystem(system);
ctx.Inject(system);    // 解析 [Inject] 字段
ctx.AttachTo(system);  // 回写 Context 引用，让 System 可以使用扩展方法

// 使用
ctx.ExecuteCommand(new AddGoldCommand(100));

// 生命周期由持有者负责，Dispose 级联取消所有异步操作
ctx.Dispose();
```

纯代码 Context 与场景中的 `MonoGameContextBase` 完全独立，不受 Hierarchy 层级影响。如果场景里的 View 需要使用它，直接持有引用调用即可。订阅生命周期可以手动 `new DisposableBag(ctx)`，享受和 `MonoViewBase.Bag` 一样的统一 API：

```csharp
public class MiniGameController : MonoBehaviour
{
    private GameContext _ctx;
    private DisposableBag _bag;

    private void Awake()
    {
        _ctx = BuildContext();
        _bag = new DisposableBag(_ctx);

        _bag.Subscribe(_btn.onClick, () => _ctx.ExecuteCommand(new StartGameCommand()));
        _bag.Subscribe(_ctx.GetModel<ScoreModel>().Value, s => _scoreText.text = s.ToString());
    }

    private void OnDestroy()
    {
        _bag?.Dispose();
        _ctx?.Dispose();
    }
}
```

> **选择路径** — 纯代码 Context 适合自动化测试（快速搭建隔离环境）、不依赖 Unity 场景的后台服务，或需要精确控制初始化时机的工具模块。大多数游戏功能仍推荐 Mono 路径——Unity 托管生命周期，Context 和各层节点在 Hierarchy 可见、可拖拽调整、可在 Inspector 实时查看状态。

---

## 13. AssetReference（资源引用）

框架通过 `IAssetUtility` 与 `AssetReference<T>` 提供统一资源入口。业务动态加载用 location；Inspector 拖拽引用用 `AssetReference<T>`。GUID 只保存在引用内部，不作为业务 API 暴露。

`MonoViewBase/MonoModelBase/MonoSystemBase/MonoUtilityBase` 内置 protected `Bag`——动态加载通过 `Bag.Load<T>(location)` / `Bag.LoadScene(...)` / `Bag.LoadText(...)` 等方法，handle 自动登记到 Bag，`OnDestroy` 时统一释放。`AssetReference<T>` 字段则自己持有 handle，并由宿主 `OnDestroy` 自动 `Dispose`。真实引用计数由具体资源 provider 维护，框架只管理“谁负责释放哪一类 handle”。

### 基础用法

```csharp
public class IconView : MonoViewBase
{
    [SerializeField] private AssetReference<Sprite> _iconRef;
    private Image _image;

    protected override async void Awake()
    {
        base.Awake();
        _image = GetComponent<Image>();

        var icon = await _iconRef.Get();
        if (icon != null) _image.sprite = icon;
    }
}
```

动态路径加载（在 MonoXxxBase 子类里）：

```csharp
var prefab = await Bag.Load<GameObject>("ui/panel_inventory");
var text   = await Bag.LoadText("config/items.json");
```

### API 一览

| 成员 | 说明 |
|---|---|
| `UniTask<T> Get()` / `Get(CancellationToken ct)` | 通过绑定的资源加载器异步加载并缓存；多次调用返回同一实例 |
| `bool TryGetAsset(out T asset)` | 非阻塞检查；仅当 `IsLoaded == true` 时返回 true，不会触发加载 |
| `T Asset` | 已加载的实例（未加载为 null） |
| `bool HasGuid` / `IsLoaded` / `IsLoading` / `float Progress` | 状态查询 |
| `void Bind(IAssetUtility utility, CancellationToken hostToken)` | 手动绑定资源加载器和宿主销毁信号，适用于纯 C# 或 ScriptableObject 场景 |
| `void Unload()` | 释放当前引用持有的内部加载结果，下次 `Get()` 会重新加载 |
| `void Dispose()` | 同 `Unload`（实现 `IDisposable`） |

### 并发与取消

- 同一个 `AssetReference<T>` 的多个 `Get()` 并发调用共享同一加载任务
- `AssetReference<T>` 自己持有并释放自己的资源 handle，不登记到 `Assets` 动态资源作用域
- 传入 `CancellationToken` 时，**只让当前调用方收到 `OperationCanceledException`**，底层加载继续，其他并发等待者不受影响
- 宿主销毁时会自动 Dispose 字段里的 `AssetReference<T>` 和 `AssetReferenceList<T>`，避免 View 销毁后忘记手动 `Unload`

```csharp
// 与 View 销毁绑定（推荐 — View 销毁后没必要继续加载）
var icon = await _iconRef.Get(this.GetCancellationTokenOnDestroy());
```

### 批量加载：AssetReferenceList

```csharp
[SerializeField] private AssetReferenceList<Sprite> _avatarSet;

var avatars = await _avatarSet.GetAll(this.GetCancellationTokenOnDestroy());  // T[]
_avatarSet.UnloadAll();                                                       // 一并释放
```

`GetAll` 并行触发底层加载（遵守资源 provider 配置的并发上限）；单项失败时对应位置为 null，不影响其他项。

### 下载进度

下载不再通过 listener 注册回调，而是状态流：

```csharp
var downloader = Bag.CreateTagDownloader("level1");
Bag.Subscribe(downloader.Progress, report => _progressBar.value = report.Progress);
await downloader.Download(this.GetCancellationTokenOnDestroy());
```

下载器是创建时的快照：清缓存或切版本后要重新 `CreateTagDownloader` / `CreateAllDownloader` / `CreateLocationDownloader` 才会重新统计。单文件失败由配置里的 `FailedTryAgain` 自动重试；整体最终失败时 `Download()` 抛异常，业务用 `try/catch` 接住并重新创建下载器再下，已成功分片会走缓存跳过。

### 初始化、缓存与卸载

`AssetSystemConfigModel` + `AssetUtility` + `AssetInitSystem` 挂在同一 Context 节点。所有包都登记在 `AssetSystemConfigModel.Packages` 列表里，每个包各有「自动初始化」开关：开则启动即拉清单；关则启动不碰它的网络（DLC 懒加载 / 隐私同意 / 选区前不联网的合规启动），业务在合适时机显式调用冷启动它：

```csharp
await this.GetUtility<IAssetUtility>().Initialize();          // 默认包
await this.GetUtility<IAssetUtility>().Initialize("DlcPack"); // 指定包
```

> ⚠ 既没开自动初始化、也没 `Initialize` 过的包，`Load` 它会**直接抛**「未初始化」异常（fail-fast，不是无限等待）——要加载的包要么开自动初始化、要么先 `Initialize`。

资源释放分三层，别混用：

| 操作 | 清理对象 | 常见时机 |
|---|---|---|
| `Unload()` / `Dispose()` / `Bag.Dispose()` | 释放 handle，让 bundle 引用计数归零 | 关闭界面 / 离开功能 |
| `UnloadUnusedAssets()` | 卸载内存中引用归零的 bundle | 场景切换 / 关卡结束 |
| `ClearCache(...)` / `ClearCacheByTags(...)` / `ClearCacheByLocations(...)` | 删除磁盘上的已下载 bundle 缓存 | 强制重下 / 热更后省空间 / 卸 DLC 缓存 |

Host 模式默认允许 `Load` 对未缓存 bundle 当场按需下载。大型 DLC 若不想“误 Load 一个资源就自动下载”，在 `AssetSystemConfigModel.Packages` 列表里取消该包的「启用按需下载」：之后本包未缓存资源的 `Load` 直接失败，业务必须先用下载器显式预下载并展示进度。包级策略（自动初始化 / 启用按需下载）都在这一处按包配置。

### Inspector 行为

- 拖入与字段类型匹配的资源：自动记录 GUID
- 拖入 `MonoBehaviour` 给 `AssetReference<GameObject>`：自动取所在 GameObject
- 拖入 `Texture2D` 给 `AssetReference<Sprite>`：自动取出 Sprite 子资产
- 资源被删除：Inspector 显示 `Missing (T)`，运行时 `HasGuid` 仍为 true，但 `Get()` 报错
- GUID 跟随资源移动，与路径无关——重命名/移动文件不会断链

---

## 14. 数据流：异步原语的统一抽象

### 设计哲学

回头看第 1 章 §"重新理解数据"提出的二分：**可观察 vs 不可观察**、**有状态 vs 瞬时**。事件被划归"瞬时可观察数据"——它和 Model 中的 `ReactiveProperty` 在框架的世界观里**同属数据层**，只是一个有当前值、一个没有。

把这条线推下去，会发现一件有趣的事：

```
ReactiveProperty<T>   —— 有状态的值，会推送变化
事件 (IEvent)          —— 瞬时值，每次发生推送一次
C# event / UnityEvent —— 同样是"在某些时刻产生值"
UniTask / Task         —— 一次性的值（成功或失败）
协程 / Coroutine        —— 按帧产生 yield 值，结束时完成
按帧轮询、定时器、网络包……一切随时间发生的东西
```

它们看上去五花八门、由不同库提供，但**本质都是"在时间线上产生值的流"**——只是发生次数（一次 / 多次）、是否携带状态、是否有完成时刻不同。一旦统一为 `Observable<T>`，框架的所有工具（`Bag` 生命周期、R3 操作符）就能作用于它们。

这也回到了框架的核心理念：**单向数据流**。View 不主动拉数据，而是订阅"会到来的值"。把异步、事件、属性、用户输入都看成"会到来的值"，View 的代码就只需要一种范式。

### 一切皆可成流

下面这张表列出常见原语和它们到 `Observable<T>` 的桥梁。具体 API 名称查 R3 文档（这里只示意思路）：

| 来源 | 转 Observable 的方式 |
|---|---|
| `ReactiveProperty<T>` | 本身就是 `Observable<T>`，直接订阅 |
| Framework `IEvent` | `this.OnEvent<TEvent>()` —— 框架扩展，返回 `Observable<TEvent>`，可链 R3 操作符 |
| C# `event` / `Action` | `Observable.FromEvent` |
| `UnityEvent` / `UnityEvent<T>` | R3 已提供 `.AsObservable()` 扩展 |
| `Button.onClick` / `Slider.onValueChanged` 等 UnityUI | R3 已提供 `OnClickAsObservable()` / `OnValueChangedAsObservable()` |
| `UniTask` / `Task` | `.ToObservable()` —— 单元素流（成功推一次 + OnCompleted；失败 OnError） |
| Coroutine | `UniTask.ToCoroutine` / `UniTask.FromCoroutine` 互转，再 `ToObservable()` |
| 按帧 / 每秒 | `Observable.EveryUpdate()` / `Observable.Interval(...)` / `Observable.Timer(...)` |
| 手动控制 | `Subject<T>` —— 既能 OnNext 也能订阅 |

> **统一原则**：所有源都能转成 `Observable<T>`，之后 `Bag.Subscribe(Observable<T>, Action<T>)` 是唯一的订阅入口。
> 简单订阅用 `Bag.Subscribe<T>(handler)` / `Bag.Subscribe(unityEvent, handler)` 等便利重载；
> 一旦需要"带初值订阅 / 过滤 / 节流 / 组合"等操作，把源转 Observable 后链 R3 操作符（`Prepend` / `Where` / `Throttle` / `CombineLatest`），不再追加新重载。

反向同样成立：

| 从 Observable 到 | 方式 |
|---|---|
| `UniTask<T>` | `.ToUniTask()` 取首个值或终止值；用 `await observable.FirstAsync()` 也行 |
| 协程 | 转 `UniTask` 后 `.ToCoroutine()` |

**有了这些桥梁，"事件流就是数据流"不再是口号——一段 LINQ 风格的表达式可以横跨 UI 点击、网络回调、Model 变化、定时心跳。**

### R3 类型职责速查

| 类型 | 作用 | 关键点 |
|---|---|---|
| `Observable<T>` | 只读流抽象 | 只保证可订阅，不保证当前值；适合事件流、派生流、异步流 |
| `ReactiveProperty<T>` | 可写状态流 | 有 `Value` 和 `CurrentValue`；只应在 Model/System 内部写入 |
| `SerializableReactiveProperty<T>` | Unity 序列化状态流 | 推荐用于 Mono Model，Inspector 可见，便于 Demo/调试/配置初始值 |
| `ReadOnlyReactiveProperty<T>` | 只读状态流 | 有 `CurrentValue`，无 `Value` setter；推荐作为 Command 返回给 View 的状态类型 |
| `ISubject<T>` / `Subject<T>` | 手动推送源 | 既是观察者又是可订阅源，适合桥接外部回调；长期状态仍优先 ReactiveProperty |
| `Observer<T>` | 订阅端处理器 | 需要处理错误/完成时用 `Observer.Create`，普通 UI 更新用 lambda 即可 |

### 用操作符表达派生状态

`Bag.Subscribe(Observable<T>, Action<T>)` 接受任意 `Observable<T>`，所以**任何 R3 链式表达式可以直接传入**，订阅由 `Bag` 自动追踪生命周期。下面是几种常见组合。

#### 过滤：条件触发

避免在 handler 内堆 `if`：

```csharp
// HP 低于 20% 时显示红屏警告
Bag.Subscribe(
    model.HP.Where(hp => hp < model.MaxHP.Value * 0.2f),
    _ => ShowLowHpVignette());
```

#### 变换：从一个值推导显示

```csharp
// 分数大于 9999 显示 "9999+"
Bag.Subscribe(
    model.Score.Select(s => s > 9999 ? "9999+" : s.ToString()),
    text => _scoreText.text = text);
```

#### 跳过初始推送

`ReactiveProperty` 订阅时会立即推送当前值。只要后续变化时：

```csharp
// 初始 100 HP 不算"掉血"，只有真正变化才播放受击动画
Bag.Subscribe(model.HP.Skip(1), _ => PlayHurtAnimation());
```

#### 节流与防抖

输入框搜索、滑条拖动等高频更新降频：

```csharp
// 玩家停止输入 300ms 后才触发搜索
Bag.Subscribe(
    model.SearchText.Debounce(TimeSpan.FromMilliseconds(300)),
    query => _searchSystem.Search(query));

// 高频信号每秒最多触发一次（取窗口内最后一次）
Bag.Subscribe(
    networkSignal.ThrottleLast(TimeSpan.FromSeconds(1)),
    OnSignal);
```

#### 组合多个源：派生状态

```csharp
// 血条比率：HP 或 MaxHP 任一变化都重算
var ratio = model.HP.CombineLatest(
    model.MaxHP,
    (hp, max) => max > 0 ? (float)hp / max : 0f);

Bag.Subscribe(ratio, r => _hpBar.fillAmount = r);
```

`CombineLatest` 在每个源都至少推送一次后才发出，之后任一变化都带最新组合触发——把"派生状态"的同步逻辑从手写 if 里解放出来。

#### 一次性触发

```csharp
// 第一次升级到 10 级时弹奖励，后续不再触发
Bag.Subscribe(
    model.Level.Where(lv => lv >= 10).Take(1),
    _ => ShowLevel10Reward());
```

`Take(1)` 取首个值后自动完成，订阅自动释放（`Bag` 同步清理）。

#### 缓冲与聚合

```csharp
// 每 5 次伤害聚合一次做总伤害弹字
Bag.Subscribe(
    damageStream.Buffer(5),
    list => ShowComboDamage(list.Sum()));
```

### 跨原语组合的例子

理论说完，看几个实际把多种原语连起来的场景。

**例 1：按钮点击 + 防抖触发异步加载**

UnityEvent → Observable → Throttle → 异步 Command：

```csharp
// 1 秒内多次点击只触发一次保存
Bag.Subscribe(
    _saveBtn.OnClickAsObservable().ThrottleFirst(TimeSpan.FromSeconds(1)),
    async _ => await this.ExecuteCommandAsync(new SaveProgressCommand()));
```

**例 2：合并 Framework Event 与 Model 变化**

业务里"血条要不要闪红"取决于"刚受伤" + "当前 HP 低"两个条件：

```csharp
// OnEvent<T>() 把 Framework Event 桥接为 Observable<T>，省去 FromEvent 样板
// 受伤且当前血量低于阈值时闪红
Bag.Subscribe(
    this.OnEvent<PlayerHurtEvent>()
        .WithLatestFrom(_model.HP, (_, hp) => hp)
        .Where(hp => hp < 30),
    _ => FlashRedHpBar());
```

**例 3：协程 + UniTask + 流**

老代码里的协程函数包成 UniTask，转成 Observable，加入操作符链：

```csharp
// 旧协程：LoadLevelCoroutine() 完成时表示关卡加载完
var loadDone = UniTask.FromCoroutine(LoadLevelCoroutine).ToObservable();

// 加载完成 + 玩家按确认后，进入战斗
Bag.Subscribe(
    loadDone.SelectMany(_ => _confirmBtn.OnClickAsObservable().Take(1)),
    _ => EnterBattle());
```

### 错误处理

R3 默认把订阅链中的异常抛到 `UniTaskScheduler.UnobservedTaskException`。需要在订阅点处理时，用 `Observer.Create` 自定义：

```csharp
Bag.Subscribe(
    riskySource,
    Observer.Create<int>(
        onNext: HandleValue,
        onErrorResume: ex => Debug.LogError($"流出错: {ex}"),
        onCompleted: _ => Cleanup()));
```

### 回到框架理念

为什么这套抽象在框架里特别顺手？因为它把第 1 章描述的"View 只观察、不写入"自然展开成一种编程范式：

```
任何来源的值（Model / Event / 异步 / 用户输入 / 时间）
            ↓
       Observable<T>
            ↓
   LINQ 风格操作符（过滤、变换、组合、节流）
            ↓
        Bag.Subscribe
            ↓
       UI 副作用 / 派生写入
```

数据从源头流向 UI，中途用声明式表达式加工，View 写起来更接近"描述关心什么"，而不是"在某时刻怎么操作"。

> **要点回顾**
>
> - 异步、事件、属性、协程、UnityEvent 都能用 `Observable<T>` 统一表达
> - 一旦进入 Observable 范畴，LINQ 风格操作符（Where / Select / CombineLatest / Throttle / Buffer ...）皆可用
> - `Bag.Subscribe` 接受任意 `Observable<T>`，链式表达自动获得生命周期管理
> - 用操作符表达派生状态，比在 handler 里堆状态机简洁数倍
> - 这套抽象不是 R3 的，是"数据流"思想的延伸——它和框架"单向数据流"的理念是同一件事
