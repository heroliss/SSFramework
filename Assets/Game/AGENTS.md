# Game 框架使用规则

本文件只放使用 `Assets/Game/Framework/` 框架 API 时的**约束与决策依据**，AI Agent 在 `Assets/Game/` 下任意目录工作时自动加载（目录就近性）。教学、示例与完整 API 见 `docs/framework-guide.md`（下称 guide）对应章节；框架**内部**编码规则见 `Assets/Game/Framework/AGENTS.md`。

## 1. IGameContext vs IHasGameContext

> **术语口径**：对象统一叫 **Context / 上下文**（`GameContext` 实例 = 能力环境）；**作用域 / scope** 只描述生命周期 / 解析边界——Context 嵌套成**作用域树**做解析回退、`Bag.CreateChild()` 开**更短的作用域**。别把某个 Context 实例直接叫成「作用域」。

- `IGameContext` 是完整能力接口；`GameContext` 与 `MonoGameContextBase`（场景 Mono 代理，转发到内部真实 GameContext）都实现它。
- 业务对象统一持有 `IHasGameContext.Context`（接口）；`MonoGameContextBase.RawContext` 仅限必须拿具体 `GameContext` 的边界（如 `GameContext.Main`）。

### 1.1 层不应该直接拿到 `IGameContext`（除 struct Command）

`MonoXxxBase` 的 `IHasGameContext.Context` 是**显式接口实现**——业务子类写不了 `Context.GetModel<>()` 之类代码。
**Why:** 拿到完整 Context 就能 Container/Inject/RegisterXxx，绕过 `ICanXxx` 权限接口。
**How to apply:**
- Model/System/Utility 子类用扩展方法访问允许的层；需要依赖用 `[Inject]` 字段。
- View 不允许 GetModel/GetSystem、不注入 Model/System、不写 Model、不发 Event；外发只 `ExecuteCommand`，显示状态用只读查询 Command 返回 `ReadOnlyReactiveProperty<T>` / `Observable<T>`（优先前者，保留当前值读取）。
- 框架内部经 `((IHasGameContext)self).Context` 或 `GameContext.ResolveFrom(self)`。
- **struct Command 是唯一特例**：值类型用 `this.GetXxx` 扩展会装箱，只能经 `Execute(ICommandContext ctx)` 参数访问层。

## 2. MonoXxxBase 自动注册 + 接口多重注册

- `MonoModelBase/MonoSystemBase/MonoUtilityBase` Awake 调 `AttachLayer<TLayer>(_targetContext)`；`_targetContext`（Odin 序列化的 `IGameContext`，可拖 Mono 也可代码赋纯 C# 实例）为空时自动 `GetComponentInParent<MonoGameContextBase>()`。
- `RegisterFor` 同时注册：具体类型 + 所有派生自 `TLayer` 的接口（**不含** `TLayer` 本身）。
- `MonoViewBase` 不注册，只对自身 `Inject`。

## 3. 子类 Awake 中不要立即调用框架服务

`base.Awake()` 后父级 Context 可能尚未就绪（同优先级脚本 Awake 顺序不定）。服务引用优先懒加载（`Start()` 或首次调用时取）。

## 4. `readonly struct` 用于不可变值类型

不可变 struct 必须声明 `readonly struct` + `readonly` 字段。典型：struct Command。

## 4.1 响应式类型与泛型实参

- **响应式状态用 `RP<T>`**（`using R3;`——RP 定义在 R3 命名空间，不是 `Game.Framework`）：
  `[field: SerializeField] public RP<int> Count { get; private set; } = new(0);`——Inspector 直接显示值（专用 Drawer），任意类型可用。
- **只读返回统一 `ReadOnlyReactiveProperty<T>`**：`RP<T>` IS-A 它，直接赋值零分配无转换；**不引入 `ROP` / `IntROP` 等别名**（C# 无泛型 using 别名、闭合别名跨程序集失效、全名对人和 AI 更可追溯——详见 guide §5）。
- 能由参数 / 返回推断的泛型实参不写：`var count = this.ExecuteCommand(new GetCountStateCommand());` 仅编译器无法推断或需避免装箱时才显式写。

## 5. class Command vs struct Command

**所有 Command（同步 / 异步）默认 `readonly struct`**——取舍只看「要不要 `[Inject]` 字段注入」，与同步异步无关（`readonly struct` 一样能写 `async` 实现 `IAsyncCommand`，同步异步共用同一套泛型分发、零装箱）。

| | class Command | struct Command |
|---|---|---|
| `[Inject]` | ✅ | ❌（反射 SetValue 只改装箱副本） |
| 访问层 | `ctx.GetXxx` 或 `[Inject]` | 只能 `ctx.GetXxx<T>()` |
| 分配 | 堆分配 | 零分配 |
| 有返回值 | 本就堆分配，可推断重载即可 | 可推断重载 `ExecuteCommand(new Cmd())` 会**装箱一次**（`TResult` 只在约束里、无法被推断）；绝大多数场景够用，热路径零装箱写双泛型 `ExecuteCommand<TCmd, TResult>(new Cmd())` |

## 6. `Game.Framework.System` 命名空间与 `global::System` 冲突

`Game.Framework.System` 存在时 `System.X` 会被就近劫持。文件顶部 `using System;` 后裸写 `Array.Empty<T>()`、`[ThreadStatic]`；不写 `global::System.X`（除非用户明确要求）。

## 7. 扩展方法需要正确 using

`this.GetModel<T>()` 需 `using Game.Framework.Model;`，`GetSystem` 需 `using Game.Framework.System;`，其余层同理。

## 8. 异步 Command 规范

- 签名固定 `ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)`；框架已把相关生命周期令牌合并传入，命令内部**只用这一个参数**（不再碰 `ctx.CancellationToken`）。
- 异常 try-catch + `Debug.LogException` 兜底，不能 `.Forget()` 丢弃。
- View 调无参 `this.ExecuteCommandAsync(cmd)` 自动绑定 View 销毁 + Context 双令牌，任一销毁即取消；需要业务级取消才显式传自定义 token（会链接到 Context 令牌）。
- **命令内组合子命令经 `ctx`**（Command 不持有 `ICanSendCommand`）：同步 `ctx.ExecuteCommand(...)`；异步 `await ctx.ExecuteCommandAsync(sub, cancellationToken)` 透传令牌、取消随父命令级联。子命令的价值 =「可被 CommandSystem 装饰器统一拦截」（日志/回放/事务），不需要拦截就直接调 System 方法。

## 9. 命名空间约定

- 框架：`Game.Framework.{Context|Command|Event|Model|System|Utility|View}`（另有 `Internal`/`Common`/`Asset` 等）。
- Demo：`Game.Framework.Demo.{Model|System|...}`；**Demo 子命名空间之间用相对命名空间引用、不加 using**——写 `Model.CounterModel`、`System.ICounterSystem`，自动解析到 `Game.Framework.Demo.Model.*`；框架类型照常 using。
- 测试：`Game.Framework.Test`。

## 10. 上下文绑定方式

- Mono 路径：继承 `MonoXxxBase`，Awake 自动绑定。纯 C# 路径：实现 `IHasGameContext`，`GameContext.AttachTo()` 反射找 `GameContext` 字段回写（FieldInfo 已缓存）。
- **`[Inject] GameContext / IGameContext` 被禁止**：万能门绕过权限接口，注入期报错。
- `[Inject]` 注入目标受层权限校验（与 `this.GetXxx` 同源）：宿主有对应 `ICanGetModel/System/Utility` 才能注入该层——View 注 Model/System、Model 注 Model/System 被挡；**Utility 有 `ICanGetUtility`，可注 / 取其他 Utility（基础设施互相组合），但仍不能注 Model/System**（不反向依赖业务）。Command 例外（经 ctx 有完整层访问权）。非层类型（普通服务等）不受此限，能否注入只看容器是否注册。

## 11. struct Command 的扩展方法限制

struct 不能用 `this.GetXxx<T>()` 扩展方法（值类型接口调用必然装箱），只能通过 `Execute(ICommandContext ctx)` 参数访问层。

## 12. Container 解析顺序

1. `_overrides`（运行时：MonoXxxBase Awake + `GameContext.RegisterXxx`）→ 2. `_bindings`（构建时 `InstallBindings`；工厂首次 Resolve 调用并缓存）→ 3. 父级容器递归 → 4. `GameContext.Main` 全局回退（`inheritFromGlobal=true` 时）。

- 子级运行时注册可覆盖父级 InstallBindings 同型注册；同层运行时重复注册抛 `InvalidOperationException`。
- Container 按**精确类型键**查找、不做继承扫描。Mono 路径自动注册具体类型 + 派生接口（不含层标记本身）；`InstallBindings` 手动路径只注册显式传入的 contracts。详见 guide §11。
- **构建期值绑定自动注入**（ADR-0019）：`RegisterValue`/`RegisterOwned` 实例在 Context 构造时自动 `Inject`+`AttachTo`（工厂产物不注入——经工厂参数 `Container` 显式接线）；运行时 `RegisterXxx` 的纯 C# 实例仍需手动补这两步。固定目录的服务注册可代码生成（菜单 `SSFramework/服务注册/生成服务安装器代码`，opt-out 标 `[ExcludeFromInstaller]`）。

## 13. 全局上下文用 MonoGlobalContext

项目唯一根上下文应继承它：自动设 `GameContext.Main`、`DontDestroyOnLoad`、重复实例检测；业务代码不要手工设置 Main。

## 14. ContainerBuilder.RegisterFactory

工厂 Lazy（首次 Resolve）或 Eager（Build() 时，传 `Resolution.Eager`）调用一次，结果缓存为 Singleton：
`builder.RegisterFactory(c => new AudioMixer(c.Resolve<IConfig>()), typeof(IAudioMixer));`

## 15. 异步命令的取消令牌

`cancellationToken` 参数已合并所有相关生命周期：View（MonoBehaviour）无参调用 = View 销毁 + Context 令牌链接；System / 纯 C# 无参调用 = 仅 Context；显式传入自定义 token = 链接到 Context 令牌。命令内部一律只用该参数（见 #8）。

## 16. 动态 Instantiate 后自动注入

`Instantiate(prefab, contextParent)` 即可——prefab 内 `MonoXxxBase` 在 Awake 自动完成查找 / 注册 / 注入。查找顺序：`_targetContext`（Inspector 显式）→ Transform 父链最近的 `MonoGameContextBase` → `GameContext.Main` 兜底，三者都没有才报错。View 只注入不注册；Model/System/Utility 注册 + 注入。

## 17. DisposableBag 统一生命周期管理

`MonoXxxBase` 内置 `protected DisposableBag Bag`，OnDestroy 自动释放——订阅、资源句柄、池租借、任意 IDisposable 的统一容器。完整示例见 guide §8 / §13 / §14，速查：

- **订阅**：`Bag.Subscribe(observable, handler)`（RP 订阅即得当前值）/ `Bag.Subscribe<TEvent>(handler)`（Framework Event）/ `Bag.Subscribe(unityEvent, handler)` / `Bag.Subscribe(subscribe, unsubscribe)`（C# event 双侧对称）/ `Bag.Add(disposable)`（逃生舱口）。
- **资源**：`await Bag.Load<T>(location)`（handle 自动入 bag）/ `Bag.LoadScene(...)` / `Bag.LoadText` / `Bag.LoadBytes`（内容直读、不入 bag）；跨包用带 packageName 重载。查询 / 下载器走 `this.GetUtility<IAssetUtility>()`——Bag 只收「借出 + 跟随生命周期」的操作。
- **订阅时初始化心智**（与 R3 对齐）：状态流订阅即得当前值（跳过初值 `.Skip(1)`）；无数据通知传 `invokeImmediately: true`；带数据事件走 Observable 桥接——`this.OnEvent<T>().Prepend(...)` / `AsObservable().Prepend(...)`。复杂订阅一律走 R3 操作符链，**不加新重载**。
- **子作用域**：`Bag.CreateChild()`——child 单独 Dispose 无副作用、parent Dispose 自动级联；按清理时机前缀命名（`_enableBag` / `_roundBag`），对应回调里 Dispose 后重建。
- 覆写 `OnDestroy` 必须调 `base.OnDestroy()`。纯 C# 场景 `new DisposableBag(ctx)`；Command 内 `using var bag = ctx.CreateBag()`。
- Unity 对象判空用 `if (x != null)`、不用 `?.`（fake null）；null UnityEvent 订阅 Editor/Dev 下 LogError（Inspector 漏配 fail-fast，见 #22）。

## 18. FrameworkLog 全局诊断开关

`FrameworkLog.Verbose = true` 开启框架诊断日志，仅 Editor / Development Build 生效。

## 19. 资源系统最佳实践

三件套挂同一 Context 节点：`AssetSystemConfigModel`（Model·配置数据）+ `AssetInitSystem`（System·初始化编排）+ `AssetUtility : IAssetUtility`（Utility·加载 API），Awake 顺序由 ExecutionOrder 自动保证。全流程图谱见 `docs/asset-system-flow.md`，用法教学见 guide §13。

- 动态加载走 `Bag.Load*`（见 #17）；Inspector 拖拽引用走 `AssetReference<T>.Get()`——`MonoXxxBase` 字段 Awake 自动绑定、随宿主 Bag 释放。GUID 是 AssetReference 内部细节，不作为业务 API 暴露。
- **SO / 纯 C# 对象的 AssetReference 不自动绑定**（框架刻意不递归 SO——共享资产不该被某个宿主接管）：由加载 / 持有它的宿主一行 `bag.BindAssetReferences(obj)` 绑定。config SO 是「Model 持有 / 加载的数据」，不做 Model 层。
- 多包：所有包（含默认包）登记在 `AssetSystemConfigModel.Packages`，各配「自动初始化 / 按需下载」策略；`DefaultPackageName` 只是默认指针（留空 = 无默认包）。子 Context 靠容器父级回退共享，不重复挂三件套。业务代码的 `packageName` 参数用生成的常量类（菜单 `SSFramework/资源构建/生成包名常量代码`，默认 `Game.Main.AssetPackages`），不写裸字符串。
- ⚠ **既没开自动初始化、也没 `Initialize` 过的包，`Load` 直接抛「未初始化」异常**（fail-fast，不是无限等待）。启动进度订阅 `IAssetUtility.InitState` 或 `await Bag.EnsureInitialized()`。
- 下载器：`CreateTagDownloader(...)` 订阅 `dl.Progress` 后 `dl.Download(ct)`；下载器 / 查询（`CheckLocationValid` / `IsNeedDownload`）刻意不在 Bag 上。
- 底层为 **YooAsset 3.0 原生 API**，全部接触面收口在 `YooAssetProvider`（ADR-0013）；构建期踩坑见 `docs/yooasset-pitfalls.md`，加密见 `docs/asset-encryption.md`。

## 20. MonoXxxBase 反注册必须 IsDisposed 短路

`MonoGameContextBase`(-1000) 比子层（-400/-300/-200）先 OnDestroy——层反注册前必须检查 `_contextProvider.IsDisposed`，否则访问已 Dispose 的 Container 会 NRE。
**此短路已集中在 `MonoLayerBase<TLayer>` 实现，业务自动获得、无需重写 OnDestroy**；仅当新增一个「会在 Awake 注册进 Container 的 Mono 层基类」时才照搬此模式（`MonoViewBase` 不注册自己，无此问题）。

## 21. 不要在运行时热替换已注册的层（Model/System/Utility）

| 访问路径 | 取 model 的方式 | 删除子 model 后的实际目标 |
|---|---|---|
| `[Inject]` 字段（class Command / System） | Awake/Execute 时一次性快照 | 仍指向已反注册的孤儿实例 |
| `ctx.GetModel<T>()`（struct Command） | 每次实时解析容器 | 按 #12 回退到父级 |
| View 经查询 Command 拿到的只读订阅源 | 绑定具体实例 | 继续订阅孤儿（不感知容器变化） |

**Why:** 订阅与 `[Inject]` 都绑定实例引用、不随容器重定向——混用时「读的和写的不是同一份」。
**How to apply:** 边界速记——**增量随便加，换血不允许，撤就整棵撤**。换数据 → 改 model 内部状态（重置字段）；换实例 → 子 Context 覆盖；换整层 → Context 一并 Dispose 重建（场景切换 / 关卡重置）。把「层 + 它的消费者」放同一子树、连根一起撤即无孤儿。详见 guide §11「运行时增删层的边界」。

## 22. Inspector 引用默认 fail-fast

必填的 `[SerializeField]` 引用不要 `if (_xxx != null)` 静默跳过：直接使用，或在初始化入口显式校验抛清晰异常。只有确实支持降级的引用才保留判空，并用命名 / Tooltip 标明 optional。Unity fake null 场景判空用 `==` / `!=`、不用 `?.`。
**Why:** Inspector 漏配是场景搭建错误，静默跳过会让按钮无响应、文本不刷新，拖到交互阶段才暴露。

## 23. 对象池 IPoolUtility

- **注册按生命周期选**：跟随 Context 用 `builder.RegisterOwned(new PoolUtility(), typeof(IPoolUtility))`（随 Dispose 清池，推荐）；不关心释放用 `RegisterValue`；要 Inspector 配容量 / 预热用 `MonoPoolUtility`。三者同一套逻辑，子 Context 靠父级回退共享。
- **首选 `Bag.Rent<T>()` / `Bag.Spawn(prefab, ...)`**——宿主销毁自动归还。单个提前归还必须在**同一 bag** 上 `Bag.Return(obj)` / `Bag.Despawn(go)`（自动摘登记、不重复归还）；「一波 / 一局」局部作用域配 `Bag.CreateChild()` 整批管理；弹幕级高频热路径用领域 List + 手动池。
- 自定义工厂 / 钩子先 `GetPool<T>(factory, onRent, onReturn, maxSize)` 配置一次（**首次配置生效**，之后带配置的调用 Editor/Dev 下警告）；状态清理放 `IPoolable.OnReturn`；**已 Return 的实例不要再用**；池不负责 Dispose 实例（持非托管资源的对象在 OnReturn 里自行释放）。
- GameObject 池：`await pool.Prewarm(n, perFrame)` 分帧预热、`TrimAsync / ClearAsync` 分帧收缩；实例上任意组件实现 `IPoolable` 即收 OnRent/OnReturn；按 location 先 `Bag.Load<GameObject>` 再 Spawn（池刻意不依赖资源系统）。主线程独占；Editor/Dev 下检测重复归还 / 外来实例。详见 guide §7。

## 24. 配置表（Luban）最佳实践

- **接入** = 一行子类闭合泛型 `class XxxConfigUtility : MonoConfigUtilityBase<Tables>`（补 `TableFiles => LubanTableManifest.Files` 与 `CreateTables`），挂 Context 子节点。配置是静态只读引用数据：不占 Model 层、不拆三件套，一个组件自加载。
- **取表**：各层（含 View）`GetUtility<IConfigUtility<Tables>>().Tables` 直读（也可 `[Inject]` 字段）。`Tables` 是**普通取值**（只读、无 `.CurrentValue`），加载完成前为 null——**等就绪订阅 `State`（`ConfigInitState`），不要轮询判空**。查询直接用生成的强类型 API（`TbItem.Get(id)` 等），框架不包查询层。**多套配置** = 不同闭合泛型并存，各自解析互不冲突。
- **改表**：改对应 profile 的 conf 源目录（demo 在 `Demo/Configs~/`，`~` 后缀 Unity 不导入）→ 菜单「配置表构建 / 生成」（Play 中拒绝）。生成代码目录被 Luban 接管，勿手放文件。多套集中管理用「配置总览」窗口。
- ⚠ **topModule 别嵌进含 `System` 子命名空间的层级**（生成代码裸写 `System.Func` 被就近解析劫持 CS0234）——demo 用顶层 `DemoCfg`。
- 数据 `.bytes` 随资源包打包 / 热更；表**结构**变化会改生成代码 → 走代码热更 / 发版。详见 guide §16、ADR-0009。

## 25. UI 框架（窗口 / 层级）最佳实践

- **入口**：Context 子节点挂**单个** `MonoToolkitUI`（UIDocument + PanelSettings）或 `MonoUGuiUI`（Canvas，场景需 EventSystem）——自动注册 `IUIUtility`；**同一 Context 二选一**，重复注册报错。
- **开窗**：`this.GetUtility<IUIUtility>().Open<T>(args)`（View 有 `ICanGetUtility`，开窗合法，同 `Bag.Load` 心智；struct Command 经 `ctx`）；`Close<T>()` / `Back()`（关 Page 栈顶）/ `CloseAll(layer)` / `Get<T>()`；资源加载失败返回 null。
- **写窗口**：Toolkit 继承 `UIToolkitWindowBase`（**需无参构造**，框架 Activator 实例化；接线放 `OnCreated`、参数放 `OnOpen(args)`）；UGUI 继承 `UGuiWindowBase`（是 `MonoViewBase`，**不要覆写 Awake**）。元数据用 `[UIWindow(Layer/Asset/Cache/Modal)]` 声明；`Asset` 留空 = 纯代码搭建（两套 backend 都支持）。窗口就是 View——读写分离照旧。
- **生命周期 hook**（框架调，非 Unity）：`OnCreate → OnOpen(args) → OnCover/OnReveal → OnClose`；cover/reveal **按层内**计算，跨层覆盖不触发。
- **数据绑定统一 R3**（`Bag.BindText / BindEnabled / BindVisible / SubscribeClick`），**不用** UI Toolkit 原生 DataBinding。非窗口的 Toolkit 视图用 `UIToolkitViewBase` + `view.BindTo(ctx)`。
- ⚠ Toolkit 窗口 Context 由框架**显式注入**（不在 GameObject 父链）；UGUI 窗口沿父链自动注入。换后端业务开窗代码零改。详见 guide §17、ADR-0016。
