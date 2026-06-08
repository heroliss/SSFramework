# Demo 章节文案（Source of Truth）

> **本文件是 Demo 章节文案的源稿**。Demo 运行时读取的是 `Assets/Game/Framework/Demo/Res/DemoCopy.asset`
> （ScriptableObject），但 markdown 在 git 里 diff 更清晰、PR review 更友好，因此采取双轨：
> - **修改流程**：先改这里 → 用 MCP 同步到 `DemoCopy.asset`（或 Inspector 手动同步）
> - **新增章节**：在下面追加一节 `## 章节 N — Id: xxx`，按现有格式填齐 5 段（标题、一句话核心、Body、设计考量、Code Snippet），然后在底部章节顺序表里登记
> - **章节 Id 约定**：稳定 kebab-case 字符串。一旦发布不要改，避免 SO 引用断链；如需重命名，先改 SO 再改这里
>
> 现状：7 主章节，每章 30-90 秒入门量。

---

## 章节 1 — Id: `architecture`

**标题**：分层架构：减少耦合，清晰职责

**一句话核心**：把代码分成五层，每层只关心自己——这是框架的根本目的；单向数据流和权限分层只是实现手段。

**Body**：
点击中央的 "Click Me" → 看四个色块按 View → Command → System → Model 顺序点亮，旁边 status 文本同步播报当前数据在哪一层。最后 Model 数字 +1，View 的 Count 文本立即同步。

整个动画演示一次完整的"单向数据流"：View 行为触发 IncrementCommand，Command 调 ICounterSystem.Increment()，System 写 CounterModel.Count，RP 自动通知订阅者刷新 View。读源码时注意 View 里没有 `[Inject] Model/System`、没有 `SendEvent`、也没有 `GetSystem<>()`——只发 Command、只订阅查询命令返回的 RP。

**设计考量**：传统 MVC 里 Controller 是一个矛盾的存在——既响应输入又协调数据，随项目增长必然变成垃圾桶。本框架把 Controller 拆成 **Command（声明意图）+ System（实现意图）**——视图开发者定义 Command 接口、逻辑开发者实现 System，两边通过 Command 类型对接、互不耦合。"分层"由几条具体手段保障：**单向数据流**（View 只发 Command）、**权限接口**（编译期约束）、**面向接口**（依赖抽象不依赖实现）——这些都是手段，目的是把"职责清晰"从纸面落到代码里。

**Code Snippet 1 - caption**：分层与数据流向
```
       调用方向：写入数据
View ────────→ Command ────────→ System ────────→ Model
                                                    │
View ←──────── Command ←──────── System ←──────── Model
       数据方向：读取 / 订阅
```

**Code Snippet 2 - caption**：分层带来的好处——替换实现而不动业务代码
```csharp
// 测试时用 MockPlayerSystem 替换真实的 PlayerSystem，
// View / Command 代码完全不变，因为它们只依赖 IPlayerSystem 接口
public class TestContext : MonoGameContextBase
{
    protected override void InstallBindings(ContainerBuilder builder)
    {
        builder.RegisterValue(new MockPlayerSystem(), typeof(IPlayerSystem));
        builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
    }
}
```

---

## 章节 2 — Id: `minimal-counter`

**标题**：最小可运行示例：Counter

**一句话核心**：30 行代码走通 Model + Command + System + View，亲眼看到分层的形状。

**Body**：
三个按钮（+ / − / Reset）各对应一个 struct Command：
- `+` → `IncrementCommand` → Count + 1
- `−` → `DecrementCommand` → Count − 1
- `Reset` → `ResetCounterCommand` → Count = 0

页面有两行文本：上方 Count 订阅 `GetCountStateCommand` 返回的 RP，下方 Commands 累计执行次数——每次任意按钮都会让两行都更新一次，证明 Command 是唯一写入入口。

在 Inspector 里展开 CounterModel 节点，运行时可以看到 RP&lt;int&gt; Count 的 Value 实时跳变。

**设计考量**：让"分层"成本足够低——`MonoModelBase / MonoSystemBase / MonoViewBase` 这些基类已经替你做了 Context 查找、容器注册、依赖注入、Bag 释放等所有样板。业务代码就剩"我这层在干什么"。

**Code Snippet 1 - caption**：完整 Counter（一屏内看完）
```csharp
// Model：状态载体
public class CounterModel : MonoModelBase
{
    [field: SerializeField] public RP<int> Count { get; private set; } = new(0);
}

// System：业务规则，唯一合法的状态写入者
public interface ICounterSystem : ISystem
{
    ReadOnlyReactiveProperty<int> Count { get; }
    void Increment();
}
public class CounterSystem : MonoSystemBase, ICounterSystem
{
    [Inject] private CounterModel _model;
    ReadOnlyReactiveProperty<int> ICounterSystem.Count => _model.Count;
    public void Increment() => _model.Count.Value++;
}

// Command：用户意图（推荐 struct，零分配）
public readonly struct IncrementCommand : ICommand
{
    public void Execute(ICommandContext ctx) => ctx.GetSystem<ICounterSystem>().Increment();
}
public readonly struct GetCountCommand : ICommand<ReadOnlyReactiveProperty<int>>
{
    public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx)
        => ctx.GetSystem<ICounterSystem>().Count;
}

// View：只发 Command、只订阅状态
public class CounterView : MonoViewBase
{
    [SerializeField] private Button _btn;
    [SerializeField] private TMP_Text _label;
    protected override void Awake()
    {
        base.Awake();
        Bag.Subscribe(this.ExecuteCommand(new GetCountCommand()), v => _label.text = v.ToString());
        Bag.Subscribe(_btn.onClick, () => this.ExecuteCommand(new IncrementCommand()));
    }
}
```

---

## 章节 3 — Id: `lifetime-bag`

**标题**：亮点 (1)：统一生命周期，一切皆 IDisposable

**一句话核心**：订阅、资源、嵌套作用域……所有"有生命的东西"都进 Bag，OnDestroy 一次性释放。

**Body**：
按顺序操作：
1. 点 "Spawn" 3 次 → 列表里出现 3 个子 View，每个都显示 "Subs: 0"。每个子 View 在 Awake 里 `Bag.Subscribe<LogEvent>` 订阅了事件，但代码里没有一行反订阅。
2. 点 "Send Ping" → 三个子 View 计数同时 +1，证明都收到了 ping。
3. 点 "Destroy 一个" → 最后一个子 View 销毁，它的 Bag 自动 Dispose。
4. 再点 "Send Ping" → 只有剩下的两个 +1；被销毁的那个再也不会响应。

整个流程没有写过 `-=` / `Unsubscribe` / `Dispose`——一切订阅生命周期都跟着宿主 GameObject。把这个心智推广到 R3 订阅、Framework Event、UnityEvent、资源加载 handle、任意 `IDisposable`：一律 `Bag.Add` / `Bag.Subscribe`，宿主 OnDestroy 时统一释放。

**设计考量**：为什么选 `IDisposable` 作为统一抽象？
- **.NET 官方标准**：`using` 语句、`IAsyncDisposable`、`CancellationTokenSource`、`HttpClient`……所有 .NET 资源管理都基于 `IDisposable`，跨库可组合。
- **第三方库通用契约**：R3 的订阅返回 `IDisposable`、UniTask 的 `CancellationTokenRegistration` 也是。这意味着框架不发明新概念，业务代码可以无缝接入任何 R3/UniTask/其他库的输出——直接 `Bag.Add(anyDisposable)` 就行。
- **避免心智割裂**：如果"订阅释放"和"资源释放"用两套 API，业务要在两个心智模型间切换；统一为 IDisposable 后，记一句"放进 Bag"即可。

**Code Snippet 1 - caption**：一个 Bag 管所有
```csharp
public class HudView : MonoViewBase
{
    [SerializeField] private TMP_Text _hpText;
    [SerializeField] private Button _hitBtn;
    [SerializeField] private AssetReference<Sprite> _avatarRef;

    protected override async void Awake()
    {
        base.Awake();

        // R3 响应式订阅
        var hp = this.ExecuteCommand(new GetHPStateCommand());
        Bag.Subscribe(hp, v => _hpText.text = v.ToString());

        // Framework Event（带数据 / 无数据 / 订阅时初始化）
        Bag.Subscribe<PlayerHurtEvent>(e => PlayHurtAnim());
        Bag.Subscribe<GoldChangedEvent>(RefreshGold, invokeImmediately: true);

        // UnityEvent
        Bag.Subscribe(_hitBtn.onClick, () => this.ExecuteCommand(new HitCommand()));

        // 资源加载——handle 自动入 Bag
        var icon = await Bag.Load<Sprite>("ui/icon");

        // 任何第三方 IDisposable（这里举 R3 Disposable.Create 为例）
        Bag.Add(Disposable.Create(() => Debug.Log("HudView 销毁了")));
    }
    // 不写 OnDestroy。MonoViewBase.OnDestroy 自动 Dispose Bag。
}
```

**Code Snippet 2 - caption**：嵌套作用域 — 一回合的临时订阅
```csharp
public class BattleView : MonoViewBase
{
    private DisposableBag _roundBag;

    public void OnRoundStart()
    {
        _roundBag = Bag.CreateChild();           // 父级 Bag 的子作用域
        var targeting = this.ExecuteCommand(new GetTargetingStateCommand());
        _roundBag.Subscribe(targeting, ShowTargetMark);
    }

    public void OnRoundEnd() => _roundBag?.Dispose();  // 父级不动，仅清这一回合
}
```

---

## 章节 4 — Id: `r3-streams`

**标题**：亮点 (2)：数据二维划分 + R3 整合

**一句话核心**：用"有/无当前值 × 可观察/不可观察"两个维度统一理解数据；R3 把所有数据源转成 Observable，一套 API 处理。

**Body**：
拖动 Slider A 和 Slider B，观察三个文本同步变化：
- **Sum** = A + B，每次拖动都立即重算（`a.CombineLatest(b, (x,y) => x+y)`）
- **Throttled Sum** 在你停止拖动 500ms 后才更新一次（`sum.ThrottleLast(500ms)`）
- **Max** = `Mathf.Max(A, B)`，同样实时

整段 View 代码没有 Update、没有手写状态机——只有 5 行 R3 链式表达：`onValueChanged.AsObservable().Prepend(currentValue)` 把 Slider 事件转成 Observable 并补一个初值，下游全部用 R3 操作符派生。

`Bag.Subscribe(Observable<T>, Action<T>)` 是统一的订阅入口；事件、状态、UniTask、UnityEvent 都能转成 `Observable<T>`，配合 R3 操作符（`Where` / `Throttle` / `CombineLatest` / `Skip` / `Buffer`…）即可声明式表达派生关系。

**设计考量**：选 R3 而不是 UniRx 或自造响应式系统——R3 是 UniRx 作者重新设计的零分配响应式库，性能更好、API 更现代、与 UniTask 同生态。框架不发明"自家响应式概念"，业务代码可以直接享受 R3 全套操作符和社区生态。

**Code Snippet 1 - caption**：跨原语组合 — 防抖按钮 + 异步保存
```csharp
// UnityEvent → Observable → Throttle → 异步 Command，全程声明式
Bag.Subscribe(
    _saveBtn.OnClickAsObservable().ThrottleFirst(TimeSpan.FromSeconds(1)),
    async _ => await this.ExecuteCommandAsync(new SaveProgressCommand()));
```

**Code Snippet 2 - caption**：派生状态 — 血量比例
```csharp
// HP 或 MaxHP 任一变化都重算 ratio，UI 不用关心"什么时候重算"
var ratio = model.HP.CombineLatest(
    model.MaxHP,
    (hp, max) => max > 0 ? (float)hp / max : 0f);

Bag.Subscribe(ratio, r => _hpBar.fillAmount = r);
```

**Code Snippet 3 - caption**：异步 Command 自动取消
```csharp
public readonly struct SaveProgressCommand : IAsyncCommand
{
    public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
    {
        await ctx.GetSystem<ISaveSystem>().WriteAsync(cancellationToken);   // 唯一要关心的 token
    }
}

// View 销毁、Context Dispose、外部 cancel 任一触发都会取消
await this.ExecuteCommandAsync(new SaveProgressCommand());
```

---

## 章节 5 — Id: `mono-power`

**标题**：亮点 (3)：与 Unity 引擎深度融合

**一句话核心**：Hierarchy 表达 DI 树、Inspector 实时看 RP\<T\>、引擎组件可跨层——Unity 不是绊脚石而是放大器。

**Body**：
本 Page 在 Hierarchy 下挂了两个并列子 Context：**SubContextA** 和 **SubContextB**，各自有独立的 CounterModel + CounterSystem 节点。

操作：
- 点 "A +" → 只有 A 的 Count 跳变；B 不变。
- 点 "B +" → 只有 B 的 Count 跳变；A 不变。
- 点 "Reset Both" → A 和 B 都归零。

三个按钮调用的都是同样的 `IncrementCommand` / `ResetCounterCommand` struct，区别只在 `_contextA.ExecuteCommand(...)` vs `_contextB.ExecuteCommand(...)`。Container 按调用入口的 Context 路由到该子 Context 内的 Model 实例——Hierarchy 父子关系即作用域，调整作用域只需要在 Inspector 里拖动节点。

运行时展开 SubContextA → CounterModel 节点，Inspector 直接看到 RP&lt;int&gt; Count 的实时值——这就是 Mono 深度融合的两个核心：**Hierarchy = DI 树**、**Inspector = 调试器**。

**设计考量**：传统 DI 容器在 Unity 里水土不服——它们假设依赖关系在代码里声明，但游戏开发中 GameObject 层级才是最自然的"作用域"。本框架反过来拥抱 Unity：你已经在拖 GameObject 了，那就用拖动的结果当 DI 关系。这让"调整作用域"从改代码降级为拖动节点，可视化调试也变得不需要专门的调试工具——Inspector 就是。

**Code Snippet 1 - caption**：Hierarchy 直接表达 Context 层级
```
Scene
├── MainContext (MonoGlobalContext)         ← 全局服务：Audio / Save / Config
│   ├── PlayerModel
│   ├── PlayerSystem
│   └── Canvas
│       ├── HudView                         ← 属于 MainContext
│       └── BossContext (MonoGameContextBase) ← 节点树内，自动识别 MainContext 为父级
│           ├── BossModel                   ← 覆盖父级同型注册
│           ├── BossSystem
│           └── BossView                    ← 操作 BossModel；CommandSystem 从父级继承
│
└── MiniGameContext (MonoGlobalContext sibling) ← 平行 Context，完全独立
    └── ...
```

**Code Snippet 2 - caption**：引擎组件作为 Model 字段（跨层）
```csharp
// Model 直接持有 Rigidbody，把它当作物理状态
public class ProjectileModel : MonoModelBase
{
    [field: SerializeField] public Rigidbody Body  { get; private set; }
    [field: SerializeField] public RP<float> Damage { get; private set; } = new(10f);
}

// System 直接写入引擎组件，效果等价于改 Model 字段
public class ProjectileSystem : MonoSystemBase
{
    [Inject] private ProjectileModel _model;
    public void Launch(Vector3 dir, float speed)
        => _model.Body.AddForce(dir * speed, ForceMode.Impulse);
}

// View 什么都不用写——Unity 渲染管线每帧自动把 Body 的 transform 画出来
```

---

## 章节 6 — Id: `asset-system`

**标题**：亮点 (4)：资源管理 — AssetReference + Bag.Load

**一句话核心**：Inspector 拖拽用 `AssetReference<T>`，动态路径用 `Bag.Load<T>`——所有句柄统一交 Bag 释放。

**Body**：
本 Demo Context 没有配 AssetSystemConfigModel + AssetUtility + AssetInitSystem 三件套，所以这一页主要演示"无资源系统时框架的安全降级"：

- 点 "Load Static"：未拖资源时 status 提示 `AssetReference 未配置`；即使拖了资源，因为没有 utility，框架会输出一条 warning 并跳过，**不抛异常**（运行库的 `AssetReferenceBinder` 走 TryResolve 路径）。
- 点 "Load Dynamic"：`Bag.Load` 在初始化等待时找不到 utility，会以可读异常退出，status 显示 `[FAIL] ...`。Page 不崩。
- 点 "Clear"：把当前 sprite 清空；Bag 内已有的 handle 仍持有，宿主 OnDestroy 时统一释放。

想看完整成功路径：在 NewDemoContext 节点下加 AssetSystemConfigModel + AssetUtility + AssetInitSystem 三件套，配置 YooAsset 包，再进这一页——拖拽路径会自动加载并显示 sprite，动态路径会按 location 加载，所有 handle 都登记到 Bag 跟着 View 走。

**设计考量**：为什么不直接用 YooAsset 的 API？因为：
- **隔离底层换库代价**：业务代码不直接依赖 YooAsset，未来换 Addressables 或自研只需重写 `IAssetProvider`。
- **接入框架生命周期**：业务用 YooAsset 原生 API 自己管 handle 释放，等于在框架之外开了一条不受 Bag 约束的生命周期通道。`AssetReference<T>` + `Bag.Load` 让所有资源都在 Bag 体系内，永远不会忘释放。

**Code Snippet 1 - caption**：Inspector 拖拽 + 动态路径混用
```csharp
public class IconView : MonoViewBase
{
    [SerializeField] private AssetReference<Sprite> _iconRef;
    [SerializeField] private Image _image;

    protected override async void Awake()
    {
        base.Awake();
        // 拖拽引用：宿主 OnDestroy 时自动 Dispose
        _image.sprite = await _iconRef.Get();

        // 动态路径：handle 自动登记到 Bag
        var prefab = await Bag.Load<GameObject>("ui/panel_inventory");
        Instantiate(prefab, transform);
    }
}
```

**Code Snippet 2 - caption**：下载进度作为响应式流
```csharp
var downloader = Bag.CreateTagDownloader("level1");
Bag.Subscribe(downloader.Progress, r => _progressBar.value = r.Progress);
await downloader.Download(this.GetCancellationTokenOnDestroy());
```

---

## 章节 7 — Id: `bootstrap-practice`

**标题**：工程实践：启动流程 + 目录结构 + 配置

**一句话核心**：怎么把项目搭起来——MonoGlobalContext 作为根、ScriptExecutionOrder 自动排好、推荐目录 + 必要配置一览。

**Body**：
点 "Refresh"，文本区列出当前 Context 能解析的层：
- `[OK] CounterModel` / `[OK] ICounterSystem` / `[OK] IFormatterUtility` / `[OK] ICommandSystem` ——四类层默认配置全部命中，证明 InstallBindings + MonoXxxBase 自动注册都已生效。
- 想看 [MISS]？在 Inspector 的 `_targets` 里追加一个不存在的类型名（如 `Game.Framework.Demo.Model.MissingModel`），或填一个未注册的接口（如 `Game.Framework.View.MonoViewBase` —— View 不进容器），再点 Refresh。

代码层面这一页演示两件事：(1) InstallBindings + MonoXxxBase 实际把什么放进了 Container；(2) 跨程序集类型查找的兜底——`Type.GetType` 命中失败时会回落到 `AppDomain.GetAssemblies()` 全量扫描，所以 `_targets` 里的字符串只写类型全名即可，不依赖程序集名。

更深入的启动流程（ExecutionOrder / 推荐目录 / 配置文件清单）见 `docs/framework-guide.md`。

**设计考量**：为什么不用单独的 `[InitializeOnLoadMethod]` 或 `RuntimeInitializeOnLoadMethod`？因为它们都是"全局静态初始化"，无法表达"作用域"。本框架坚持"每个 Context 都是独立单元"——单元测试时 new 一个 Context 不污染全局、跨场景切换时旧 Context Dispose 干净——所有初始化逻辑都跟 Context 走，没有隐藏全局状态。

**Code Snippet 1 - caption**：最小可运行的 MonoGlobalContext
```csharp
public class GameContext : MonoGlobalContext
{
    protected override void InstallBindings(ContainerBuilder builder)
    {
        // 必装：CommandSystem
        builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));

        // 项目通用服务在此注册（纯 C# 路径）
        builder.RegisterValue(new JsonUtility(),    typeof(IJsonUtility));
        builder.RegisterFactory(c => new AudioMixer(), typeof(IAudioMixer));
    }
}
```

**Code Snippet 2 - caption**：启动场景的 Hierarchy 模板
```
GameRoot                      (MonoGlobalContext)
├── AssetUtility              (MonoUtility, IAssetUtility)
├── AssetSettings             (MonoModel, AssetSystemConfigModel — 在 Inspector 配 CDN / 包名)
├── AssetInitSystem           (MonoSystem)
├── BootstrapView             (MonoView — 等待 IAssetUtility.InitState=Ready，加载首关)
└── (DontDestroyOnLoad 保留)
```

---

# 章节顺序定稿（v3）

| # | Id | 类型 | 主题 |
|---|---|---|---|
| 1 | `architecture` | 核心理念 | 分层架构：减少耦合、清晰职责 |
| 2 | `minimal-counter` | 核心理念 | 最小可运行 Counter |
| 3 | `lifetime-bag` | 亮点 (1) | 统一生命周期：Bag + IDisposable 通用标准 |
| 4 | `r3-streams` | 亮点 (2) | 数据二维划分 + R3 整合（含 RP 类型族、异步 Command） |
| 5 | `mono-power` | 亮点 (3) | Mono 深度融合 |
| 6 | `asset-system` | 亮点 (4) | 资源管理 |
| 7 | `bootstrap-practice` | 工程实践 | 启动流程 + 目录结构 + 配置文件 |

下一步等你审稿确认。
- "通过" → 我用 MCP 把这些内容写到 SO 资产 + 老 Demo 迁移 + 重建场景骨架
- 想改某章/某段 → 告诉我哪里
