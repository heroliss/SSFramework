# SSFramework 持续完善计划

> 当前项目健康度与后续优化的工作入口。功能路线看 `roadmap.md`，架构边界看 `framework-module-map.md`，已定设计看 `adr/`。

## 目标与方法

目标不是把文件机械拆小或堆更多 API，而是让框架在真实 Demo、Outpost 垂直切片和教学过程中持续暴露问题，并把每个问题闭环为：

1. **现象与证据**：能在业务切片、测试、诊断或文档矛盾中复现；
2. **Module / Interface / Seam 判断**：问题属于职责、依赖方向、生命周期还是 Adapter 接线；
3. **最小设计决策**：必要时写 ADR，明确未选择方案；
4. **实现 + 契约测试**：修根因，不只修调用点；
5. **Demo + guide + AI 规则同步**：教学和协作约束不能继续推荐旧路径；
6. **全量验证**：编译、EditMode、PlayMode、Demo 防腐检查与文档一致性。

## 已验证基线（2026-08-31）

| 维度 | 当前事实 |
|---|---|
| Unity | 6000.3.22f1 |
| Framework Module | 31 个 asmdef Module（含测试与可选 Odin / HybridCLR Editor Adapter）；依赖与删除测试见 `framework-module-map.md` |
| Demo | 35 个自动发现章节；Catalog 集中拥有 Adapter 生命周期，并按 Capability / Concept / Workflow 校验真实 Build 教学语义 |
| 教程 | `framework-guide.md` 28 章 |
| ADR | 0001–0046；0040 为 UPM-aware 源码目录，0041/0042 补齐依赖证据，0043 收口 Editor 菜单与工作台，0044 固化 Unity CLI 工程外 Adapter 边界，0045 拆分资源与 HybridCLR 构建依赖，0046 收敛资源运行时入口 |
| 测试 | PlayMode 599 + EditMode 593，共 1192 项全绿；交互式 MCP 后台运行且 PlayMode 先预检，命令行入口默认 EditMode + PlayMode |
| Demo CodeRef | 315 处可打开源码跳转；完整门禁通过后以精准命中为基线，注释、文案与外部文档路径不计入源码构造点 |
| AI 常驻规则预算 | 最深 AGENTS 链 30.48 KiB，低于 Codex 默认 32 KiB 项目指令上限；本轮已压缩 Demo 教程式规则，新增常驻规则前仍须优先外移可测试/可按需加载内容 |

## 已完成的高优先级闭环

### P0 · Container 生命周期与绑定模型

- 新增 `RegisterOwnedFactory`，补齐“延迟解析依赖 + Context 拥有 IDisposable”组合；Outpost 本地化从泄漏路径迁移。
- 内部 `ContainerBinding` 显式区分值与 Factory，修复 `Func<Container, object>` 普通值被误执行，并让多 contract 共享同一诊断状态。
- 注册值 / Factory 结果做 contract 校验；循环 Factory fail-fast；Eager 构建失败释放已创建 owned 产物。
- Context Dispose 后禁止解析、注入、订阅与动态注册；取消回调异常不再阻断事件和 owned 服务级联释放。
- 补齐 Build 前失败事务：Builder 暂管 owned 并可 using 回滚，GameContext 构造失败释放 Container，Mono Context 只发布完整 Ready 状态；FlowState 安装失败不再泄漏或让 GoTo 永久 Pending。
- 收紧装配事务的两个遗漏：值绑定整批、公开 Inject 与动态 Register 都在副作用前预检 Context 归属，禁止同一 `IHasGameContext` 跨作用域形成注入 / 解析双重真源；OwnedFactory 的错误 contract 待提交产物会立即回滚，清理失败仍保留最初契约异常。
- Demo/Outpost/Test 内嵌服务器兼容 Mono 直接抛出的端口 `SocketException`，构造期 HTTP/WS 半启动会逆序回滚；Demo server 改为 Eager OwnedFactory 纳入 Build 事务。
- Container 与本地化两章 Demo、guide、AGENTS、ADR-0003/0019/0024/0035 和契约测试已同步。

### P0 · 测试与文档可信度

- `Tools/run-tests.ps1` 默认顺序跑 EditMode + PlayMode，分平台保留 XML / 日志，区分测试失败与基础设施失败，零测试不允许假绿。
- Unity 路径支持显式参数、环境变量、默认 Hub、Hub 次级安装目录和 Installer 注册表；非零 Unity 退出码不能被 XML 误判为成功。
- README 的测试、Demo 与教程规模按实测更新；Unity MCP 测试参数名修正；历史 ADR 的当前测试入口同步。

### P1 · 教学与 AI 可维护性

- 三层 AGENTS 常驻规则压缩并重新路由；最深链从约 60 KiB 降到约 23 KiB。
- repo skills 以 `.agents/skills` 为跨工具正文，`.claude/skills` 只做路由；当前 3 个 Skill / 6 个发现入口均通过 validator。
- Demo 目录校验 Id / Title / Category / Order / Summary；CodeRef 防腐校验已实跑。
- 新增跨工具 AI 协作指南与 Framework Module 地图。
- 新增跨工具 PlayMode 自动化预检：显式保存有路径脏场景、未命名场景 fail-fast，不用全局 Hook 劫持人工 Play；Editor 契约测试锁定“整批先验证再写入”，并以每用例唯一且显式持有的临时目录防止误删用户资产。
- 新增后台优先的 Unity 自动化 Skill：`editor_unfocused` 只作观察值，测试按真实进度轮询，不再固定抢占 Windows 焦点；原生模态框与真实输入验证才升级到 OS UI。
- Editor 工具补齐响应式布局：诊断/配置窗口按宽度重排，`AssetReference` 在窄 Inspector 自动纵向降级，UI Binding 的 Inspector、节点/总览 Popup 与 Overlay 在窄宽度或低工作区仍保留全部操作。
- Popup 高度按所在显示器工作区预算并把完整内容放进滚动视口；布局测试覆盖极窄宽度、负坐标显示器、无效分辨率与偏好状态恢复，不会为测试意外创建配置资产或残留全局 `EditorPrefs`。

### P1 · Demo 教学质量与分层术语对齐

- 为 35 章建立“定位 → 可操作行为或可验证样板 → 设计取舍 → 适用边界/下一步”的渐进教学契约；概念章不为凑按钮伪造交互，顶部 Summary 由运行期强制 ≤160 字、≤2 句，避免导航说明挤成正文。
- Demo 外壳新增“本组 / 全部”进度与章节底部上一步/下一步导航；入门/核心提示顺读，能力/进阶明确可按需跳转，实际按钮切章与滚动复位已验证。
- `DemoModuleHost` 在真实 Build 中记录教学语义，Catalog 按能力/概念/工作流分别检查定位、解释结构、交互或步骤；源码注释、死代码和早退不再能靠 token 数量假绿。
- 场景依赖缺失统一用结构化降级页说明“为什么不可用 → 如何恢复 → 接下来怎么学”，并强制提供接线源码；UGUI、UI 框架、多 Context 与字体的顶层早退已迁移。
- 新建独立 Demo PlayMode Module，在真实 DemoScene 中穿过 Context、Catalog 与 Shell 逐章 Build 35 个 Adapter，并用真实 UGUI/UI 框架章节覆盖降级路径；CodeRef 精准源码构造继续由编辑器测试逐项防腐。
- 移除 Shell 在每章重复注入的“新手导览”，只在「框架总览」说明一次阅读路线；入门与核心主线改由正文承接前置知识、解释易混关系并提示下一站。首次接入与发布期裁剪拆章，服务注册生成移到进阶，避免新手路径突然跳进 asmdef / Linker / HybridCLR 细节。
- 重写入门地图，并为 Counter / Model / Command / System / Event 补上选择标准、代价、生命周期与反例；8 个过长章节摘要完成收束，实际 Game View 已检查首屏、对照表和 System 深度说明。
- Demo 实战发现 `IShopSystem` 泄漏 `ICommandContext`：改成窄业务接口 `TryBuyPotion()`，WalletModel 由 Implementation 注入，并用购买不变量测试锁定。
- 修正框架层术语漂移：简单原子 Command 可直接写 Model，System 承载可复用/多步规则；Utility 可持有基础设施状态但不持有业务状态。源码 XML doc、README、guide、roadmap 与 ADR-0001 已同步。
- `DemoModuleBase` 明确为教学目录 Adapter，而非第六层；运行期直接实现已有 `IView`，不再手写复制三项权限。`IDemoModule` 继续只描述目录生命周期，测试锁定 View 可用能力与 `GetModel / GetSystem / SendEvent` 禁权，避免两种 Interface 身份互相污染。
- UI 融合章新增 128px 低清/正常预算即时切换，把视觉异常变成可复现教学实验；实战修复 RT 两轴分别钳制造成的拉伸，并以托管 `CanvasScaler` 分离逻辑布局与采样分辨率，确保降预算只变糊、不变形也不重排。纯尺寸契约、实际 Game View 与低清按钮 Raycast 均已验证。

### P1 · Demo 异步动作生命周期

- `DemoModuleHost` 新增 `AddAsyncActionRow(Func<CancellationToken, UniTask>)`：任务进行中禁用当前按钮、防双击重入，未接异常统一进入框架日志，切章、UIDocument 重建与 Shell 销毁会先取消 Host 再 Teardown Module。
- 12 个章节共 61 个异步按钮全部走专用入口，不把 `async` lambda 塞进 `Action` 退化为 `async void`；静态门禁按 C# 词法区分代码、字符串和注释，并检查 `AddActionRow` 调用体不能藏 `.Forget()` / `UniTaskVoid`。
- 下载器/缓存、资源更新/修复、profile 白盒损坏步骤、对象池 Prewarm/Trim 等共享资源增加组级互斥；资源初始化吞取消的边界在 Demo 调用点恢复为章节取消，用户主动取消仍可就近反馈。
- 框架资源异步所有权下沉到 `AssetUtility`：初始化按包 single-owner，调用者取消只离开共享等待；三种 `ClearCache*` 与 `UnloadUnusedAssets` 按包 FIFO 串行，取消不再提前释放仍有 YooAsset operation 在跑的维护 lane。可控 fake provider + Edit/PlayMode 契约测试锁定重入、异常与参数快照语义。
- 实战继续暴露“Utility 局部 lane 看不见 YooAssets 进程级共享包”的缺口：Yoo Adapter 新增按 `ResourcePackage` 身份共享的公平 Reader/Writer 协调器，把跨 Provider 的 Load/Download 与初始化/维护纳入同一物理终态；清缓存以缓存世代淘汰旧 downloader，取消后的弃置 handle 与后台异常都有明确收口，并由独立 Adapter EditMode 测试程序集锁定。
- 独立复查继续修出两处黏性边界：已完成 downloader 的后续调用必须重新入 Reader 队列再验世代，不能越过 Clear 复用旧终态；`suspendLoad=true` 在 Unity 0.9 激活门交接 handle，不能等待只有调用方 `UnSuspend` 后才会成立的 `IsDone`。专用空场景 fixture 让该回归不依赖 Outpost 组合根与日志。
- `ShowToast` / Loading 入口补可选生命周期令牌并由核心、Toolkit、UGUI adapter 原样透传；测试锁定预取消与异步创建中取消不会在切章后延迟开窗。Loading 进一步以 `AcquireLoading → LoadingHandle` 表达并发 owner，最后一个 lease 释放才关闭，旧 Show/Hide 作为单 owner 兼容入口且不会越权关闭 active handle（ADR-0037）。

### P1 · Demo 目录与章节生命周期

- 新增内部 `DemoModuleCatalog` Module：根 `MonoDemoContext` 一次发现、校验并持有全部章节 Adapter，同一实例严格按 InstallBindings → Initialize → Build / Teardown 执行；Shell 只负责展示和选择，不再反射构造第二批实例。
- Catalog 同时拥有活动 `DemoModuleHost`，把“先取消 Host、再 Teardown 同一 Adapter”变成唯一释放出口；父子 GameObject 销毁顺序不确定时重复收尾保持幂等，取消回调/Teardown 清理期间拒绝重入激活，Build 失败会回滚且保留原异常。
- 直接 EditMode 契约覆盖实例身份、多轮 Build/Teardown、乱序、重入、外来 Adapter、Dispose 与失败回滚；Demo 编写规则和领域词汇同步禁止恢复双实例路径。

### P1 · Outpost 真实玩家路径冒烟

- 新建独立 `Game.Outpost.Smoke.Test` Module，用真实场景、Composition Root、Command、Flow、资源/UI Adapter 跑“标题 → 战斗就绪 → 撤离 → 结算 → 回标题”，不依赖 UI 坐标或私有反射。
- `BattleReadModel.IsReady` 把“状态已进入”和“异步配置/音频/模拟已可交互”分开，撤离与托管按钮在初始化前及结算收束期统一禁用；自然战败也立即关闭对外交互。
- 冒烟测试实测发现并修复组合缺陷：隔离当前场景时误删 Test Runner 自身根节点；撤离为关闭交互清掉导演 `_ready`，导致结算倒计时永远不再推进；UniTask 自定义 Enumerator 不兼容 Test Framework 的反射续跑器。
- 测试用同一父目录内原子重命名隔离并恢复 Outpost 存档，失败时保留完整备份；退出后断言战斗场景、Context、导演与 `Time.timeScale` 无残留，可在 Unity 失焦时运行。

### P1 · 日志接缝渐进收敛

- Localization、Storage、Pool、Network 及 Core Context / Injection 基础设施的运行时日志已迁移到统一 `Log` 门面。
- Fonts Runtime 的 5 处降级 Warning 已迁入同一 Seam：默认 Console 文案与 `MonoLocaleFonts` context 保持不变，捕获 sink 契约锁定 level / category / message 和一次性警告语义；字体 Editor 生成工具仍保留原生 `Debug.*`。
- UI Core、UGUI、Toolkit 与融合 Bridge 的 Runtime 错误/警告已迁入同一 Seam：生命周期异常带窗口类型与 hook 阶段，节点绑定保留窗口 context；UGUI Adapter 同步修正了“文档要求继承 `UGuiWindowBase`、Implementation 却只检查 `IUIWindow`”的浅校验，在层级/资源副作用前 fail-fast。
- Asset Core 与 Yoo Adapter 的 Runtime 失败/警告已迁入同一 Seam：空地址在 Core Interface 边界 fail-fast，Utility/System 诊断携带 Unity context，Yoo manifest/handle/type 失败保留 Adapter category，初始化 owner 保留原始 exception；第三方内部日志继续交给 Unity 日志桥，避免重复包装。
- Audio Runtime 的异步淡变/回收异常与 Dispose 后误用已迁入同一 Seam，保留 exception、category 与可用的 Unity context。Boot 的原生 `Debug.*` 已审计为刻意边界：AOT 引导必须在框架/热更程序集加载前运行，不为日志一致性反向引用 Core。
- Config Runtime 的清单、资源与表构造失败已迁入同一 Seam，保留具体服务类型、根 exception 与组件 context；同一原始失败也可由 `EnsureReady` 交还命令式调用方，日志不再代替控制流。
- 相邻公共 Interface 注释补齐 `LocaleFontChain` 的字体表快照、OS 资产所有权、Dispose 义务与 Apply 异常，以及 `LocaleFontProfile` 的只读查看和非所有权语义。
- 迁移按 Module 做定向回归并保留原 Console 文案与 Unity Object context；Logging 自身实现、第三方 Adapter 和编辑器工具不做机械替换。
- DisposableBag 补齐释放异常隔离：取消回调或单个 `IDisposable` 失败不会截断余下清理，并有契约测试锁定。
- `UIToolkitViewBase.BindTo` 以注入与 `OnCreated` 完整结束为提交点：失败、自释放或清理 hook 二次失败都会穷尽 View 自有 Bag / Root，且保留最初异常；Context 明确只是借用的作用域能力，不替创建 owner 释放独立 View。为守住“日志不能覆盖业务根因”，`Log` 同时隔离自定义 sink 的 `MinLevel` getter 与投递异常，坏去向不会阻断后续 sink。

### P1 · 配置就绪契约深化

- Demo 与 Outpost 实战暴露：命令式调用方都要手写 `WaitUntil(State is Ready or Failed)`，而 Failed 枚举无法交还资源缺失、清单错误或反序列化的原始异常。
- `IConfigUtility<TTables>` 新增 `EnsureReady(token)`：流程直接获得同一份 Tables 或原始失败；`State` 专注响应式观察，`Tables` 专注已就绪同步读取，三种 Interface 形态不再互相冒充。
- 调用方取消只脱离自己的 waiter，配置组件与 Context 继续拥有共享加载；owner 销毁才取消物理操作和剩余等待。完成信号只表达终态，根异常由 `ExceptionDispatchInfo` 保存，避免无人等待时出现未观察的 UniTask 异常。
- `MonoConfigUtilityBase` 在任何资源 I/O 前快照并校验清单，拒绝空项、重复项及空表根；Outpost 战斗启动迁移到新契约，Demo、guide、ADR-0009、领域词汇与业务规则同步解释选择标准和失败/取消边界。
- 独立 `Game.Framework.Config.Tests` 以真实 Mono Context + AssetUtility + 可控 Provider 覆盖 Start 前等待、稳定表根、原始失败与日志 context、调用方取消不截断 owner、owner 销毁取消物理加载，以及无效清单在 Adapter 工作前 fail-fast；测试随 Config 目录整体删除，不反向黏住通用 Test Module。
- 常用调用新增 Context 感知的 `GetConfig<TTables>()` / `EnsureConfig<TTables>(token)`：删除重复的 Utility 泛型解析和分散的未就绪判断，但不复制 readiness 状态机；Outpost 的长寿命 System 只在初始化 Seam 等待一次，之后继续缓存 `Tables` 直读。
- 明确拒绝静态 `TbItem` / `Tables.Current` 与默认逐表层级权限矩阵：前者会隐藏父子 Context、多配置集与测试身份，后者只镜像生成 schema。客户端/服务端归属用 Luban target、独立配置集或程序集处理，业务解释才建立领域查询 Adapter。
- Config 契约测试补充同步早读 fail-fast、层对象与 Command 解析同一 Context、快捷入口不触发二次加载；Demo 用真实索引器 `TbItem[id]` 展示最短安全路径，并说明高频调用应缓存表根。

### P1 · 本地化延迟 Source 失效语义

- `ILocalizedTextSource` 从 bool 查询升级为 `Unavailable / Missing / Found + Invalidated`，配置加载中不再被误判为永久缺 key；Adapter 违反 Found 非空契约时 fail-fast。
- `Locale` 只表达语言身份，`TextRevision` 汇总换语言与 Source 失效；文本 UI、动态参数和排行榜行都订后者，字体与按语言资源仍只订前者。
- Outpost 删除 Boot 对本地化配置 Ready 的硬等待；Demo 增加“不切语言也会刷新”的现场实验，框架测试使用实际 Toolkit `Label` 锁定绑定行为。
- ADR-0024 v2、guide §21、AGENTS、roadmap 与 Outpost 技术说明已同步。

### P1 · 资源地址四态查询语义

- Demo 实战暴露 `CheckLocationValid` / `IsNeedDownload` 的 false 同时表示包未就绪、地址无效或本地已有内容；多包调用方还容易误守卫默认包状态。
- `IAssetUtility` 深化为一次 `GetLocationState(package, location)`，明确 PackageNotReady / Invalid / AvailableLocally / RequiresDownload；具体初始化原因继续由正交的 `AssetInitState` 表达。
- Core 在包非 Ready 时不触碰 Adapter；Ready 后才组合 manifest 与缓存快照。旧 bool 从稳定 Interface 移出，仅以 `[Obsolete]` 扩展方法提供源码迁移，Provider SPI 保持不变。
- 可控 Provider 契约覆盖空地址、Idle / Pending / Initializing / Failed / Ready、地址有效性、本地 / 远端与多包隔离；Demo 改成一个四态按钮并解释设计取舍。
- ADR-0013、guide、资源流程图、领域词汇与业务侧 AGENTS 已同步，记录为何原“三态”候选最终必须是四态。

### P2 · Demo 可视证据补强

- ReactiveList 章为真实行 View 显示稳定实例号，并从 item factory 与 rowBag Dispose 两个 Seam 采集创建 / 释放 / 存活计数；新增 Replace 操作，Move 复用、Replace 重造一槽与逐行释放都能在画面直接核对。
- EditMode 契约不只比对最终文本，而是断言真实 `VisualElement` 引用、父子层级和 rowBag 释放状态，让增量绑定 Implementation 的身份与生命周期语义有可执行证据。
- UI Framework 章新增 Destroy / Cache 真实窗口对照：稳定实例号与 hook 计数展示重开身份；PlayMode 穿过 DemoScene 的 `MonoToolkitUI` Adapter，锁定 Destroy 重建、Cache 复用。
- 独立 Toolkit View 删除只做转发的 `CloseView` 回调：卡片直接 `Dispose` 自己，章节每轮 Build 只拥有一个当前实例清理项，反复开关不再线性保留已释放 View；真实 DemoScene 契约覆盖按钮旁挂载、关闭、重开与切章兜底。正式 Window 仍请求 `IUIUtility.Close(this)`，不把两种所有权压成含糊的自关闭 API。
- guide §17/§24 与 ADR-0016/0027 同步选择标准、代价和验证方法；修正列表 XML doc 对不存在 `BindListView` Interface 的陈旧引用，继续守住“虚拟化留给原生 ListView”的边界。

### P1 · Module 裁剪证据与依赖可见性

- 新增 `SSFramework/诊断与分析/模块与依赖`：以当前目标平台 Player 编译图确定候选，再读当前已编译 DLL 快照的元数据引用，避免把 auto-reference 的“编译可见”直接误算成代码消费；Unity 6000 可能返回 Editor 变体，故目标平台结论另由显式 DLL 门禁、HybridCLR 目标产物和真实 Player Build 验证。
- 报告 Core-only / Core + UGUI / Core + Toolkit / 全部 Runtime / 当前 HybridCLR 热更档位的原始托管闭包，并机器执行 Core、两个 UI 后端与 Bridge 的删除测试。窗口改为“健康结论 → 关键数字 → 通俗建议 → 常用组合卡片”的渐进披露；完整模块、热更配置、程序集清单和原始报告默认折叠，620px 以下按钮与指标卡纵排且使用内容高度，避免裁剪和重叠。
- Core、Fonts、Proto、UI 与两个后端的真实外部依赖全部回写 asmdef 显式声明，审计不再发现隐式依赖；ADR-0010/0027、Module 地图、guide §24/§26、Framework AGENTS 与 Demo 接入章同步依赖语义。
- 原始 DLL 字节明确不等于最终包体；先用它发现值得实测的候选，再以 WebGL/小游戏 Player BuildReport 决定是否拆 `ReactiveListBinding` 或 Core 能力，避免为理论体积制造浅 Module。
- 审计结论拆成 Error / Warning / Advisory / Clear：依赖错误、证据缺口与已知 `preserve="all"` 成本不再混成一个黄色提醒；无条件保留仍进入体积解释，但不会冒充结构故障。一次采集复用 Asset 路径、PluginImporter 与 Player / Editor 编译图输入，窗口显示进度和阶段耗时。

### P1 · 隔离 Player Build 体积证据

- 新增 `SSFramework/诊断与分析/真实构建体积`：在 `Library` 下创建隔离空工程，Core / UGUI / Toolkit / 全部四档只复制审计闭包中的 Runtime Module，未选目录、业务场景、HybridCLR 生成物与其 link.xml 不进入结果。
- 每档依赖清单按所选 Module 从当前 manifest 最小化；当前 BuildTarget / 脚本后端 / stripping 原样使用，不修改主工程设置。隐藏 Unity 子进程顺序构建，可请求“当前完成后停止”，不强杀正在写产物的进程；Profile key、队列状态、PID 与停止原因进入报告，本机运行根由 EditorPrefs 保存，结果路径按运行根与 key 重建，主 Unity Domain Reload / 重启后可重新附着或从结果继续，人工停止与自动证据漂移不会被重载清空或在最终报告中混淆。
- `report.json`、`report.md`、子进程日志与玩家产物共同保存；窗口显示可发布输出与相对 Core 差值。Unity BackUp / DoNotShip / 调试符号从默认比较中排除，原始 BuildReport 总量仍保留；所选程序集完整保留，明确将结果定义为体积上界，而非具体游戏的包体承诺。
- Windows IL2CPP 实测完成 Core 80.04 MiB、UGUI 99.80 MiB（+19.75 MiB）、Toolkit 101.90 MiB（+21.86 MiB）的可发布输出上界；同轮原始 BuildReport 为 573.36 MiB / 1.00 GiB / 1.08 GiB，验证了 BackUp / 调试证据不能混进默认比较。矩阵中主动触发 Domain Reload 后可自动续跑。该快照只验证实现，不作为 WebGL 基线。
- 架构取舍记录于 ADR-0038；模块地图、guide §26 与 Demo 接入章同步“快速托管闭包 → 隔离 Player Build → 正式产品构建”三级证据链。

### P1 · UPM-aware Module 源码接缝

- 新增 `FrameworkModuleSourceCatalog`，集中拥有 canonical Unity Asset Path、真实 Physical Path、源码根和 package id 之间的映射；支持 `Assets`、嵌入式 Package、registry/Git `PackageCache` 与绝对物理路径回转，并拒绝目录逃逸。
- Module Audit 不再把 CompilationPipeline 返回路径直接交给 `System.IO`，会严格读取项目与全部已注册 Package 的 `link.xml`；已登记候选不可读时 fail-fast，窗口显示每个 Module / Package 的源码所有者，定位仍使用稳定 Asset Path。
- 隔离体积探针从 Catalog 的真实目录复制源码，以程序集名隔离同名 `Runtime/` 叶目录，相同/嵌套源码域则拒绝制造虚假删除证据；JSON / Markdown 不落盘 PackageCache 绝对路径，但会保存过滤 Editor/Test 后实际复制文件的 SHA-256 指纹。Domain Reload 会逐档检查拓扑、package 与内容漂移，已移除档位不再被静默过滤。子进程模板在 run-owned `Inputs/` 中冻结，报告以已编译 Editor DLL、主探针源码和模板的联合指纹识别证据实现；模板和弹窗门禁仍按程序集源码域查找，不受无关 Package 同名文件影响。
- 架构取舍记录于 ADR-0040；这完成了 UPM 抽包前的工具链路径准备，但没有越权实现安装、卸载、版本解析或第二套 Package Manager。

### P1 · Odin 可选依赖与原生基线

- 保留 Odin 作为当前项目推荐的专业 Inspector / Validator 工具，但 Framework 的公共基线不再继承
  `SerializedMonoBehaviour`，Core、Fonts、UGUI Editor、通用 Editor、测试与隔离构建探针均不再声明 Sirenix 编译引用。
- `_targetContext` 与可序列化父 Context 改为 Unity 原生具体组件引用；纯代码父 Context 仍通过非序列化
  `IGameContext` 路径表达。显式代码父级在关闭自动层级搜索时仍然有效，避免把两个不同配置语义错误绑定。
- 新增原生 fallback Mono Inspector、字段/Header 诊断、资源包名称下拉与 UGUI 代码生成 Inspector；窄 Inspector
  会纵向重排，资源包 PropertyDrawer 的菜单回写对多对象使用新的 `SerializedObject`，普通校验失败走非模态反馈。
  可选 Odin Editor Adapter 以无持久化的临时 Editor 映射保留业务属性绘制并追加框架诊断；映射只替换原生
  fallback，按 Odin 的程序集分类/逐类型设置启用，并在配置资产更新后延迟重应用。字段 Drawer/Header hook
  覆盖其余场景，实际所有权由真实组件测试锁定。
- 在 Odin 仍安装的主工程中审计 6 个场景/Prefab 的 34 个旧 `serializationData` 节点，确认全部为空后通过
  Unity `ForceReserializeAssets` 迁移为原生字段；没有手改 YAML，也没有丢失对象引用。
- 删除测试通过 CompilationPipeline 定位 Assets/Packages 中的 Framework 生产源码和 asmdef，并读取已编译 DLL
  直接引用，阻止 Sirenix 重新渗入通用基线且拒绝零样本假绿；隔离空工程只复制
  `Game.Framework`，在 Windows IL2CPP / Minimal Stripping 下实际构建成功（0 error / 0 warning，发布输出
  66.64 MiB）。该数字是当前平台的完整保留上界，不是 WebGL 或具体游戏包体承诺。
- ADR-0015 取代“Core 硬依赖 Odin”的旧结论；`Game.Framework.Odin.Editor` 只承担 Odin 绘制 + Framework
  诊断这一真实共存增量，映射不写 Odin 配置，可整体删除且不改变 Runtime/资产布局。后续 Validator 规则、专用数据宿主或迁移器
  仍需独立证据，Adapter 不得随框架重新分发商业插件，不为目录对称制造空壳能力。
- UPM 长期按安装/版本/删除粒度组织 Core、YooAsset、UI、Protobuf、Yoo-HybridCLR 与可选 Odin Adapter，
  不机械按每个 asmdef 抽包。下一阶段重点是标准化 Git 依赖与 embedded NuGet DLL 的发布来源，并在干净消费工程
  验证安装 recipe，而不是复制第二套 Package Manager。

### P1 · Module 依赖证据完整性

- Module Audit 不再把 asmdef `references` 与 `precompiledReferences` 合并成一类：前者只匹配 asmdef 程序集，后者只在 `overrideReferences:true` 时匹配预编译 DLL。Framework、Demo、业务与可选 Odin Adapter 的直接 DLL 依赖已经迁移到真实字段；所有一方 Runtime、Editor 与测试 asmdef 均退出插件 Auto Reference，可删除 Editor Module 同时关闭预定义程序集隐式引用，并由门禁阻止回退。
- 工具中心与配置中心都改为 Module-local 注册：前者登记工作台导航，后者登记真实 Profile 类型、单例/多份语义和附属配置。中央窗口不再维护可选程序集字符串表，删除 Module 后两类卡片随域重载自然消失。
- 新增 `FrameworkEditorProfileCatalog`，把配置中心、批量 Profile owner 与只读审计重复执行的 `AssetDatabase.FindAssets` 收敛为按类型、按工程 revision 的发现快照；显式“重新扫描”与 `projectChanged` 失效，创建和业务校验仍由 owner Module 负责。
- Profile Catalog 接入已经覆盖全部 Framework owner：资源构建、HybridCLR 热更新、字体字集与场景快捷入口删除各自的 `_cached + projectChanged + FindAssets`，和 Luban、Protobuf、Service Installer、UI Binding 一起消费同一 revision。Catalog 的窄 stable-first loader 只修复“非空首路径已无法加载”的确定陈旧快照，不接管 owner 的 Warning、默认初始化或创建；单例仍把重复 Warning 限为每 revision 一次。五个固定路径自动创建 owner 会在任何默认目录 mutation 前强制刷新，拒绝 reparse、异类型或尚未导入文件占位，写入后再验证新资产就是稳定生效项；显式类型刷新不主动清空其它缓存，但 Unity `projectChanged` 仍可全局失效。旧 `Assets/Game/Settings` 资产不移动；字体配置同时复用 `FrameworkProjectSettingsLocation`，删除重复目录创建 Implementation。最终完整 EditMode 572/572、PlayMode 545/545，共 1117/1117 通过。
- 配置中心从 IMGUI 灰色长列表迁到共用 UI Toolkit 视觉语法：用途 hero、Module / 资产 / 单例三项摘要、分组卡片和窄窗纵排形成明确层级；窗口壳先显示，Profile 发现经 root scheduler 延迟执行，Domain Reload 后也不会停在永久 loading。
- `FrameworkProjectPath` 深化为物理目录树安全 Seam：递归读取、复制、指纹和删除统一拒绝 symbolic link / junction / reparse point，且删除在任何 mutation 前验证整棵树；Windows junction 集成测试证明工程内链接不能触及边界外标记文件。
- HybridCLR Generate stamp 升级为 v4：热更侧读取 HybridCLR 针对目标平台编译的 DLL 元数据拓扑（定义、布局、签名、泛型、Attribute、P/Invoke / calli 与元数据操作数），AOT 侧哈希非热更 Player 源文件、asmdef、defines 与非 Unity 内置预编译 DLL；另记录 source `link.xml`、启用场景、Resources / Preloaded 资产和序列化依赖组成的 Player linker 根，避免误用 `Library/ScriptAssemblies` 的 Editor 变体或漏掉“代码未变、裁剪根已变”。普通热更算术、分支和常量变化不失效；AOT / linker 输入或会改变 MethodBridge 的结构变化要求重新 Generate。重新生成的 AOT / linker / CodePackage 清单已移除 Sirenix。
- Generate stamp v5 保持 v4 的证据范围，把 linker 图深化为“根集合 + 可达依赖并集”的一次批量采集，并在单轮指纹会话内去重 source/compiler/DLL/序列化资产读取；Module Audit 把已冻结的 Asset 路径和 Player 图沿可删除反射 Seam 交给热更新 Module。旧 v4 stamp 明确要求一次 Generate 迁移，不在只读审计中写盘。相同 AOT + linker 基准由 25.215s 降至 2.878s（约 88.6%），且缓存不跨轮，新鲜度语义不放松。
- Generate 的迷你 Player Build 会原地清空启用了 `Clear Dynamic Data On Build` 的 TMP / TextCore 源字体。构建器现在按序列化标记通用发现资产，Generate 前保存字节，无论 Generate 成败都逐文件尝试恢复；若生成与恢复同时失败则聚合两边异常，避免自动化流程污染项目工作树或遮蔽根因，不硬编码 Demo 路径。
- 默认资源装配从 Core 的 Yoo 程序集限定字符串迁移到 Adapter Assembly 注册。Core 只拥有 `DefaultAssetProviderAttribute` 与严格的零/一/多注册校验；删除 Yoo 后可安装另一个 Adapter，无需修改 Core。取舍与门禁见 ADR-0041。

### P2 · Demo 动态字体资产仓库卫生

- 清空 `DemoLatin SDF` 与 `DemoNotoSansSC SDF` 中由编辑器会话生成的 glyph / character / atlas 缓存，保留 Dynamic 模式、源字体引用、atlas 配置与 `Clear Dynamic Data On Build`；序列化资产合计减少约 4.2 MiB，运行时仍按需生成字形。
- 这是源码仓库体积与 diff 稳定性优化，不宣称玩家包同步减少：两份资产原本就启用了构建时清理，最终包体仍由目标平台 BuildReport 判断。
- Demo EditMode / PlayMode TestRun 守卫保存两份动态字体的原始字节，整轮测试回到稳定 EditMode 后恢复并继续观察迟到写回；只有磁盘原字节与同路径下 FontAsset、材质、atlas 等全部 Unity Object 的 dirty flag 都连续稳定才消费快照。任一子资产标脏时会先清标记再强制同步重导入，避免后续 `Assets/Refresh` 因脏 atlas 保存整份 `.asset`、连带写回主对象 glyph table。捕获前移到 `ExitingEditMode`，人工 Play 同样受保护；Domain Reload / Editor 重启也能从 `Library` 快照续恢复。这比调用 `ClearFontAssetData` 更安全，因为后者会连资产原有的 feature / atlas 基线一起清除。已落盘但 Git 未提交的字体字节会原样保留；捕获前仍在内存 dirty 的主/子资产则明确拒绝启动，避免静默丢弃用户编辑，并有专门回归锁定。真实 DemoScene 逐章 Build 与完整 909/909 后主动 `Assets/Refresh` 均须保持两份字体无源码 diff。

### P1 · Unity CLI 工程外自动化 Adapter

- 资源构建 workflow 中的 Editor `6000.3.14f1` 硬编码已经与项目 `6000.3.22f1` 漂移；测试脚本又独立维护 Hub / 注册表发现逻辑。新增 `Tools/UnityAutomation.psm1` 集中读取 ProjectVersion、核对 revision、选择 CLI / Direct Adapter、同步启动并返回退出码。
- `Tools/run-tests.ps1` 与 `build-assets.yml` 已复用同一 Module。Auto 模式在无显式 Editor 路径时优先使用 Unity CLI，旧 Hub 环境安全回退；通用 run、专用 test、CLI 保留参数与 Direct command-line quoting 的差异集中在 Module。不自动安装 / 升级、不删除工程锁，也不以“退出 0”替代 NUnit XML / 构建产物门禁；隔离最小工程已验证 beta.5 能精确选择 6000.3.22f1、同步等待并返回 0，`unity test` 的 1 条 EditMode 测试产出 1/1 passed XML。
- 工程锁拒绝已进入共享 headless Interface，调用方不能绕过且 Module 从不自动删锁；启动 / IO / XML 异常统一归入基础设施退出码 2。`Tools/Tests/UnityAutomation.Tests.ps1` 无需启动 Unity 即回归 Adapter 选择、版本拒绝、参数过滤 / quoting 与专用测试映射。
- 实验性的 `com.unity.pipeline` 暂不进入 manifest，当前 Editor 继续使用第三方 MCP、稳定菜单与 PlayMode 预检。未来只有形成可物理删除的 Editor-only 第二 Adapter 并通过删除测试时再接入。取舍、能力矩阵与命令示例见 ADR-0044 和 `unity-cli-automation.md`。

### P1 · 资源构建与 HybridCLR 热更新构建拆分

- 原 `Game.Framework.Build.Editor` 同时引用 YooAsset、Boot、HybridCLR.Editor 与 dnlib，且资源 Builder/Inspector 反向读取热更 Profile；只想保留普通资源构建的项目无法删除热更新工具链。现在保留既有程序集名给 YooAsset 资源构建，新建 `Game.Framework.Build.HybridCLR.Editor` 作为单向下游，热更新测试也随 owner 独立。
- 资源 Profile 成为普通 AssetBundle 选择的唯一真源；CodePackage 显式关闭“参与构建”。任何误启用或由 CLI 点名的 RawFile 包都会在写产物前失败并给出中文修复步骤，不再靠另一个可删除 Module 的 Profile 或默认名称静默跳过。
- 热更新侧继续复用资源侧的版本格式、部署、构建前预检与安全产物路径，没有为目录对称再抽浅 Common 程序集。通用 Module Audit 的证据字段和文案同步改为“HybridCLR 热更新构建 Module”，避免与仍可存在的资源构建混淆；取舍见 ADR-0045。

### P1 · Unity 6.3 / Odin 4 Editor 兼容基线

- Hierarchy 装饰器迁移到 Unity 6.3 的 `EntityIdToObject`、`GetEntityId` 与 `Selection.entityIds`，不再只替换 API 名而继续在内部传递旧 Instance ID；真实选中与取消选择契约由 EditMode 测试锁定。
- Odin 适配器改用公开的 `InspectorTypeDrawingConfig.GetDefaultEditorType(type)` 判断具体 Editor 所有权，不复制 Odin 的程序集分类规则；删除旧 `AssemblyTypeFlags` / `GetAssemblyTypeFlag` 调用后，Unity 编译恢复 0 错误、0 警告。

### P1 · 中文优先的 Inspector 与诊断反馈

- 高频配置 Profile 和资源运行模式增加 `InspectorName`：界面显示中文，序列化字段名、枚举成员与已有资产保持不变；关键代码值放在括号中，仍可按英文标识检索源码和第三方文档。
- `MonoContext` 状态由同一格式化入口显示“未初始化（Uninitialized）”等中文优先标签，诊断、Inspector 和复制报告不再各写一套；资源引用 Drawer 与模块/体积工具同步收敛用户可见术语。
- Demo 的分数、重置、缓存策略等现场文案和 guide 日志示例改为中文优先；缓存策略示例同时移除以显示字符串判断行为的脆弱逻辑。测试锁定高频标签与全部诊断状态，最新完整基线为 EditMode 364 + PlayMode 545。
- 服务安装器与 `MonoGameContextBase` 补齐中文 Inspector 主标签；场景快捷入口、Protobuf 与字体字集工作台不再裸露 `Entries / Profile / Charset`，需要映射到 Unity、TMP 或框架 API 的 `Boot Scene / Play / Character Set` 等术语以中文释义加英文原名呈现。
- 资源 Core、配置服务与 YooAsset Adapter 的启动、空输入、清单失配、加载/清缓存/下载失败、缓存世代和后台所有权消息改为中文说明；`Provider`、`TableFiles`、`location/tag`、类型/API 名及 YooAsset 原始错误仍原样保留。精确日志测试改为锁“中文动作 + 动态标识 + 原始异常对象”，不把整句标点当脆弱契约。
- Demo 总览把五层名称统一成“中文职责（英文类型）”，日志章先解释日志接收器（sink）再展示 `Info / Warning`，资源章把三类下载/清理范围改成横向表格，并把启动更新长段落拆成四步流程。真实 Game View 复查又收短了导航标题与表格参数显示，避免窄列截断；教学契约与 CodeRef 专项 31/31、DemoScene 冒烟 6/6、最新整库 909/909 通过。
- Context / DI / Pool 的异常、警告与 Trace 改为中文动作优先，同时保留 `Context`、类型名、`GetModel`、`IDisposable`、`Spawn/Despawn` 等可复制标识；覆盖容器构建与所有权回滚、Mono Context 初始化、注入权限、主线程守卫、Bag 归还和对象池误用。相关测试不再锁整句英文，而是断言“中文语义 + 动态类型/API + 原始异常”；Unity 编译 0 错误/0 警告，最新完整 EditMode 364/364、PlayMode 545/545 通过。
- 后台测试复查确认 `editor_unfocused` 不阻塞 Runner；同时发现当前 MCP schema 的 `filter` 别名未被已安装 Unity 端消费。后台自动化 Skill 与 MCP 指南现要求先按同一 mode 查询测试清单、使用 `groupNames/testNames`，并把终态 `succeeded + total=0` 判为筛选失败而非假绿；PlayMode 定向复验 26/26。
- Toast owner 测试不再依赖 0.05–0.35 秒墙钟窗口：UI Implementation 保持生产实时延迟，在内部构造提供手动 delay Seam，测试可让已取消旧 timer 故意迟到成功并直接验证 identity。Audio 生产淡变仍使用 `Time.unscaledDeltaTime`，内部测试 Seam 改用固定帧增量；一次性/非循环播放由显式 `AudioSource.Stop` 模拟物理终态，循环契约直接验证引擎 loop 与显式 owner。音频整组不再依赖真实声卡、前台焦点或 batchmode Ignore；最新完整 PlayMode 545/545 通过。
- Flow 的退出失败/取消回调、Audio 的淡变/回收/释放后误用，以及 UI Toolkit 的异步点击与重复 Context 绑定反馈已改为中文语义优先，同时保留 `FlowState`、`OnExit`、`IAudioUtility`、Button 名和 View 类型等检索锚点；原始 exception 继续单独交给日志 Seam。新增契约测试锁定中文动作、动态标识和“失败后仍清理”的所有权语义。
- “框架诊断”窗口以真实最小宽度、中宽和宽屏三档复查：最小宽度下原命令接入长说明会被底部裁切，现按信息密度显示短说明并把完整接入代码保留在 tooltip；中宽/宽屏继续展示完整说明。按钮、搜索、日志闸门、Context 分栏和命令区在三档均无溢出，响应式结构测试同步锁定实际 HelpBox 文案。
- 日志接缝补齐 Warning 级原始异常：`LogEntry.Exception` 不再只在 Error 路径有意义，Unity 默认 sink 会在同一条 Warning 中展示异常而不额外抬成 Error；HTTP、WebSocket 与 Storage 的可恢复失败改为“稳定中文动作 + 结构化异常”，Demo 服务器清理反馈完成中文化。定向 PlayMode 111/111、Demo Server EditMode 2/2，最新完整 EditMode 364/364、PlayMode 545/545 通过。
- WebSocket Interface 与 Implementation 的错误边界进一步收口：`Connect` 在 Adapter 前严格拒绝相对地址和非 `ws/wss` scheme；Send / Disconnect 以 caller / Context owner 意图识别取消，即使 Adapter 在竞态中抛 ODE 或 socket error 也返回 OCE 并保留 inner；关闭事件只发布框架稳定摘要，原始异常走日志 Seam；Provider Dispose 失败也不会漏掉响应式 State 释放。Guide、ADR-0028 与 Demo 同步解释“重连只判断 ByUser”，专项 PlayMode 52/52、最新完整 909/909 通过。

### P1 · Editor 工作台动作可用态

- `FrameworkEditorOperationGate` 增加无 Unity 静态依赖的状态 evaluator，统一编译、资产导入、Player Build 与 Play / 即将切换 Play 的阻止顺序；`requireEditMode:false` 明确表示“有副作用但 Play-safe”，不再被误当成只读动作。
- 资源构建、HybridCLR、Luban、Protobuf、服务安装器与字体字集工作台都在点击前使用同一 Gate 并显示完整原因，Generator / Builder 动作层继续二次门禁竞态；刷新、定位、打开目录和 HotUpdate 只读校验保持可用。
- Core 测试只拥有 evaluator 与 Service Installer；五个可删除 Editor Module 在各自测试程序集内锁定接入，不把可选窗口类型或源码路径反向写进 Core。真实 Utility 窗口已用 Unity MCP `PrintWindow` 在 280–320px 复查，资源、Proto、Installer 与 HotUpdate 的纵排、长文换行和 Play 状态双按钮原因均无横向裁切。审查补出的“业务原因被 Gate 覆盖 / 无 Profile 次按钮空提示”由 HybridCLR 模块内纯 evaluator 锁定；本轮业务前置检查最终定向 29/29、完整 EditMode 412/412、PlayMode 545/545，共 957/957 通过。
- 第一批 owner Module 业务前置检查已落地：资源工作台把“构建 / 部署 / 伺服已有 Deploy”按真实依赖分别判定，零启用包不会误伤本地服务器；动作层在保存脏场景、确认全量重建或触碰 SBP 前再次拒绝无效请求。服务安装器把输出路径安全与全局所有权作为整批写入门禁，把命名空间、扫描目录与扫描失败保留为逐条结果；总览和 Inspector 会显示可生成条目数，坏条目不再隐藏好条目，也不会到点击后才暴露跨 Profile 冲突。
- 第二批 owner Module 已把 Luban、Protobuf 与字体字集的廉价前置条件前移：Luban 一次报告 CLI / conf 缺项，Protobuf 一次报告当前平台 protoc / 源目录并递归统计输入，两个批量入口只提交已就绪 Profile；输出所有权只比较已成立的安全声明，空白新 Profile 不再冻结其它配置，但未就绪 Profile 的有效声明仍能阻止冲突。字体把工程边界错误与“目录暂不存在、可能空字集”的可恢复警告分开，并拒绝能逃逸扫描根的文件模式。窗口与 Generator 共用 Module 内只读报告，GUI 不预跑外部进程、解析器或完整生成，也没有为表面相似抽中央泛型工作台。共享 `FrameworkProjectPath` 进一步拒绝被普通文件占用的目标/父级，UGUI 删除重复路径实现；四个真实生成器复用 `FrameworkCSharpSyntax`，集中命名空间验证与关键字安全标识符清洗。最终定向 107/107、完整 EditMode 471/471、PlayMode 545/545，共 1016/1016 通过。
- 新增 Editor-only `FrameworkGeneratedOutputClaimCatalog` Seam：独占目录、递归文件后缀、精确文件三种 claim 足以表达当前清理边界，Luban、Protobuf、服务安装器、UI Binding、资源包名常量与字体字集由 owner Module 自注册 collector，Core 不引用可选生成器类型。预览只消费随 `projectChanged` 失效的既有快照，冷启动证据缺口明确显示且不执行 collector；任何真实写入前强制重采集且 collector 失败 fail-fast。跨 Module 冲突、同来源内部冲突、claim id 唯一性、大小写/规范路径、后缀子集、Asset/绝对路径身份、冷预览与写盘刷新由 Core 矩阵测试锁定，各可选程序集另锁定自己的注册 Adapter。初版完整 EditMode 491/491、PlayMode 545/545，共 1036/1036 通过。
- Luban 生成改为 Module-local `LubanGenerationTransaction`：CLI 只写 `Temp` staging，代码 / 数据格式由管线固定为 `cs-bin + bin`，不再暴露可编辑但唯一合法的 Profile 字段；强制 `validationFailAsError`，受控参数拒绝 ExtraArgs 覆盖、短选项 bundle 绕过与 watch 常驻。非空 UTF-8 C# 规范成无 BOM LF，并与根目录 `.bytes`、由其生成的 manifest 一起校验；commit 前再次重采输出 claim，再对代码 / 数据两棵独占树做联合差量，保留未变文件和 retained `.meta`，清理陈旧产物、孤儿 `.meta` 与空目录，支持大小写目录变化和目录 / 文件拓扑替换。首次正式修改前备份两树，数据或代码发布故障会同时回滚并保留原始根因；回滚失败保留 recovery 路径，断电 / 强杀不冒充文件系统级原子保证。受控 `ILubanCliRunner` 锁定 CLI 半产出失败不触碰正式目录，真实 Demo CLI 冒烟确认 10 个 `.cs` + 4 个顶层 `.bytes` 满足校验。未因 Proto 的单目录后缀同步而抽丢两者不同所有权语义；生产与测试 namespace 也从误挂的 `Game.Framework.Build(.Tests)` 收回各自 asmdef 已声明的 `Game.Framework.Config.Editor(.Tests)`，不改脚本 GUID 或资产身份。Config Editor 专项 71/71，最终完整 EditMode 542/542、PlayMode 545/545，共 1087/1087 通过。
- Protobuf 工作台不再把 `CapturePhysicalTree("*.proto")` 绑到 IMGUI `OnGUI`：卡片、批量按钮与摘要共用 Module-local 输入快照，只有“重新扫描”显式采集。Profile Catalog revision 或 protoc / 源 / 输出路径变化只廉价失效，不在 Layout / Repaint 期间暗中读盘；`Generate` 仍直接重验当前输入与写盘 claim，预览缓存不进入真实生成证据。
- UI Binding 输出 claim collector 不再在任意其它生成器写盘前 `LoadAssetAtPath` 全工程所有 Prefab：Module-local 候选索引首次完整建立，随后经 AssetPostprocessor 增量维护，并把候选与 Prefab Variant 依赖图一起放进 `SessionState` 跨脚本域重载复用；基 Prefab 单独变化也会递归重验后代。真正采集只加载根上含 `UIBindingData` 的候选，仍按当前 Profile、目录覆盖与 Prefab 内容计算精确文件声明；其它工作台的冷预览只显示证据待采，不会沿 collector 触发首次全扫。UI 配置窗口同时迁到共享 Profile Catalog 与 UI Toolkit 视觉原语，用 hero、摘要指标、覆盖链说明和分层配置卡替代灰色逐行表单；首次绘制只显示轻量说明，“重新扫描”才完整刷新配置与候选索引，CreateGUI / 重绘不再逐次 `FindAssets` 目录配置。两类刷新任一步失败都会共同丢弃快照，避免拼接新旧证据。索引完全留在可删除 UI.UGui Editor Module，没有为单一消费者扩张 Core Interface。最终完整 EditMode 565/565、PlayMode 545/545，共 1110/1110 通过。
- Demo 输入返回键的 PlayMode fixture 区分了“PlayerLoop 后台推进”与“Input System 设备失焦投递”：SetUp 暂时使用 `InputSettings.BackgroundBehavior.IgnoreFocus`，TearDown 恢复消费项目原值，使程序化键盘事件在 `editor_unfocused` 下仍确定可测，同时不把测试策略写进产品行为。

### P1 · Editor 诊断渐进披露与打开性能

- Framework Mono Inspector 删除每个组件重复的“打开完整框架诊断”，低频运行时诊断改为按实例、默认折叠；折叠时仍暴露失败 Context、当前 Play 未初始化和未解析 Context 的摘要。视觉复核顺带修正 Odin 禁用 / 排除时没有把具体类型归还原生 fallback 的所有权漏洞。
- 模块裁剪审计与真实构建体积窗口不再在 `CreateGUI` 同步扫描工程、解析全部程序集并哈希全部档位。打开耗时从本机实测约 18.9 秒降到约 0.8 秒；昂贵采集改由明确按钮触发，工程、Package、构建场景、目标平台或编译图变化会使会话预览失效。
- 模块裁剪审计命中会话缓存时也不再急切创建所有折叠卡片：全量 Module、第三方目录、全局 linker 规则、进阶 Profile、任意 Module 闭包与原始报告均在首次展开时构建，卡片内长清单再做第二层懒建；同一 Foldout 只构建一次，风险区保持默认展开，窄窗新增行立即重放当前响应式布局。
- 性能优化没有削弱证据：真正构建会重新采集当前 Audit，并只为所选 Profile 计算闭包、manifest、Runtime 与 Package 指纹；缓存只服务只读预览。工具中心、两个证据窗口与 AI 自动化说明采用窄视觉 helper 统一 hero、语义色、卡片和响应式指标，不抽中央业务工作台。
- `SSFramework/诊断/AI 自动化` 保留三个点击即执行的稳定机器 Interface，并新增只读人工说明入口，逐项解释副作用、完成判据与人工工作台；PlayMode 预检的忙碌门禁改为复用共享 Gate，补上 Player Build 状态。
- 该批在 Unity 6000.3.22f1 的回归基线为 EditMode 546/546、PlayMode 545/545，共 1091/1091；后续批次的当前总基线见上文最新完成项。

### P1 · UI 必需窗口与 Flow 错误边界

- `Open<T>` 保留 Adapter 创建失败返回 null 的宽松 Interface；新增非破坏性的 `OpenRequired<T>` 扩展，把同一失败提升为带窗口类型与资源位置的异常，取消仍保持 OCE。可选提示窗继续判空降级，Flow 主页面与承诺出现可见结果的动作使用严格入口，不扩张自定义 Adapter 的实现面。
- Loading Interface 进入非破坏性退役阶段：并发安全的 `AcquireLoading → LoadingHandle` 成为唯一推荐入口，`ShowLoading/HideLoading` 在 Interface、核心与两个 Adapter 上统一发出可重新编译的 `[Obsolete]` 警告；生产源码门禁禁止新调用，compatibility tests 继续锁定旧源码与 lease 混用、创建中取消和陈旧 owner，等待未来破坏性版本一次删除 legacy 状态与转发。
- Outpost 标题/结算状态改用严格开窗，真实 `GameFlow` 测试锁定窗口创建失败后 `Current` 仍为 null；`FlowNav` Adapter 覆盖成功、顶替取消、faulted task 与同步误用异常，真实失败只保留原异常并记录一次中文日志。Outpost 玩家路径与 DemoScene 冒烟继续通过。
- 扩展包命令式初始化改为 `Initialize → EnsureInitialized`，不再读状态后手造泛化异常；排行榜与扩展包的可恢复 Warning 也把原异常交给日志 Seam。Demo/guide/ADR 同步解释宽松/严格开窗及响应式状态与命令式资源门禁的选择。新增 10 项 PlayMode 契约后完整基线为 EditMode 364/364、PlayMode 545/545，共 909/909。

### P1 · Asset 启动更新与旧缓存收尾边界

- Demo 启动更新不再把 `ClearCache(Unused)` 失败混成整包不可用：确认新清单与 bundle 已就绪后，非取消的旧缓存回收失败记录带原异常的 Warning，让玩家继续进入游戏；`ClearCache(All)` 修复动作仍保持异常与重试语义。
- 内部 `ReclaimUnusedCache` Seam 明确区分页面 caller、`ClearCache` waiter 与 `AssetUtility` 物理 owner：页面取消不会提前释放共享 gate，也不向旧 UI 发布迟到结果；同时发生的物理失败仍保留原异常，物理生命周期取消则原样传播 OCE。预取消、正常成功、可恢复失败、caller 取消后的物理成功/失败与物理取消共 6 项契约全部覆盖。
- Demo 与 guide 同步说明为什么「新内容可用」与「旧缓存已删除」是两个不同成功条件，并将 `None` 解释为不让 waiter 提前脱离，不再误称业务页面拥有物理操作。独立只读评审的 Medium/Low 已全部闭环；Demo 教学与 CodeRef 31/31、DemoScene 6/6，最终 EditMode 370/370、PlayMode 545/545，共 915/915。

### P1 · Runtime 日志调用面闭环

- Core 与可选 Runtime Adapter 中剩余的裸 `Debug.*` 只在 `FrameworkSelfCheck` 和 `LoggingCommandSystem`：前者改经 `Log` 写入并携带自身 Unity context，后者的 opt-in echo 改经同一 Seam 写 Info，保留命令类型、Context 名、同步/异步、耗时与失败文字。
- AOT `Game.Framework.Boot` 仍保留原生日志，因为它必须在 Runtime Framework 加载前独立自举；Logging Implementation 内的 `Debug.*` 是默认 Console Adapter，不是未迁移调用点。Editor 工具与 Demo 的裸日志捕获实验同样是刻意边界。
- `DiagnosticsTests` 穿过真实装饰器与捕获 sink 锁定 echo 的 category、级别与消息，不用源码 token 冒充行为验证。

### P2 · Demo 服务器物理任务所有权

- `DemoGameServer` 用内部 task registry 统一拥有并观察 HTTP / WebSocket accept、HTTP handler 与 connection task；未知 fault 进入 Logging Seam，Stop/Close 导致的 listener 终止仍作为正常收尾。
- `/api/slow` 从阻塞线程的 `Thread.Sleep` 改为 server token 驱动的 `Task.Delay`；每个 WS connection 将 tick task 纳入自身物理终态，`Dispose` 后不会留下未观察的推送循环或 20 秒睡眠 handler。
- 公共 `IDemoGameServer.Stop` 仍保持同步逻辑停止，不为单一 Demo 调用方扩张异步 Interface；内部 drain Seam 只供测试证明 Domain Reload 卫生。定向用例真实发起 20 秒慢请求，验证 Dispose 后 2 秒内全部 task 归零。

### P1 · 手写分层注册与 WebSocket 装配职责收敛

- Demo、Outpost 与真机自检中的普通纯 C# Model / System / Utility 统一迁移到 `RegisterModel/System/Utility` 或对应 `RegisterOwnedXxx`；调用点不再重复维护具体类型和 `typeof(I...Utility)`，具体 Implementation 与层 Interface 仍是可解析的真实契约。
- `RegisterValue/RegisterOwned` 继续服务 `ICommandSystem`、非分层服务、选择性暴露与生成安装器；Factory 继续显式列 contract。没有为机械统一隐藏生成清单或新增一套 Layer-aware Factory API。
- 公共 XML doc、Demo 教学与 guide 全部改教层感知入口；Container 章明确区分普通分层注册与低层精确接线。既有契约测试锁定“具体类型 + Interface 同一实例、层标记不可解析、owned 恰好释放一次”。
- Outpost 的 `NewRecordPushEvent` type 映射从运行期 System 移回 Composition Root；System 只消费事件并拥有断线重连策略。当前没有第二类安装调用者，故不拆新的注册权限 Interface，避免用浅 Seam 交换更多概念。

### P1 · Context 装配与 Mono 发布事务

- Factory 回调返回后重新确认 Container 仍存活，并让本次解析立即服从回调期间写入的 runtime override；owned 产物在 Context 被重入释放时以弱引用历史识别已释放别名，既避免二次 `Dispose`，也不延长外部对象寿命。
- 分层动态注册拆为“预检计划 → 用户装配 → 原子提交 → Trace”，恰好一个 Model / System / Utility 标记成为所有 Builder、runtime 与 Mono 入口的一致契约；Mono 层和 View 在注入阶段可访问 provisional Context，但只有注入和资源绑定全部成功后才发布注册，任一步失败都复用同一逆序清理路径。
- `GameContext` 改为整批先 Inject、再 Attach；后续绑定失败会撤销本批已经写入的 Context 归属并释放 owned 服务。注入计划同时修正为基类成员先于派生成员，匹配公共文档与可预测的继承初始化顺序；任意属性 setter / 方法内部产生的外部副作用仍明确属于用户代码边界，框架不伪装成可回滚数据库事务。
- `AssetReferenceBinder` 在当前 bindable 的 Bind / Bag.Add 失败时立即清理当前项，Mono owner 再回滚此前成功项。AGENTS、guide 与 ADR-0003/0019/0035 已同步所有权、失败与重入语义；定向 PlayMode 74/74、完整 EditMode 603/603、PlayMode 700/700、Unity 编译 0 错误。

### P1 · Editor 证据任务可靠性与热路径减负

- 真实构建体积探针的主报告与 child 结果改为同目录临时文件原子发布，JSON 最后作为恢复提交标记；previous-latest 本机 journal、首代 JSON、latest 指针按可恢复顺序提交。初始、PID、child 终态或最终报告持久化失败都不会留下永久 `running`。恢复用 PID + UTC 启动时间验证 Unity child，拒绝 PID 复用、自附着和检查异常；已落盘结果仍保留真实结论，未知 owner 则以独立原因停止后续档位。
- 状态观察者异常不再穿透构建状态机；恢复只重建报告实际记录的 Profile，不再为少数已选档位重算完整 Module 假设矩阵。失败日志由 `ReadAllLines` 改成有界流式采集，优先保留最早的可行动诊断，否则只保留尾部。
- Module DLL 引用缓存改以流式内容 SHA-256 命中：相同字节避免反复装入反射只读程序集，同长度、同 mtime 的原地替换仍会刷新引用。显式刷新失败会清空当前证据，成功/失效状态同步到所有已打开窗口且逐观察者隔离；体积窗口第二次点击“刷新”会真正重采，进阶 Module 卡片首次展开才创建，折叠或重采不会丢失选择意图。
- 框架诊断的对象池区默认折叠，折叠时只读 O(1) 数量，展开后同一轮只格式化一次详情；鼠标和键盘开合使用同一状态路径。人工“刷新”会作废树、明细、Mono、日志和命令签名，不再因同类型实例替换而复用旧定位对象。专项 EditMode 132/132、Pool PlayMode 63/63；最终完整 EditMode 614/614、PlayMode 700/700，Unity 编译 0 错误/0 警告。

### P1 · Flow 最新意图与异步主线程提交

- `GameFlow.GoTo` 改为先发布新 pending owner、再取消旧排队 task，避免同步 UniTask continuation 重入的新请求被外层调用覆盖并永久 Pending；每个 task 终态先摘 active owner，没有后续请求时先释放 runner，旧循环的 finally 不会再停掉重入启动的新循环。
- `InstallBindings`、Context 构造/注入/附着与 `FlowChangedEvent` 都作为可重入用户边界处理：scope 建成后重验宿主与最新意图，陈旧 scope 事务回滚且不进入；成功 `OnEnter` 的 token owner 在事件与 task 发布前撤掉，后续正常切换不会回头取消已经提交的状态。
- `OnEnter/OnExit` 可在 worker 物理结束，但异常分类、scope Dispose、`Current`、Event 和 `GoTo` 终态只在 Unity 主线程提交。相同边界扩展到 `ICommandSystem`：默认 dispatcher 的成功/失败/取消均回主线程，`LoggingCommandSystem` 对自定义 inner 再兜底后落无锁流水；原始异常对象与堆栈保持不变。
- 新增 pending 取消 continuation 重入、安装期 GoTo/Dispose、事件回调重入、worker 成功/失败/回滚，以及默认/日志命令分发线程边界契约。Flow 专项 27/27、Command + Diagnostics 专项 36/36；最终完整 EditMode 614/614、PlayMode 709/709，Unity 编译 0 错误。

## 下一批候选（按杠杆排序）

| 优先级 | 候选 | 证据 / 完成标准 |
|---|---|---|
| P1 | 公共 API 注释审计 | 优先生命周期、取消、异常、所有权与 Adapter 接缝；删除复述代码或记录历史的注释。以“调用者能否仅靠悬浮提示正确释放/取消”为完成标准。 |
| P1 | CI Runner 与发布真正接线 | 资源构建 workflow 已复用 ProjectVersion + Unity CLI / Direct Adapter；下一步是在真实自建 Runner 验证授权、Android/iOS/WebGL Module、缓存与 CDN 上传凭据，并把一次成功 artifact 固化为基线。 |
| P1 | 中文友好性继续收口 | 高频 Inspector、诊断状态、资源/配置/YooAsset、Context/DI/Pool/Bag，以及 Flow / Audio / UI Toolkit 的已发现低频反馈已完成；下一步继续审查 Lifecycle、Profile 与其他边缘路径。始终保留类型、参数和第三方原始错误供检索，不引入过度的运行时多语言系统。 |
| P2 | 大文件按职责复查 | `DiagnosticsWindow`、`YooAssetProvider`、`AssetUtility` 等只在发现两个独立变化轴或测试 Seam 时拆；单纯行数不是理由。 |
| P2 | WebGL / 小游戏固定 Runner 基线 | 隔离探针已能在当前目标平台生成真实 Player BuildReport 上界；下一步等确定发布平台与 CI Runner 后保存同环境 artifact / 阈值，避免把本机 Windows 数字当 WebGL 基线。 |
| P1 | UPM 分发依赖标准化与干净消费矩阵 | 当前体积探针把源码复制到临时工程的 `Assets`，尚未证明真实 UPM 安装/移除。下一步先确定 Core / Yoo / UI 等发布 Package 拓扑和 Git、embedded NuGet 依赖来源，再以工程外临时 Unity 项目验证 core → add Yoo → remove Yoo（保留 Library）的编译与 Player Build，不在框架内复制第二套 Package Manager。 |
| P2 | 物理路径跨平台门禁矩阵 | `FrameworkProjectPath.TryResolve` 已让所有生成器在写盘前拒绝现存路径链上的 symlink / junction / reparse point，递归树操作还会验证全部后代；Windows junction 集成门禁已有覆盖。下一步只补 macOS/Linux 目录与文件 symlink 的 CI 矩阵，验证同一 `FileAttributes` 契约在各平台成立；发现平台差异再改共享 Module，不重复实现各生成器局部门禁。 |
| P2 | 字体输入语义与扫描成本量化 | 重叠目录/模式可能重复读取同一文件，JSON/C# 中的 `\uXXXX` 也不等同于已提取真实字形。先用真实本地化数据统计缺字与扫描成本，再决定是否引入文件去重、格式解码 Adapter 或明确维持“源码字面字符”语义。 |
| P2 | Asset 维护 operation / 更新 session | Demo 已有两处为区分 caller waiter 与物理维护 owner 而手拼 gate、`CancellationToken.None` 和章节 token；Demo 与 Outpost 也出现 Initialize → Ready → 快照下载轮廓。启动流程已直接锁定「waiter 在物理清理终态前仍为 Pending」、「页面取消不发迟到 UI，但物理失败仍保留原异常」及「非取消的旧缓存回收失败只降级为 Warning」；出现第三个生产调用方后再决定是否形成有状态 session，避免把业务重试/确认策略塞回 Core。 |
| P2 | UI 窗口 lease 语义 | `Open → Bag.Add(Close<T>)` 已在多个 Flow 状态重复，但当前同类型全局单实例，简单 `OpenOwned` 会让多个 owner 互相误关。先定义独占或引用计数所有权并证明并发需求，再决定是否形成窗口 lease Module。 |

## 每批完成门禁

- `git diff --check`；新增 C# 经 Unity 编译零错误；PowerShell 经 AST parser。
- 相关 fixture 定向测试先绿，再跑 EditMode + PlayMode 全量。
- 改 Demo 时打开 `SSFramework/Demo 教学/维护与校验`，运行 CodeRef 校验；目录元数据通过自动发现。
- 改公开设计时同步 ADR / guide / Demo / 最近层级 AGENTS；只改实现细节则不要制造文档噪音。
- 不直接修改第三方库，不手改 `.unity` / `.prefab` YAML。
