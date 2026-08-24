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
| 资源 `IAssetUtility` | 资源三件套挂同一 Context；动态借用走 Bag，Inspector 引用走 `AssetReference<T>`。SO/纯 C# 内的 AssetReference 不自动递归绑定，由持有者 `BindAssetReferences`。未初始化包直接抛错；package 名用生成常量，不写裸字符串。 | guide §13 / ADR-0013 |
| 对象池 `IPoolUtility` | Context 所有权优先 `RegisterOwned`；首次工厂/钩子配置生效。高频领域热路径自己维护租借列表；Pool 不替实例 Dispose 非托管资源。GameObject 池主线程独占。 | guide §7 |
| 配置 `IConfigUtility<TTables>` | 配置是全层只读 Utility，不占 Model；加载前 `Tables` 为 null，等 `State` 不轮询。改表走 profile + 生成菜单；生成目录勿手放文件，`topModule` 不含 `System` 段。 | guide §16 / ADR-0009 |
| UI `IUIUtility` | 同一 Context 只挂一个 Toolkit 或 UGUI 入口。窗口仍是 View；元数据用 `[UIWindow]`，Toolkit 窗口需无参构造，UGUI 窗口不要覆写 Awake。过渡期间框架挡输入；关闭的逻辑状态先于动画收尾。异步任务显示全局 Loading 用 `using var loading = await AcquireLoading(...)`，不要用 Show/Hide 配对表达并发 owner。 | guide §17 / ADR-0016/0020/0037 |
| 存储 `IStorageUtility` | `[Serializable]` 类整存整取，key 是持久契约；Save 失败抛异常并必须 await，Load 无可用主/备数据返回 null。迁移由数据 `Version` + 业务 switch 完成。 | guide §18 / ADR-0021 |
| 音频 `IAudioUtility` | BGM 交给单通道编排；一次性 SFX 自动回收，循环音效 handle 必须 Stop 或进 Bag。持续跟随对象的 3D 音源直接用 `AudioSource`。音量持久化归业务。 | guide §19 / ADR-0022 |
| 流程 `IGameFlow` | 只表达启动/登录/大厅/战斗等宏观阶段；每次进入 new 一个 `FlowState`。阶段私有服务/订阅/资源放状态子 Context/Bag。转换“串行 + 最新意图胜”；OnEnter 内转向后直接 return，不能 await 自己触发的 GoTo。 | guide §20 / ADR-0023 |
| 本地化 `ILocalizationUtility` | UI 绑定 key 并订 Locale；动态参数用 `CombineLatest`。缺 key 保留裸 key + 一次警告，不用空串掩盖。语言列表、选择持久化、复数规则归业务/上层 Adapter。 | guide §21 / ADR-0024 |
| 字体 `MonoLocaleFonts` | 根 Context 一份组件接管指定主字体 fallback；TMP/Toolkit 分别配置。同一主字体不可被两份组件接管；OS 族名用英文并按平台给候选。 | guide §22 / ADR-0025 |
| 网络 `IHttpUtility/IWebSocketUtility` | HTTP 请求响应返回 UniTask；WS 推送映射 Framework Event。非 2xx 动词门面抛 `NetworkException`，外部取消保持 `OperationCanceledException`。JsonUtility 消息用字段；重试/重连显式写在业务层。 | guide §25 / ADR-0028 |
| UI 嵌入 `UI.Bridge` | 需被 Toolkit 内容流裁剪/滚动才用 RenderTexture Bridge；简单顶层覆盖优先 overlay。隔离专用 layer 并从主相机 cullingMask 排除；交互模式不承诺 IME/多点触控。 | guide §27 / ADR-0033 |
| 日志 `Log` | 新代码统一 `Game.Framework.Logging.Log`，不要裸 `Debug.Log`。日志同时经过全局与 sink 的 `MinLevel`；Trace 用插值处理器且参数不得有副作用。玩家问题追踪在启动时配置 File sink 并 `CaptureUnityLogs()`。 | guide §28 / ADR-0034 |

## 模块入口与配置可发现性

- 资源、存储、音频、池等纯 C# Utility 默认按 Interface 注册并由 Context 持有所有权；需要 Inspector 配置时再选 Mono Adapter。
- 新模块配置 Profile 必须能从 `SSFramework/<模块>/` 菜单到达，并登记到配置总览；不要让生效配置只能靠翻目录猜。
- UGUI 与 UI Toolkit 是 Adapter，实现可替换；核心业务通过 `IUIUtility`/View 约束保持渲染中立。

## 命名与日志

- Framework 命名空间以 `Game.Framework.*` 开头；Demo 为 `Game.Framework.Demo.*`；测试为 `Game.Framework.Test`。
- 扩展方法需正确 using：Model、Systems、Utility、View 等各在自己的命名空间。
- 新业务日志统一 `Log.Info/Warning/Error/Trace`。`Trace` 只放纯读取插值；新增 sink 包装层若转发到 Unity Console，要保持 `[HideInCallstack]` 链完整。
