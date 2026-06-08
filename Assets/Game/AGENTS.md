# Game 框架使用规则

本文件记录使用 `Assets/Game/Framework/` 框架 API 时的核心约束。AI Agent 在 `Assets/Game/` 下任意目录工作时会自动加载本文件（目录就近性）。

框架**内部**编码规则（仅修改框架源码时适用）见 `Assets/Game/Framework/AGENTS.md`。

## 1. IGameContext vs IHasGameContext

> **术语口径**：对象统一叫 **Context / 上下文**（`GameContext` 实例 = 能力环境）；**作用域 / scope** 只描述「生命周期 / 解析边界」——Context 之间构成**作用域树**做解析回退、`Bag.CreateChild()` 开**更短的作用域**。别把某个 Context 实例直接叫成「作用域」。

- `IGameContext` 是完整上下文能力接口，包含 Container/Inject/Resolve/GetXxx/RegisterXxx/Event/Command/CancellationToken 等能力。
- `GameContext` 直接实现 `IGameContext`。
- `MonoGameContextBase` 也实现 `IGameContext`，作为场景 Mono 代理转发到内部真实 `GameContext`。
- `IHasGameContext.Context` 返回 `IGameContext`，业务对象统一持有接口，不再依赖具体 `GameContext`。
- `MonoGameContextBase.RawContext` 仅用于必须拿具体 `GameContext` 的边界（如 `GameContext.Main`）。

### 1.1 层不应该直接拿到 `IGameContext`（除 struct Command）

`MonoViewBase/MonoModelBase/MonoSystemBase/MonoUtilityBase` 的 `IHasGameContext.Context` 是**显式接口实现**，业务子类内部**无法**写 `Context.GetModel<>()` / `Context.SendEvent(...)` 之类的代码。
**Why:** 拿到完整 `IGameContext` 就能 Container/Inject/RegisterXxx，绕过 `ICanGetModel`/`ICanSendEvent` 等权限接口，导致 View 也能改 Container、Model 也能 SendEvent。把 Context 隐藏后，每层只能用扩展方法（`this.GetModel<T>()` / `this.SendEvent<T>()` 等），编译期由 `ICanXxx` 强制。
**How to apply:**
- Model/System/Utility 子类用扩展方法访问允许的层；需要依赖时用 `[Inject]` 字段。View 不允许 `GetModel/GetSystem`，不注入 Model/System，不写 Model，不发送 Event；所有外发动作只能 `ExecuteCommand`。View 需要显示状态时，用只读查询 Command 返回 `Observable<T>` / `ReadOnlyReactiveProperty<T>` 等只读订阅源，优先用 `ReadOnlyReactiveProperty<T>` 保留当前值读取能力。
- 框架内部需要 Context（如扩展方法实现）通过 `((IHasGameContext)self).Context` 或 `GameContext.ResolveFrom(self)`。
- **struct Command 是唯一特例**：值类型不能用 `this.GetXxx<T>()` 扩展（装箱），只能用 `Execute(ICommandContext ctx)` 参数访问层。

## 2. MonoXxxBase 自动注册 + 接口多重注册

- `MonoModelBase/MonoSystemBase/MonoUtilityBase` 的 Awake 调 `AttachLayer<TLayer>(_targetContext)`。
- `_targetContext` 是 Odin 序列化的 `IGameContext`：可拖 `MonoGameContextBase`，也可运行时代码赋纯 C# `GameContext`。
- `_targetContext` 为空时自动 `GetComponentInParent<MonoGameContextBase>()`。
- `RegisterFor` 同时注册：`GetType()` + 所有派生自 `TLayer` 的接口（不含 `TLayer` 本身）。
- `MonoViewBase` 不注册，只对自身做 `Inject`。

## 3. 子类 Awake 中不要立即调用框架服务

子类 Awake 中调 `base.Awake()` 后，父级 Context 可能尚未就绪（同优先级脚本执行顺序不确定）。优先懒加载服务引用。

## 4. `readonly struct` 用于不可变值类型

不可变 struct 必须声明 `readonly struct` + `readonly` 字段。适用场景：struct Command。

## 4.1 尽量省略可推断的泛型实参

**响应式状态用 `RP<T>`**（`using R3;`——框架的 RP 定义在 R3 命名空间下，不是 `Game.Framework`）——框架提供的 `SerializableReactiveProperty<T>` 包装类，配有专用 `[CustomPropertyDrawer]`，Inspector 直接显示值，不多套一层：

```csharp
[field: SerializeField] public RP<int>        Count { get; private set; } = new(0);
[field: SerializeField] public RP<Vector3>    Pos   { get; private set; } = new();
[field: SerializeField] public RP<PlayerData> Stats { get; private set; } = new();
```

只读返回类型统一用 `ReadOnlyReactiveProperty<T>`。`RP<T>` 继承链为 `RP<T>` → `ReactiveProperty<T>` → `ReadOnlyReactiveProperty<T>`，System/Command 实现直接把 `RP<T>` 赋给 `ReadOnlyReactiveProperty<T>` 接口属性，**零分配无转换**，无需 `ROP<T>` 包装。**不引入 `IntROP` 之类别名**：C# 不支持泛型 `using` 别名，闭合别名（`using IntROP = ReadOnlyReactiveProperty<int>;`）只能 per-assembly 声明、跨程序集失效，`ROP` 缩写也不如全名自解释（对人和 AI 都更难追溯）。

C# 不支持泛型 alias（`RP<T> = ...` 语法不存在），`RP<T>` 是通过包装类实现的，与 `SerializableReactiveProperty<T>` 完全兼容——Drawer 已注册为 `[CustomPropertyDrawer(typeof(RP<>))]`，Odin 也会遵从此注册。

调用泛型方法时，能由参数或返回上下文推断的 `<T>` / `<T, TResult>` 不写，保持代码简洁。

```csharp
var count = this.ExecuteCommand(new GetCountStateCommand());
```

只有编译器无法推断、或必须避免值类型装箱且没有更简洁重载时，才显式写泛型实参。

## 5. class Command vs struct Command

**所有 Command（同步 / 异步）默认用 `readonly struct`**——零分配，通过 `ctx.GetXxx<T>()` 访问层；仅当确实需要 `[Inject]` 字段注入时才改用 class。**struct/class 的取舍只看「要不要字段注入」，与同步/异步无关**——`readonly struct` 一样能写 `async` 方法实现 `IAsyncCommand`（状态机捕获 this 的副本，不写回字段），异步也零装箱（`CommandSystem` 同步异步共用同一套泛型分发）。

| | class Command | struct Command |
|---|---|---|
| `[Inject]` | ✅ 支持 | ❌ 反射 SetValue 只修改装箱副本 |
| 访问层 | `ctx.GetSystem<T>()` 或 `[Inject]` 字段 | 只能通过 `ctx.GetSystem<T>()` |
| 分配 | 堆分配 | 零分配 |
| 同步/异步 | 都可，但仅在需要 `[Inject]` 时选它 | 都可（含 `async`），默认首选 |
| struct 有返回值时 | 优先用可推断重载 `ExecuteCommand(new Cmd())` | 无可推断重载时才写双泛型避免装箱 |

## 6. `Game.Framework.System` 命名空间与 `global::System` 冲突

`Game.Framework.System` 存在时，`System.X` 会解析到 `Game.Framework.System.X`。
- 文件顶部加 `using System;`，然后裸写 `Array.Empty<T>()`、`[ThreadStatic]`
- 不写 `global::System.X`（除非用户明确要求）

## 7. 扩展方法需要正确 using

`this.GetModel<T>()` 需要 `using Game.Framework.Model;`，`GetSystem` 需要 `using Game.Framework.System;` 等。

## 8. 异步 Command 规范

异步 Command 通过 try-catch + `Debug.LogException` 捕获错误，不能用 `.Forget()` 丢弃。

`ExecuteAsync` 签名必须带 `CancellationToken cancellationToken` 参数，框架已将 Context 生命周期令牌（或调用方传入的链接令牌）合并后传入，命令内部只用这一个参数：

```csharp
public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
{
    await UniTask.Delay(2000, cancellationToken: cancellationToken);
    ctx.GetSystem<IMySystem>().DoWork();
}
```

**View 中调用无需手动传 token**——`ViewExtensions` 的无参重载已自动将 View 销毁令牌与 Context 生命周期令牌链接，任一方销毁即取消：

```csharp
await this.ExecuteCommandAsync(new MyCommand());  // ✅ 自动绑定 View + Context 双重生命周期
```

需要更精细控制时才传入自定义令牌：

```csharp
await this.ExecuteCommandAsync(new MyCommand(), customCancellationToken);
```

## 9. 命名空间约定

- **框架**：`Game.Framework.Context/Command/Event/Model/System/Utility/View`（另有 `Internal`/`Common`/`Asset` 等内部命名空间）
- **Demo**：`Game.Framework.Demo.{Model|System|Command|Event|Utility|View}` 等子目录对应
- **测试**：`Game.Framework.Test`

**Demo 子命名空间之间用相对命名空间引用**，不加 `using`：
- 写 `Model.CounterModel`、`System.ICounterSystem`、`Event.CountChangedEvent` 等
- `Game.Framework.Demo.System` 中的 `Model.CounterModel` 会自动解析为 `Game.Framework.Demo.Model.CounterModel`
- 框架层类型（`Game.Framework.*`）仍正常 `using` 导入

## 10. 上下文绑定方式

- Mono 路径：继承 `MonoViewBase`/`MonoSystemBase` 等，Awake 自动绑定到目标 `IGameContext`。
- 纯 C# 路径：实现 `IHasGameContext`，`GameContext.AttachTo()` 反射找 `GameContext` 字段并赋值（FieldInfo 已缓存）。
- `[Inject] GameContext` / `IGameContext` 被禁止：万能门会绕过权限接口，InjectionPlan 注入期报错。
- `[Inject]` 注入目标受层权限校验（与 `this.GetXxx` 同源）：宿主有 `ICanGetModel` / `ICanGetSystem` / `ICanGetUtility` 才能注入对应层，否则注入期 `LogError` 拦下——View 注 Model/System、Model 注 Model/System、Utility 注任何层都会被挡。Command 例外（经 `ctx` 有完整层访问权）。注入非层类型（普通服务，或偶尔注册进容器的 View 等）不受此限，能否注入只看容器有没有注册。

## 11. struct Command 的扩展方法限制

struct 不能用 `this.GetXxx<T>()` 扩展方法（值类型接口调用必然装箱）。只能通过 `Execute(ICommandContext ctx)` 参数访问层。

## 12. Container 解析顺序

1. `_overrides`（运行时：MonoXxxBase Awake + `GameContext.RegisterXxx`）
2. `_bindings`（构建时：`InstallBindings`；Factory 首次 Resolve 调用并缓存）
3. 父级容器递归
4. `GameContext.Main` 全局回退（GameContext 构造参数 `inheritFromGlobal=true` 时）

子级运行时注册可覆盖父级 InstallBindings 同型注册；同层运行时重复注册抛 `InvalidOperationException`。

Container 按**精确类型键**查找，不做继承扫描。Mono 路径（`MonoXxxBase`）自动注册具体类型 + 派生接口（不含层标记 `IModel`/`ISystem`/`IUtility` 本身）；`InstallBindings` 手动路径只注册显式传入的 contracts，如需具体类型也可解析须手动补充。详见 `docs/framework-guide.md`。

## 13. 全局上下文用 MonoGlobalContext

项目唯一根上下文应继承 `MonoGlobalContext`：自动设置 `GameContext.Main = RawContext`、`DontDestroyOnLoad`、重复实例检测；业务代码不要手工设置 Main。

## 14. ContainerBuilder.RegisterFactory

Factory 首次 Resolve 调用（Lazy）或 Build() 时立即调用（Eager），结果缓存为 Singleton：
```csharp
builder.RegisterFactory(c => new AudioMixer(c.Resolve<IConfig>()), typeof(IAudioMixer));
builder.RegisterFactory(c => new NetworkClient(...), Resolution.Eager, typeof(INetworkClient));
```

## 15. 异步命令的取消令牌

`ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)` 的 `cancellationToken` 参数已合并所有相关生命周期：

- 从 View（MonoBehaviour）调用无参 `ExecuteCommandAsync`：`ViewExtensions` 自动检测并把 View 销毁令牌与 Context 令牌链接，任一触发即取消
- 从 System / 纯 C# 持有者调用无参版本：仅 Context 销毁时取消
- 显式传入自定义 token：链接到 Context 令牌（推荐用于需要业务级取消的场景）

命令内部一律使用 `cancellationToken` 参数，不再需要手动访问 `ctx.CancellationToken`。Rule 8 第 66 行的自动 View 绑定也是这个机制。

## 16. 动态 Instantiate 后自动注入

`Instantiate(prefab, contextParent)` 即可——prefab 内的 `MonoXxxBase` 子类在 Awake 里通过 `GetComponentInParent<MonoGameContextBase>` 自动找到上下文，完成注册与注入，无需手动调用任何 Inject 方法。

**查找顺序**（`MonoLayerExtensions` 内部）：
1. `_targetContext`（Inspector 显式绑定）
2. `GetComponentInParent<MonoGameContextBase>()` —— 父节点链上最近的上下文
3. `GameContext.Main` —— 全局兜底（需 `MonoGlobalContext` 已先 Awake）

三者都找不到才报错。因此 prefab 落到无父级上下文的位置时，只要全局上下文存在，会自动绑定到全局。

| 层 | Awake 行为 |
|---|---|
| `MonoViewBase` | 只注入自身 `[Inject]` 字段，不注册到容器 |
| `MonoModelBase` / `MonoSystemBase` / `MonoUtilityBase` | 注册到容器 + 注入自身 `[Inject]` 字段 |

## 17. DisposableBag 统一生命周期管理

`MonoViewBase / MonoModelBase / MonoSystemBase / MonoUtilityBase` 内置 `protected DisposableBag Bag`，`OnDestroy` 时自动释放。
Bag 是所有"有生命周期的东西"的统一容器：订阅（R3 / Framework Event / UnityEvent / C# event）、资源加载句柄、任意 IDisposable。

```csharp
// === 订阅 ===
// ReadOnlyReactiveProperty<int>（由只读查询 Command 返回）—— 订阅时 R3 自动推一次 current value
var hp = this.ExecuteCommand(new GetHPStateCommand());
Bag.Subscribe(hp, v => _hpText.text = v.ToString());

// Framework Event（携带数据）
Bag.Subscribe<PlayerHurtEvent>(OnHurt);

// Framework Event（忽略数据）—— 可选 invokeImmediately 在订阅时跑一次
Bag.Subscribe<GoldChangedEvent>(RefreshGoldDisplay, invokeImmediately: true);

// UnityEvent / Button —— 同上支持 invokeImmediately
if (_btn != null) Bag.Subscribe(_btn.onClick, OnClick);
if (_dataChannel != null) Bag.Subscribe(_dataChannel.onChanged, RefreshDisplay, invokeImmediately: true);

// UnityEvent<T>
if (_slider != null) Bag.Subscribe(_slider.onValueChanged, OnSlide);

// C# event/delegate（方案：同时传入订阅与反订阅）—— 需初始化时在前面手动调一次 handler
Bag.Subscribe(
    () => _sys.OnComplete += OnComplete,
    () => _sys.OnComplete -= OnComplete);

// === 资源加载（handle 自动入 bag，业务无感知句柄） ===
// 默认包：
var icon = await Bag.Load<Sprite>("ui/icon");
var prefab = await Bag.Load<GameObject>("prefabs/card");
var text = await Bag.LoadText("configs/level1");
var bytes = await Bag.LoadBytes("data/binary");
var scene = await Bag.LoadScene("scenes/battle", LoadSceneMode.Additive);
// 跨包：
var dlcIcon = await Bag.Load<Sprite>("dlc-package", "ui/icon");
// 也可以查询/创建下载器
if (Bag.IsNeedDownload("ui/icon")) {
    var dl = Bag.CreateTagDownloader("level1");
    Bag.Subscribe(dl.Progress, r => _slider.value = r.Progress);
    await dl.Download();
}

// === 通用 ===
Bag.Add(someDisposable);  // 任意 IDisposable
```

**订阅时初始化的统一心智**（与 R3 对齐）：
- **状态流**（ReactiveProperty / ReadOnlyReactiveProperty）：订阅即得当前值（R3 内置）。想跳过初值用 `.Skip(1)`。
- **无数据通知**（无参 Framework Event / 无参 UnityEvent）：传 `invokeImmediately: true`，在注册后立即跑一次 handler。
- **带数据事件**（`Action<T>` / `UnityEvent<T>`）：没有 current data，不提供 init 重载。**需要带初值订阅时走 R3 路径**——所有源都能转 `Observable<T>`：
  ```csharp
  // Framework Event → 用 OnEvent<T>() 桥接
  Bag.Subscribe(
      this.OnEvent<GoldChangedEvent>().Prepend(new GoldChangedEvent { NewGold = currentGold }),
      OnGoldChanged);

  // UnityEvent / UnityEvent<T> → R3 已提供 AsObservable() / OnClickAsObservable() 等
  Bag.Subscribe(_slider.onValueChanged.AsObservable().Prepend(_slider.value), OnSlide);
  ```
- 进入 Observable 后，所有 R3 操作符可用（`Where` / `Throttle` / `CombineLatest` / `Select` 等），简单便利重载之外的复杂订阅一律走这条路径，而不是再加新重载。

覆写 `OnDestroy` 时须调 `base.OnDestroy()`。
纯 C# 场景手动 `new DisposableBag(ctx)` 使用；Command 内 `using var bag = ctx.CreateBag()` 拿一个跟随方法作用域的 bag。`ctx` 仅 Framework Event 订阅和资源加载时必需。
Unity 对象 null 判断用 `if (x != null)`，不用 `?.`（Unity 重载了 `==`，C# null 传播不识别 fake null）。

**多生命周期作用域**：基类 `Bag` 跟 `OnDestroy`，需要更短作用域时（OnDisable / 回合期间 / "清理一次"按钮）调 `Bag.CreateChild()`：child 是 IDisposable，自动登记到 parent；child 单独 Dispose 不影响 parent，parent.Dispose 自动级联 child（Dispose 幂等）。
按"清理时机"前缀命名子 bag（`_enableBag` / `_roundBag` / `_loadedBag`），在对应回调里 `Dispose` 后调 `Bag.CreateChild()` 重建即可。

## 18. FrameworkLog 全局诊断开关

`FrameworkLog.Verbose = true` 开启框架诊断日志，仅 Editor/Development Build 生效。

## 19. 资源系统最佳实践

**三层职责（MVCS 拆分）：**

| 角色 | 层 | 职责 |
|---|---|---|
| `AssetSystemConfigModel` | Model | DefaultPackageName / Packages / PlayMode / CDN URL / 下载并发等配置数据，挂在 Context 节点上 Inspector 可配 |
| `AssetInitSystem` | System | 进入游戏时的初始化编排：读取配置、逐包触发 provider 初始化、暴露状态给 Utility |
| `AssetUtility` (`IAssetUtility`) | Utility | 加载 API：Load / LoadScene / LoadText / LoadBytes；管理多包状态，具体资源库细节交给 provider |

场景搭建：同一 Context 节点下挂 `AssetSystemConfigModel` + `AssetUtility` + `AssetInitSystem` 三个 Mono；Awake 顺序由 ExecutionOrder 自动保证（Utility -400, Model -300, System -200）。

**业务接入：**

- **动态加载**走 `Bag.Load<T>(location)` / `Bag.LoadScene(...)` / `Bag.LoadText(...)` / `Bag.LoadBytes(...)`，handle 自动入 bag，宿主 OnDestroy 时统一释放。Bag 内部会等 init 完成，业务无需关心时序；跨包用 `Bag.Load<T>(packageName, location)` 等显式 package 重载。
- **Inspector 拖拽引用**走 `AssetReference<T>.Get()`：字段在 Awake 自动绑定加载器并加入宿主 Bag，宿主 OnDestroy 时由 Bag.Dispose 调 ref.Dispose 释放。AssetReference Inspector 同行下拉可指定 package，留空走默认包。
- **ScriptableObject / 纯 C# 对象的 ref 不会自动绑定**（框架刻意不递归 SO，因为共享 SO 资产不该被某个宿主生命周期接管）：由加载 / 持有它的宿主一行 `bag.BindAssetReferences(对象)` 把它内部所有 AssetReference 绑到自身生命周期（也可逐个 `ref.Bind(utility, hostToken)`，或退到 `GameContext.Main` 兜底但会输出 error）。**config SO 是「Model 持有/加载的数据」，不做 Model 层**——它常需像资源一样异步加载，无法在启动时注册成 Model。
- **启动界面进度**订阅 `this.GetUtility<IAssetUtility>().InitState`（`ReadOnlyReactiveProperty<AssetInitState>`，Idle/Initializing/Ready/Failed）；或等待 `Bag.EnsureInitialized()`。
- **多 package**：所有包（含默认包）都登记在 `AssetSystemConfigModel.Packages` 列表里，每个包配自己的「自动初始化 / 启用按需下载」策略；`DefaultPackageName` 只是指向其中一个的默认指针（留空 = 无默认包，加载须用带 packageName 的重载）。子 Context 经 Container 父级回退共享父级 `AssetUtility`，不需要每个 Context 单独挂一套资源系统。
- **包初始化**：标了「自动初始化」的包启动即拉清单；标「不自动初始化」的包（DLC 懒加载 / 合规延迟联网）须业务在用前显式调 `IAssetUtility.Initialize("包名")`。⚠ 既没自动初始化、也没 `Initialize` 过的包，`Load` 它会**直接抛**「未初始化」异常（fail-fast，不是无限等待）。
- **下载进度** `var dl = Bag.CreateTagDownloader("level1");`，订阅 `dl.Progress`（R3 状态流，无需 invokeImmediately），调 `dl.Download(ct)` 启动。跨包下载器用 `Bag.CreateTagDownloader(packageName, tags)`。
- **Command 临时加载** `using var bag = ctx.CreateBag(); var prefab = await bag.Load<GameObject>(...);` —— using 块结束自动释放，Command 用完即净。
- **手动卸载短期资源**：`ref.TryGetAsset(out T)` 非阻塞检查；`ref.Unload()` 释放本 ref 持有的 handle；`AssetReferenceList.GetAll()` 并行加载，类型不匹配有 error 日志。GUID 是 `AssetReference` 内部细节，不作为业务 API 暴露。

> **底层库版本**：当前基于 **YooAsset 3.0.2-beta**，`YooAssetProvider` 已用**原生 3.0 API** 实现——FileSystem 化初始化（`InitializePackageOptions` 分模式选项 + `InitializePackageAsync`）、`IRemoteService`、拆分后的 `IBundleOffsetDecryptor`/`IBundleMemoryDecryptor`、`LoadAssetAsync<RawFileObject>`、`ResourceDownloaderOptions` + 进度事件。**不再依赖兼容层，`YOOASSET_LEGACY_API` define 已移除**。所有 YooAsset 接触面仍收口在 `YooAssetProvider`，框架其余代码与业务都不直接依赖 YooAsset。详见 `docs/adr/0013-yooasset-native-rewrite.md`；构建期踩坑与规避（含库更新后何时可删冗余代码）见 `docs/yooasset-pitfalls.md`。

## 20. MonoXxxBase 反注册必须 IsDisposed 短路

子层 OnDestroy 反注册前必须检查 `_contextProvider.IsDisposed`：
```csharp
protected virtual void OnDestroy()
{
    if (_contextProvider != null && !_contextProvider.IsDisposed)
        _contextProvider.Container.UnregisterFor<IModel>(this);
}
```
**Why:** `DefaultExecutionOrder` 同时影响 Awake 和 OnDestroy 顺序。`MonoGameContextBase`(-1000) 比 `MonoModelBase/System/Utility`(-300/-200/-400) 先 OnDestroy，会先 Dispose 并清空 `_context`；子层后跑 OnDestroy 访问 `_contextProvider.Container` 会 NRE。父级已 Dispose 时 Container 已失效，反注册没意义，跳过即可。

**How to apply:** 此短路已集中在 `MonoLayerBase<TLayer>`（Model/System/Utility 三层的共享基类）实现，继承 `MonoModelBase`/`MonoSystemBase`/`MonoUtilityBase` 的业务**自动获得，无需重写 OnDestroy**。仅当你新增一个"会在 Awake 注册到 Container"的 Mono 层基类时才照搬此模式。MonoViewBase 不注册自己（只 Inject）所以无此问题。

## 21. 不要在运行时热替换已注册的层（Model/System/Utility）

框架**不支持**运行时删除/替换已注册的 Model/System/Utility 后让既有引用自动指向新实例。

| 访问路径 | 取 model 的方式 | 删除子 model 后的实际目标 |
|---|---|---|
| `[Inject]` 字段（class Command / System） | Awake/Execute 时**一次性快照**到字段 | 仍指向已反注册的"孤儿" model 实例（C# 对象未被 GC） |
| `ctx.GetModel<T>()`（struct Command） | 每次调用**实时解析**容器 | 按 #12 解析顺序回退到父级 model |
| View 通过查询 Command 取得的只读订阅源 | Awake 时订阅的是具体 `ReactiveProperty` 派生实例 | 继续订阅孤儿 model（不感知容器变化） |

**Why:** R3 订阅与 `[Inject]` 字段都绑定到具体实例引用，容器反注册不会重定向它们；而 `ctx.GetXxx<T>()` 走容器解析，会按回退顺序找到父级或抛异常。两条路径混用时观察值与写入目标会分裂。

**How to apply:**
- 默认假设："注册到 Container 的层在 Context 生命周期内不变"。需要切换数据时，**改 model 内部状态**（重置字段、清空集合），不要 Destroy 整个 model GameObject。
- 真正需要替换实例时，**整层 Context 一并 Dispose 重建**（场景切换、关卡重置），而不是单独删一个层。
- 想做热替换需要一套声明式绑定 + Container 注册事件的机制，目前不在框架范围内；如未来引入，再来更新此条。

## 22. Inspector 引用默认 fail-fast

必填的 `[SerializeField]` 引用不要用 `if (_xxx != null)` 静默跳过；直接使用，或在初始化入口显式校验并抛出清晰异常。
**Why:** Inspector 漏配是场景搭建错误，静默跳过会让按钮无响应、文本不刷新，问题被拖到交互阶段才暴露。
**How to apply:**
- 按钮、主文本、核心容器、必须存在的 prefab/组件引用默认视为必填。
- 只有确实支持降级的引用才保留 null 判断，并用命名或 Tooltip 表明它是 optional。
- Unity fake null 场景仍用 `if (x == null)` / `if (x != null)`，不用 `?.`。

## 23. 对象池 IPoolUtility

框架自带对象池（`Game.Framework.Pool`），替代第三方池库，与 Bag 生命周期融合。

- **注册（按生命周期选）**：纯 C# 跟随 Context 用 `builder.RegisterOwned(new PoolUtility(), typeof(IPoolUtility))`（随 `GameContext.Dispose` 清池，推荐）；不关心释放用 `RegisterValue`；需 Inspector 配参数 / 跟随 GameObject 生命周期用 `MonoPoolUtility`（挂 Context 子节点，可视化配各 prefab 容量/预热）。三者复用同一套池逻辑，子 Context 经父级回退共享。
- **Bag.Rent（首选）**：`MonoXxxBase` 子类里 `var w = Bag.Rent<Widget>();`——宿主销毁/bag.Dispose 时**自动归还**，无感知，心智同 `Bag.Load`。要求 `Widget : class, new()`。
- **手动**：`this.GetUtility<IPoolUtility>().Rent<T>()` / `.Return(obj)`；需要自定义工厂或租借/归还钩子时先 `GetPool<T>(factory, onRent, onReturn, maxSize)` 配置一次（**首次配置生效**），之后 `pool.Rent()/Return()`。
- **状态清理放归还时**：池化类型实现 `IPoolable.OnReturn()` 清字段/退订（或用 `GetPool` 的 `onReturn` 委托）；`OnRent()` 做激活。**已 Return 的实例不要再用**。
- 主线程独占。Editor/Dev 构建下重复归还/归还外来实例会 LogError。
- **GameObject/Prefab 池**：同一 `IPoolUtility` 按 prefab 管理。`Bag.Spawn(prefab, parent)` / `Bag.Spawn(prefab, pos, rot)` 取实例，宿主销毁自动 Despawn（心智同 `Bag.Rent`）；手动用 `GetUtility<IPoolUtility>().Spawn(prefab,…)` / `.Despawn(go)`（实例带 `PooledObject` 标记自动路由回源池）。`await pool.Prewarm(n, perFrame)` 分帧预热、`TrimAsync(target, perFrame)` / `ClearAsync()` 分帧收缩/销毁（C# 池用同步 `Trim(target)`）；内部停放节点被外部删后下次归还自愈重建。实例上**任意组件**实现 `IPoolable` 即收 OnRent/OnReturn。按 location 异步加载先 `await Bag.Load<GameObject>(loc)` 取 prefab 再 Spawn（池刻意不依赖 Context/IAssetUtility）。详见 `docs/framework-guide.md` §7。
