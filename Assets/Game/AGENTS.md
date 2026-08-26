# Game 框架使用约束

本文件在 `Assets/Game/` 下工作时自动加载，只保留会改变业务代码决策的约束。完整心智模型、示例与 API 见 `docs/framework-guide.md`；框架内部实现规则见 `Assets/Game/Framework/AGENTS.md`。

## 架构语言与权限

- **Context / 上下文**是能力环境；**作用域 / scope**只描述解析与生命周期边界。Context 嵌套成作用域树，`Bag.CreateChild()` 创建更短作用域，不把 Context 实例直接叫“作用域”。
- 业务对象只持有 `IHasGameContext.Context`；`RawContext` 只用于必须拿具体 `GameContext` 的边界。
- Model / System / Utility / View 不直接持有 `IGameContext`。按各自 `ICanXxx` 扩展和 `[Inject]` 访问允许的 Interface；`[Inject] GameContext/IGameContext` 会被拒绝。
- **View 只观察、只发 Command**：不取/注入 Model 或 System，不写 Model，不直接发 Event。持续显示状态由查询 Command 返回 `ReadOnlyReactiveProperty<T>` / `Observable<T>`。
- Utility 可组合其他 Utility，但不反向依赖业务 Model/System。Command 经 `ICommandContext` 拥有完整的合法编排能力。
- 框架内部才经 `((IHasGameContext)self).Context` 或 `GameContext.ResolveFrom(self)` 取完整 Context。

## 状态、Command 与异步

- 可变单值状态用 R3 命名空间下的 `RP<T>`；对外统一暴露 `ReadOnlyReactiveProperty<T>`，不新增 `ROP` 等别名。
- 可增删重排的集合用 `ObservableList<T>`，只读暴露 `IReadOnlyObservableList<T>`；不要用 `RP<IReadOnlyList<T>>` 迫使 UI 整表重建。UI 增量绑定用 `Bag.BindList`，每行订阅放工厂提供的子 Bag；超大虚拟化列表直接用 Toolkit `ListView`。
- Command 默认 `readonly struct`。只有需要 `[Inject]` 字段时才用 class；struct 只能经 `Execute(ICommandContext ctx)` 访问层，避免 `this.GetXxx` 的接口装箱。返回值 struct Command 在极热路径可显式写双泛型重载避免一次装箱。
- 异步 Command 固定接收 `CancellationToken cancellationToken`，内部只使用这个已合并生命周期的参数；组合子命令经 `ctx` 并透传 token。View 无参调用自动链接 View 销毁与 Context token。
- 异步异常必须被 `await` 或显式观察；需要兜底时用 `Log.Error(..., ex)`，不要用无人观察的 `.Forget()` 吞掉失败。
- 泛型实参能推断就不写；不可变值类型声明 `readonly struct` + `readonly` 字段。

## Mono 生命周期与 Context

- `MonoModelBase/MonoSystemBase/MonoUtilityBase` 在 Awake 自动注册“具体类型 + 派生层 Interface”；`MonoViewBase` 只注入、不注册。
- 子类 `Awake` 调 `base.Awake()` 后不要立刻假设父 Context 已就绪；同优先级 Awake 顺序不定，服务引用放到 `Start` 或首次使用时解析。
- Context 唯一根使用 `MonoGlobalContext`，业务不手设 `GameContext.Main`。动态 Instantiate 后按“显式 target → Transform 父链 → Main”自动绑定；三者都没有才报错。
- 必填 Inspector 引用 fail-fast：直接使用或初始化时抛清晰异常。只有明确 optional 才判空并标注；Unity fake null 用 `==/!=`，不用 `?.`。
- 不热替换已注册的 Model/System/Utility：注入和订阅持有实例快照，容器实时解析却会转向新实例，混用会读写分叉。边界是“增量可加，换血不允许，撤就整棵 Context 撤”。
- 覆写 `OnDestroy` 必须调用基类。注册层的“父 Context 已先 Dispose”短路由 `MonoLayerBase` 统一处理，业务不要重复实现。
- 命名空间任何层级都不要使用 `System` 段（会劫持 `System.IO`、生成代码等限定名）；System 层命名空间固定 `Game.Framework.Systems`。

## Container 与所有权

解析顺序固定：运行时 override → 构建期 binding → 父 Container → 可选 `GameContext.Main` 回退。按**精确类型键**解析，不扫描继承树。

- `RegisterFactory` 首次解析（或 Eager 构建）调用一次并缓存 Singleton，但**不拥有**产物；工厂返回 `IDisposable` 且应随 Context 释放时用 `RegisterOwnedFactory`。
- `RegisterValue/RegisterOwned` 的实例在构建时自动 Inject + Attach；两类 Factory 都用参数显式接线，不自动注入；运行时 `RegisterXxx` 的纯 C# 实例需调用方完成接线。
- 现成且有所有权的服务用 `RegisterOwned`，需解析其他依赖再构造且有所有权的服务用 `RegisterOwnedFactory`；固定目录服务可用“服务注册生成器”，按 `[ExcludeFromInstaller]` opt-out。
- 业务手工创建 `ContainerBuilder` 时用 `using var`：Build 前 Builder 暂管 owned 资源并在异常路径回滚；Build 成功后所有权移交 Container，不会被 Builder 提前释放。Mono Context / Flow 内部已自动遵守。
- 运行时同层重复注册抛异常；子级注册可覆盖父级同 contract。

## DisposableBag 是默认生命周期入口

`MonoXxxBase.Bag` 在 OnDestroy 释放。订阅、资源 handle、池租借和任意 `IDisposable` 都登记到 Bag；纯 C# 用 `new DisposableBag(ctx)`，Command 内用 `using var bag = ctx.CreateBag()`。

- 订阅使用 `Bag.Subscribe(...)`；状态流订阅即得当前值，跳过初值用 `.Skip(1)`。复杂数据流用 R3 操作符组合，不为个案扩张 Bag API。
- 资源借用使用 `await Bag.Load<T>/LoadScene`；查询、下载器等非所有权操作经 `IAssetUtility`。
- 局部阶段用 `Bag.CreateChild()`；按清理时机命名 `_enableBag/_roundBag`，对应回调先 Dispose 再重建。
- 池租借首选 `Bag.Rent/Spawn`；提前归还必须在同一个 Bag 上 `Return/Despawn`，避免父 Bag 再次归还。

## 模块使用不变量

| Module / Interface | 业务侧必须守住的边界 | 详见 |
|---|---|---|
| 资源 `IAssetUtility` | 资源三件套同一 Context；动态借用进 Bag，Inspector 用 `AssetReference<T>`，SO/纯 C# 由持有者绑定。Load 前判初始化/位置状态；package 名用生成常量。 | guide §13 / ADR-0013 |
| 对象池 `IPoolUtility` | Context 所有权用 `RegisterOwned`；首次工厂/钩子配置生效。Pool 不替实例释放非托管资源，GameObject 池只在主线程使用。 | guide §7 |
| 配置 `IConfigUtility<TTables>` | 配置是全层只读 Utility；响应式界面订 `State`，启动/流程门禁 `await EnsureReady(token)`，不轮询 `Tables`。调用方取消只退出自己的等待，失败会抛原始异常。改表走 profile/生成菜单，生成目录不手改，`topModule` 不含 `System`。 | guide §16 / ADR-0009 |
| UI `IUIUtility` | 每个 Context 只挂一个 Toolkit/UGUI 入口；窗口仍是 View。Toolkit 窗口需无参构造，UGUI 窗口不覆写 Awake；Toolkit 异步点击用 `Bag.SubscribeClickAsync` 并透传 token，可预期失败在 handler 呈现；并发 Loading 用 `AcquireLoading` 租约。 | guide §17 / ADR-0016/0020/0037 |
| 存储 `IStorageUtility` | `[Serializable]` 类整存整取，key 是持久契约；Save 必须 await，Load 无主/备数据返回 null；迁移用数据 `Version`。 | guide §18 / ADR-0021 |
| 音频 `IAudioUtility` | BGM 单通道编排；一次性 SFX 自动回收，循环 handle 必须 Stop 或进 Bag。跟随对象的 3D 音源直接用 `AudioSource`。 | guide §19 / ADR-0022 |
| 流程 `IGameFlow` | 只表达宏观阶段，每次进入 new `FlowState`；私有能力放状态子 Context/Bag。转换“串行 + 最新意图胜”；GoTo 必须 await/显式观察，OnEnter 转向交给导航 Adapter 后直接 return。 | guide §20 / ADR-0023 |
| 本地化 `ILocalizationUtility` | 文本订 `TextRevision`，字体/按语言资源只订 `Locale`。Source 用 `Unavailable/Missing/Found` 区分加载中与真缺失；仅真缺 key 警告。 | guide §21 / ADR-0024 |
| 字体 `MonoLocaleFonts` | 根 Context 一份组件接管主字体 fallback；TMP/Toolkit 分开配置，同一主字体不能重复接管。 | guide §22 / ADR-0025 |
| 网络 `IHttpUtility/IWebSocketUtility` | HTTP 返回 UniTask，WS 推送转 Framework Event；失败抛 `NetworkException`，外部取消保持 OCE；重试/重连写在业务层。 | guide §25 / ADR-0028 |
| UI 嵌入 `UI.Bridge` | 需要随 Toolkit 裁剪/滚动才用 RenderTexture；顶层覆盖优先 overlay。隔离 layer 并从主相机排除。 | guide §27 / ADR-0033 |
| 日志 `Log` | 新代码不用裸 `Debug.Log`；Trace 插值无副作用。玩家追踪在启动时配置 File sink 并 `CaptureUnityLogs()`。 | guide §28 / ADR-0034 |

## 模块入口与配置可发现性

- 资源、存储、音频、池等纯 C# Utility 默认按 Interface 注册并由 Context 持有所有权；需要 Inspector 配置时再选 Mono Adapter。
- 新模块配置 Profile 必须能从 `SSFramework/<模块>/` 菜单到达，并登记到配置总览；不要让生效配置只能靠翻目录猜。
- UGUI 与 UI Toolkit 是 Adapter，实现可替换；核心业务通过 `IUIUtility`/View 约束保持渲染中立。

## 命名与日志

- Framework 命名空间以 `Game.Framework.*` 开头；Demo 为 `Game.Framework.Demo.*`；测试为 `Game.Framework.Test`。
- 扩展方法需正确 using：Model、Systems、Utility、View 等各在自己的命名空间。
- 新业务日志统一 `Log.Info/Warning/Error/Trace`。`Trace` 只放纯读取插值；新增 sink 包装层若转发到 Unity Console，要保持 `[HideInCallstack]` 链完整。
