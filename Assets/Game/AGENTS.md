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
- 可增删重排的集合用 `ObservableList<T>`，只读暴露 `IReadOnlyObservableList<T>`；不要用 `RP<IReadOnlyList<T>>` 迫使 UI 整表重建。UI 增量绑定用 `Bag.BindList`，factory 只处理当前行（构造、配置并把行内订阅/资源登记进子 Bag）；factory、挂/摘/移回调及子 Bag 的 Dispose 回调都不得同步修改正在绑定的同一集合。初始化失败会回滚，运行期回调失败会终止该绑定，修复后重新绑定；超大虚拟化列表直接用 Toolkit `ListView`。
- Command 默认 `readonly struct`。只有需要 `[Inject]` 字段时才用 class；struct 只能经 `Execute(ICommandContext ctx)` 访问层，避免 `this.GetXxx` 的接口装箱。返回值 struct Command 在极热路径可显式写双泛型重载避免一次装箱。
- 异步 Command 固定接收 `CancellationToken cancellationToken`，内部只使用这个已合并生命周期的参数；组合子命令经 `ctx` 并透传 token。Mono View 无参调用自动链接 View 销毁与 Context；显式可取消 token 是 View 侧生命周期覆盖（替代 Mono 销毁令牌），Context 始终保留。纯 C# View 要跟随窗口/交互生命周期时显式传其 Bag/host token。
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
- 层感知注册要求具体类型恰好属于 Model / System / Utility 之一；同时实现多个层标记是分层错误，Builder、运行时 Register 与 Mono 自动挂接都会在写 Container 前拒绝。需要选择性暴露非分层能力时用低层值绑定显式列 contract，不要用多层类型绕过权限。

## DisposableBag 是默认生命周期入口

`MonoXxxBase.Bag` 在 OnDestroy 释放。订阅、资源 handle、池租借和任意 `IDisposable` 都登记到 Bag；纯 C# 用 `new DisposableBag(ctx)`，Command 内用 `using var bag = ctx.CreateBag()`。

- 订阅使用 `Bag.Subscribe(...)`；状态流订阅即得当前值，跳过初值用 `.Skip(1)`。复杂数据流用 R3 操作符组合，不为个案扩张 Bag API。
- 资源借用使用 `await Bag.Load<T>/LoadScene`；查询、下载器等非所有权操作经 `IAssetUtility`。
- 局部阶段用 `Bag.CreateChild()`；按清理时机命名 `_enableBag/_roundBag`，对应回调先 Dispose 再重建。
- 池租借首选 `Bag.Rent/Spawn`；提前归还必须在同一个 Bag 上 `Return/Despawn`，避免父 Bag 再次归还。

## 按需加载模块契约

资源、池、配置、UI、存储、音频、流程、本地化、字体、网络、UI Bridge 与日志的详细调用契约集中在 `docs/framework-guide.md` 对应章节及其 ADR；不要把所有可选模块的细节常驻到每个业务与 Framework 任务。开始修改某一模块前，先读取该章节和 ADR，再以本文件的架构、生命周期与所有权规则统领实现。

跨模块仍需始终遵守：异步调用必须观察异常并透传 token；动态借用进入 Bag；Context 拥有的服务用 owned 注册；持久 key、配置生成目录和生成常量属于稳定契约；外部取消保持 `OperationCanceledException`，不可伪装成普通失败。

## 模块入口与配置可发现性

- 资源、存储、音频、池等纯 C# Utility 默认按 Interface 注册并由 Context 持有所有权；需要 Inspector 配置时再选 Mono Adapter。
- 场景资源运行时只挂 `AssetUtility`：包、模式、CDN 与下载设置内嵌在 `Settings`，自动初始化由 Utility 自己编排；`Settings` 集合只读，场景配置在 Play 前编辑，代码引导在 Start 前用 `Configure` 一次提交，不能靠强转集合或继续修改原 DTO 热换；旧 `AssetSystemConfigModel` / `AssetInitSystem` 只用于迁移已有场景，新代码不要继续接线。
- 新模块配置 Profile 必须能从 `SSFramework/工具中心` 的 Module 工作台到达，并登记到 `SSFramework/配置中心`；不要让生效配置只能靠翻目录猜。顶层菜单只导航，创建/生成动作放进工作台并说明影响。
- UGUI 与 UI Toolkit 是 Adapter，实现可替换；核心业务通过 `IUIUtility`/View 约束保持渲染中立。

## 命名与日志

- Framework 命名空间以 `Game.Framework.*` 开头；Demo 为 `Game.Framework.Demo.*`；测试为 `Game.Framework.Test`。
- 扩展方法需正确 using：Model、Systems、Utility、View 等各在自己的命名空间。
- 新业务日志统一 `Log.Info/Warning/Error/Trace`。`Trace` 只放纯读取插值；新增 sink 包装层若转发到 Unity Console，要保持 `[HideInCallstack]` 链完整。
