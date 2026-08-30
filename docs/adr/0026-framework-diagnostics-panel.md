# ADR-0026：框架诊断面板 —— 内核采集层 + Editor 总览窗口

**Status:** Accepted（2026-07-05）

## Context

roadmap 中期第六项：把散在各组件 Inspector「运行时诊断」折叠组里的信息聚合成一个 Editor 总览窗口——定位是「框架状态一屏看穿」的调试与泄漏排查入口。

要解决的真问题：框架的核心状态是**分布式**的——Context 嵌套成作用域树、每个 Container 有本地注册表、每个 Context 有独立事件总线、DisposableBag 到处创建、对象池按类型/prefab 散落。单点诊断已有（`MonoGameContextBase` / `MonoLayerBase` 的「运行时诊断」折叠组、`MonoPoolUtility` 的池概要、`FrameworkSelfCheck` 真机冒烟），但回答「**整个运行时现在什么样**」（有几个 Context 活着、谁挂在谁下面、哪个 Subject 订阅数在异常增长、Command 在按什么顺序跑）需要逐个点开场景节点拼图——纯 C# Context（GameFlow 状态子 Context、SelfCheck 自检 Context）更是根本没有 Inspector 可看。

既有可复用件与缺口：

- `Container.LocalRegistrations`（已有）：本容器本地契约键，Inspector 折叠组在用。
- `PoolUtility.GetPoolDiagnostics()`（已有）：池概要字符串，但只有空闲数、没有借出数。
- `ICommandSystem` 文档里的 `LoggingCommandSystem` 装饰器（只是 XML doc 示例，**没有实物**）。
- **最大缺口**：没有「存活 GameContext 登记表」——不知道现在有哪些 Context、父子关系如何；纯 C# Context 连名字都没有。
- 事件订阅数：R3 `Subject<T>` 不暴露 observer 计数，需要框架自己在订阅通道上计数。

## Decision

### 1. 分层：内核采集层（`Core/Diagnostics/`）+ Editor 展示层（`Game.Framework.Editor`）

采集必须在内核（数据在内核私有字段里），展示只能在 Editor 程序集（EditorWindow）。两层间经 `InternalsVisibleTo("Game.Framework.Editor")` 白盒访问——诊断数据面是框架内部实现细节，**不进公共 API**（业务拿不到登记表，杜绝「业务遍历所有 Context」的滥用通道）；Editor 程序集本就是框架自身的一部分，与 Test 程序集同待遇。

### 2. 内核采集层 `FrameworkDiagnostics`（static，`#if UNITY_EDITOR` 收集）

- **存活 Context 登记表**：`GameContext` 构造时登记、`Dispose` 时注销。父子关系不新建机制——沿既有 `Container._parent` 链反查「哪个存活 Context 拥有父容器」还原作用域树。
- **DisposableBag 计数**：构造 +1、Dispose -1，只记全局存活数与累计创建数（泄漏排查看趋势即可，不做逐实例列表——登记每个 bag 的持有点要抓栈，分配大、收益低）。
- **事件订阅计数**：`GameContext.RegisterEvent` 通道上包一层计数 disposable（per-Context `Dictionary<Type,int>`，订阅 +1、退订 -1）。`Bag.Subscribe<TEvent>` 走的就是这条通道，全量覆盖。
- **采集条件编译 `UNITY_EDITOR`**（比框架惯用的 `UNITY_EDITOR || DEVELOPMENT_BUILD` 更窄）：展示层是 EditorWindow，真机采了也没人看；登记表持强引用会改变 GC 行为，不该带进真机。真机诊断已有分工——冒烟走 `FrameworkSelfCheck`，日志走 `FrameworkLog`。
- **域重载/Play 会话边界**：`RuntimeInitializeOnLoadMethod(SubsystemRegistration)` 清空登记表与计数，上一次 Play 泄漏的 Context 不跨会话残留（关闭 Domain Reload 的 Enter Play Mode 同样正确）。

### 3. `GameContext.DebugName`：纯 C# Context 获得身份

`GameContext` 增加 `DebugName` 属性（诊断专用，业务逻辑不得依赖）。框架内三处创建点自动命名：`MonoGameContextBase` 用 GameObject 名、`GameFlow` 用状态类型名、`FrameworkSelfCheck` 用固定名。未命名的显示 `GameContext#哈希`。这是唯一新增的运行时公共成员——一个 string 字段，Release 下零行为。

### 4. `LoggingCommandSystem`：把文档示例变成实物（顺带验证可插拔设计）

`Core/Systems/LoggingCommandSystem.cs`，public、**opt-in**——根 Context 的 `InstallBindings` 里把 `new CommandSystem()` 换成 `new LoggingCommandSystem()` 即接入（就是 `ICommandSystem` XML doc 里教的装饰器姿势，正好验证「命令分发可替换」不是纸面能力）：

- **静态环形缓冲**（默认 256 条）记录命令流水：开始时刻/帧号、命令类型名、同步/异步、耗时、异常、Context 名。多实例共写同一条时间线（多 Context 各自注册也能看到全局顺序）。
- **完成时落账**：同步命令执行完立即记录；异步命令经 wrapper `await` 完成（含异常/取消）后记录，耗时才有意义。在途异步不显示——诊断面板不是 profiler。
- **零装箱红线**：只记 `typeof(T).Name`（缓存串），不对 struct 命令调 `ToString()`；六个重载全部泛型直转发 `_inner`，struct 路径保持零装箱。
- 记录本身不条件编译（类是 opt-in 的，挂了就要工作——Development Build 真机也能用它排查）；可选 `echoToConsole` 逐条打日志，默认关。

### 5. 对象池补「借出」计数

`IObjectPool<T>` / `IGameObjectPool` 增加 `CountActive`（Rent/Spawn +1、Return/Despawn -1）——roadmap 要的「占用/空闲」里缺的那半。`GetPoolDiagnostics()` 字符串同步补上（`MonoPoolUtility` Inspector 白得增强）。边界：C# 池不跟踪实例归属（刻意，见 ADR-0007），归还外来实例会让计数漂移——钳到 ≥0 并在文档标注「误用下是近似值」；GameObject 实例被外部 Destroy 不再归还，计数停在借出侧——这本身就是要暴露的信息。

### 6. Editor 窗口 `FrameworkDiagnosticsWindow`（菜单 `SSFramework/诊断与分析/运行时诊断`）

**UI Toolkit 实现的调试器风格布局**（TreeView / MultiColumnListView 现成控件，也顺应框架「面向 UI Toolkit」的技术栈方向），全部只读：

1. **左：Context 作用域树**（TreeView）——存活 Context 按 `Container.Parent` 链成树，节点带徽标（Main / Mono·C# / →Main 回退）与「注册 · 订阅 · 存活时长」摘要；工具栏搜索按「名称 / 注册契约 / 事件类型」过滤（保留祖先链）；双击定位场景对象。
2. **右：选中 Context 明细**——本地注册表（契约 → 实例，运行时 / 构建时 / 工厂徽标——**绝不触发工厂**，诊断不得改变被观察系统；Unity 对象带定位按钮）、事件订阅计数（异常增长 = 泄漏嫌疑）、本地 `IPoolUtility` 池借出 / 空闲。
3. **下：Command 流水表格**（MultiColumnListView）——`LoggingCommandSystem` 环形缓冲，新的在上；耗时着色（≥1 帧 / ≥100ms）、过滤 + 仅错误开关 + TSV 复制导出；未接入时显示一行接入指引，不报错。
4. **顶栏计数条**：存活 Context / Bag 存活（各带约 30 秒窗口的趋势 sparkline，Painter2D 自绘）/ 命令累计。Play 模式外树区显示提示（登记表只在运行期有内容）。

500ms 定时**增量刷新**：结构签名没变只重绑可见行（树的展开状态按稳定 id 记忆、选中与滚动不丢），变了才重建；「自动刷新」可暂停冻结快照。

### 7. 刻意不做

- **运行时 overlay / 真机面板**：真机分工已定（SelfCheck 冒烟 + FrameworkLog + Development Build 下的 LoggingCommandSystem 日志），面板是 Editor 工具。
- **历史曲线 / 采样存储**：泄漏排查看「当前值 + 趋势肉眼观察」够用；要精确追踪用 Unity Profiler / Memory Profiler，不重造。
- **订阅点堆栈捕获**（谁订阅的）：每次订阅抓栈分配巨大；计数 + Context 归属已能把嫌疑范围缩到单个 Context 的单个事件类型，剩下的搜代码即达。
- **Bag 逐实例登记 / 命令 payload 展示**：同上，成本压不住收益。
- **demo 章节**：面板没有业务 API，五件套的「demo」不适用——guide 章节 + 现有 demo 场景（多上下文/流程/池章节本就是最好的观察素材）即覆盖。

## Consequences

- 「Context 树 + 注册表 + 事件订阅 + 命令流水 + 池占用」一屏看穿，纯 C# Context（GameFlow 状态）首次可观察——ADR-0023 留的口子（「要看运行时状态，走框架诊断面板」）兑现。
- 采集层全部 `UNITY_EDITOR` 条件编译或 opt-in，玩家包零成本；Editor 下每订阅多一个计数 wrapper 分配，可接受。
- `IObjectPool<T>` / `IGameObjectPool` 接口加成员是破坏性变更——两接口实现均在框架内（ADR-0007 自研池），无业务实现者，此阶段可加。
- `LoggingCommandSystem` 从文档示例变成实物后，`ICommandSystem` 的 XML doc 示例改指实物，文档与代码不再有「教你写一个其实已经有」的漂移。

**2026-08-23 失败宿主补诊断，2026-08-25 根因聚合：**初始化事务失败的 `MonoGameContextBase` 不会发布 `GameContext`，因此无法进入 `LiveContexts` 作用域树。Core 提供 Editor-only 只读快照（状态、已解析父级、Context、异常），窗口复用场景扫描在树上方单列问题；不制造假的 Context，也不增加静态强引用登记。父级初始化失败会被子级包装并继续传播，窗口按“同一最深异常对象 + 实际 Mono 父子链”聚合为根因组，显示最先失败宿主与受影响链；相同类型 / 文案但无父子关系的异常仍保持独立。没有异常的 `Uninitialized/Initializing` 只按实际父子链聚合为“时序提醒”，不计入根因数，也不宣称已经发生异常。运行中标为“当前 Play”，退出后保留的 `Failed` 标为“历史证据”，避免把上次运行残留误读成当前故障。Edit Mode 下普通 MonoBehaviour 尚未执行 `Awake`，`Uninitialized` 是正常场景资产状态，不显示为异常；Play Mode 中激活宿主仍为 `Uninitialized/Initializing` 才提示时序问题。该边界由 Editor 纯分析与状态分类契约测试锁定。

**2026-08-29 Inspector 渐进披露：**Framework Mono 组件不再各自重复显示“打开完整框架诊断”按钮；总览入口只保留在顶部菜单、工具中心和 Demo 教学中。组件内的“运行时诊断”按目标实例记录展开状态并默认折叠，只有展开后才枚举注册契约、服务状态和可选 Module contributor。折叠不等于隐藏故障：失败 Context、当前 Play 中激活但仍未初始化的 Context，以及没有解析到 Context 的激活层组件仍显示一条摘要；普通 Edit Mode 不制造噪音。原生 fallback、Odin Adapter 与默认 Header 接缝复用同一绘制器，且 Odin 被禁用或排除时必须明确归还原生 fallback，不能落到不含诊断的 `OdinEditor`。
