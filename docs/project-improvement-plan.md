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

## 已验证基线（2026-08-28）

| 维度 | 当前事实 |
|---|---|
| Unity | 6000.3.22f1 |
| Framework Module | 29 个 asmdef Module（含测试与可选 Odin Editor Adapter）；依赖与删除测试见 `framework-module-map.md` |
| Demo | 32 个自动发现章节；Catalog 集中拥有 Adapter 生命周期，并按 Capability / Concept / Workflow 校验真实 Build 教学语义 |
| 教程 | `framework-guide.md` 28 章 |
| ADR | 0001–0044；0040 为 UPM-aware 源码目录，0041/0042 补齐依赖证据，0043 收口 Editor 菜单与工作台，0044 固化 Unity CLI 工程外 Adapter 边界 |
| 测试 | PlayMode 525 + EditMode 330，全绿；交互式 MCP 后台运行且 PlayMode 先预检，命令行入口默认 EditMode + PlayMode |
| Demo CodeRef | 313 处可打开源码跳转全部精准命中；注释、文案与外部文档路径不计入源码构造点 |
| AI 常驻规则预算 | 最深 AGENTS 链 29.88 KiB，低于 Codex 默认 32 KiB 项目指令上限；新增常驻规则前需继续评估外移空间 |

## 已完成的高优先级闭环

### P0 · Container 生命周期与绑定模型

- 新增 `RegisterOwnedFactory`，补齐“延迟解析依赖 + Context 拥有 IDisposable”组合；Outpost 本地化从泄漏路径迁移。
- 内部 `ContainerBinding` 显式区分值与 Factory，修复 `Func<Container, object>` 普通值被误执行，并让多 contract 共享同一诊断状态。
- 注册值 / Factory 结果做 contract 校验；循环 Factory fail-fast；Eager 构建失败释放已创建 owned 产物。
- Context Dispose 后禁止解析、注入、订阅与动态注册；取消回调异常不再阻断事件和 owned 服务级联释放。
- 补齐 Build 前失败事务：Builder 暂管 owned 并可 using 回滚，GameContext 构造失败释放 Container，Mono Context 只发布完整 Ready 状态；FlowState 安装失败不再泄漏或让 GoTo 永久 Pending。
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

- 为 32 章建立“定位 → 可操作行为或可验证样板 → 设计取舍 → 适用边界/下一步”的渐进教学契约；概念章不为凑按钮伪造交互，顶部 Summary 由运行期强制 ≤160 字、≤2 句，避免导航说明挤成正文。
- Demo 外壳新增“本组 / 全部”进度与章节底部上一步/下一步导航；入门/核心提示顺读，能力/进阶明确可按需跳转，实际按钮切章与滚动复位已验证。
- `DemoModuleHost` 在真实 Build 中记录教学语义，Catalog 按能力/概念/工作流分别检查定位、解释结构、交互或步骤；源码注释、死代码和早退不再能靠 token 数量假绿。
- 场景依赖缺失统一用结构化降级页说明“为什么不可用 → 如何恢复 → 接下来怎么学”，并强制提供接线源码；UGUI、UI 框架、多 Context 与字体的顶层早退已迁移。
- 新建独立 Demo PlayMode Module，在真实 DemoScene 中穿过 Context、Catalog 与 Shell 逐章 Build 32 个 Adapter，并用真实 UGUI/UI 框架章节覆盖降级路径；当前 CodeRef 防腐覆盖 313 处精准源码构造。
- 重写入门地图，并为 Counter / Model / Command / System / Event 补上选择标准、代价、生命周期与反例；8 个过长章节摘要完成收束，实际 Game View 已检查首屏、对照表和 System 深度说明。
- Demo 实战发现 `IShopSystem` 泄漏 `ICommandContext`：改成窄业务接口 `TryBuyPotion()`，WalletModel 由 Implementation 注入，并用购买不变量测试锁定。
- 修正框架层术语漂移：简单原子 Command 可直接写 Model，System 承载可复用/多步规则；Utility 可持有基础设施状态但不持有业务状态。源码 XML doc、README、guide、roadmap 与 ADR-0001 已同步。
- UI 融合章新增 128px 低清/正常预算即时切换，把视觉异常变成可复现教学实验；实战修复 RT 两轴分别钳制造成的拉伸，并以托管 `CanvasScaler` 分离逻辑布局与采样分辨率，确保降预算只变糊、不变形也不重排。纯尺寸契约、实际 Game View 与低清按钮 Raycast 均已验证。

### P1 · Demo 异步动作生命周期

- `DemoModuleHost` 新增 `AddAsyncActionRow(Func<CancellationToken, UniTask>)`：任务进行中禁用当前按钮、防双击重入，未接异常统一进入框架日志，切章、UIDocument 重建与 Shell 销毁会先取消 Host 再 Teardown Module。
- 9 个章节共 58 个异步按钮全部走专用入口，不把 `async` lambda 塞进 `Action` 退化为 `async void`；静态门禁按 C# 词法区分代码、字符串和注释，并检查 `AddActionRow` 调用体不能藏 `.Forget()` / `UniTaskVoid`。
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

### P1 · 配置就绪契约深化

- Demo 与 Outpost 实战暴露：命令式调用方都要手写 `WaitUntil(State is Ready or Failed)`，而 Failed 枚举无法交还资源缺失、清单错误或反序列化的原始异常。
- `IConfigUtility<TTables>` 新增 `EnsureReady(token)`：流程直接获得同一份 Tables 或原始失败；`State` 专注响应式观察，`Tables` 专注已就绪同步读取，三种 Interface 形态不再互相冒充。
- 调用方取消只脱离自己的 waiter，配置组件与 Context 继续拥有共享加载；owner 销毁才取消物理操作和剩余等待。完成信号只表达终态，根异常由 `ExceptionDispatchInfo` 保存，避免无人等待时出现未观察的 UniTask 异常。
- `MonoConfigUtilityBase` 在任何资源 I/O 前快照并校验清单，拒绝空项、重复项及空表根；Outpost 战斗启动迁移到新契约，Demo、guide、ADR-0009、领域词汇与业务规则同步解释选择标准和失败/取消边界。
- 独立 `Game.Framework.Config.Tests` 以真实 Mono Context + AssetUtility + 可控 Provider 覆盖 Start 前等待、稳定表根、原始失败与日志 context、调用方取消不截断 owner、owner 销毁取消物理加载，以及无效清单在 Adapter 工作前 fail-fast；测试随 Config 目录整体删除，不反向黏住通用 Test Module。

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
- guide §17/§24 与 ADR-0016/0027 同步选择标准、代价和验证方法；修正列表 XML doc 对不存在 `BindListView` Interface 的陈旧引用，继续守住“虚拟化留给原生 ListView”的边界。

### P1 · Module 裁剪证据与依赖可见性

- 新增 `SSFramework/诊断与分析/模块与依赖`：以当前目标平台 Player 编译图确定候选，再读当前已编译 DLL 快照的元数据引用，避免把 auto-reference 的“编译可见”直接误算成代码消费；Unity 6000 可能返回 Editor 变体，故目标平台结论另由显式 DLL 门禁、HybridCLR 目标产物和真实 Player Build 验证。
- 报告 Core-only / Core + UGUI / Core + Toolkit / 全部 Runtime / 当前 HybridCLR 热更档位的原始托管闭包，并机器执行 Core、两个 UI 后端与 Bridge 的删除测试。窗口改为“健康结论 → 关键数字 → 通俗建议 → 常用组合卡片”的渐进披露；完整模块、热更配置、程序集清单和原始报告默认折叠，620px 以下按钮与指标卡纵排且使用内容高度，避免裁剪和重叠。
- Core、Fonts、Proto、UI 与两个后端的真实外部依赖全部回写 asmdef 显式声明，审计不再发现隐式依赖；ADR-0010/0027、Module 地图、guide §24/§26、Framework AGENTS 与 Demo 接入章同步依赖语义。
- 原始 DLL 字节明确不等于最终包体；先用它发现值得实测的候选，再以 WebGL/小游戏 Player BuildReport 决定是否拆 `ReactiveListBinding` 或 Core 能力，避免为理论体积制造浅 Module。

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
- HybridCLR Generate stamp 升级为 v4：热更侧读取 HybridCLR 针对目标平台编译的 DLL 元数据拓扑（定义、布局、签名、泛型、Attribute、P/Invoke / calli 与元数据操作数），AOT 侧哈希非热更 Player 源文件、asmdef、defines 与非 Unity 内置预编译 DLL；另记录 source `link.xml`、启用场景、Resources / Preloaded 资产和序列化依赖组成的 Player linker 根，避免误用 `Library/ScriptAssemblies` 的 Editor 变体或漏掉“代码未变、裁剪根已变”。普通热更算术、分支和常量变化不失效；AOT / linker 输入或会改变 MethodBridge 的结构变化要求重新 Generate。重新生成的 AOT / linker / CodePackage 清单已移除 Sirenix。
- Generate 的迷你 Player Build 会原地清空启用了 `Clear Dynamic Data On Build` 的 TMP / TextCore 源字体。构建器现在按序列化标记通用发现资产，Generate 前保存字节，无论 Generate 成败都逐文件尝试恢复；若生成与恢复同时失败则聚合两边异常，避免自动化流程污染项目工作树或遮蔽根因，不硬编码 Demo 路径。
- 默认资源装配从 Core 的 Yoo 程序集限定字符串迁移到 Adapter Assembly 注册。Core 只拥有 `DefaultAssetProviderAttribute` 与严格的零/一/多注册校验；删除 Yoo 后可安装另一个 Adapter，无需修改 Core。取舍与门禁见 ADR-0041。

### P2 · Demo 动态字体资产仓库卫生

- 清空 `DemoLatin SDF` 与 `DemoNotoSansSC SDF` 中由编辑器会话生成的 glyph / character / atlas 缓存，保留 Dynamic 模式、源字体引用、atlas 配置与 `Clear Dynamic Data On Build`；序列化资产合计减少约 4.2 MiB，运行时仍按需生成字形。
- 这是源码仓库体积与 diff 稳定性优化，不宣称玩家包同步减少：两份资产原本就启用了构建时清理，最终包体仍由目标平台 BuildReport 判断。
- Demo EditMode / PlayMode TestRun 守卫保存两份动态字体的原始字节，整轮测试回到稳定 EditMode 后恢复并继续观察迟到写回；只有连续保持原字节才消费快照。Domain Reload / Editor 重启也能从 `Library` 快照续恢复。这比调用 `ClearFontAssetData` 更安全，因为后者会连资产原有的 feature / atlas 基线一起清除；测试前未提交的字体调整也会被原样保留。

### P1 · Unity CLI 工程外自动化 Adapter

- 资源构建 workflow 中的 Editor `6000.3.14f1` 硬编码已经与项目 `6000.3.22f1` 漂移；测试脚本又独立维护 Hub / 注册表发现逻辑。新增 `Tools/UnityAutomation.psm1` 集中读取 ProjectVersion、核对 revision、选择 CLI / Direct Adapter、同步启动并返回退出码。
- `Tools/run-tests.ps1` 与 `build-assets.yml` 已复用同一 Module。Auto 模式在无显式 Editor 路径时优先使用 Unity CLI，旧 Hub 环境安全回退；通用 run、专用 test、CLI 保留参数与 Direct command-line quoting 的差异集中在 Module。不自动安装 / 升级、不删除工程锁，也不以“退出 0”替代 NUnit XML / 构建产物门禁；隔离最小工程已验证 beta.5 能精确选择 6000.3.22f1、同步等待并返回 0，`unity test` 的 1 条 EditMode 测试产出 1/1 passed XML。
- 工程锁拒绝已进入共享 headless Interface，调用方不能绕过且 Module 从不自动删锁；启动 / IO / XML 异常统一归入基础设施退出码 2。`Tools/Tests/UnityAutomation.Tests.ps1` 无需启动 Unity 即回归 Adapter 选择、版本拒绝、参数过滤 / quoting 与专用测试映射。
- 实验性的 `com.unity.pipeline` 暂不进入 manifest，当前 Editor 继续使用第三方 MCP、稳定菜单与 PlayMode 预检。未来只有形成可物理删除的 Editor-only 第二 Adapter 并通过删除测试时再接入。取舍、能力矩阵与命令示例见 ADR-0044 和 `unity-cli-automation.md`。

## 下一批候选（按杠杆排序）

| 优先级 | 候选 | 证据 / 完成标准 |
|---|---|---|
| P1 | 日志调用面继续收敛 | ADR-0034 已 Accepted；Fonts、UI、Asset/Yoo、Audio 与 Config Runtime 已收敛；AOT Boot 已确认必须保留原生日志。继续按 Module 审查其余历史裸 `Debug.*`，保留 Logging Implementation、第三方内部日志和 Editor 工具；测试守住消息、context、异常和双击定位语义。 |
| P1 | 公共 API 注释审计 | 优先生命周期、取消、异常、所有权与 Adapter 接缝；删除复述代码或记录历史的注释。以“调用者能否仅靠悬浮提示正确释放/取消”为完成标准。 |
| P1 | CI Runner 与发布真正接线 | 资源构建 workflow 已复用 ProjectVersion + Unity CLI / Direct Adapter；下一步是在真实自建 Runner 验证授权、Android/iOS/WebGL Module、缓存与 CDN 上传凭据，并把一次成功 artifact 固化为基线。 |
| P2 | 大文件按职责复查 | `DiagnosticsWindow`、`YooAssetProvider`、`AssetUtility` 等只在发现两个独立变化轴或测试 Seam 时拆；单纯行数不是理由。 |
| P2 | WebGL / 小游戏固定 Runner 基线 | 隔离探针已能在当前目标平台生成真实 Player BuildReport 上界；下一步等确定发布平台与 CI Runner 后保存同环境 artifact / 阈值，避免把本机 Windows 数字当 WebGL 基线。 |
| P2 | UPM 分发依赖标准化 | Module 源码与 Odin 原生基线已经具备；下一步拆解 Git 直依赖与 embedded NuGet 单体包的发布来源，形成可重复安装 recipe，并在干净消费工程验证 Core / 可选 Adapter 的安装与删除，不在框架内复制第二套 Package Manager。 |

## 每批完成门禁

- `git diff --check`；新增 C# 经 Unity 编译零错误；PowerShell 经 AST parser。
- 相关 fixture 定向测试先绿，再跑 EditMode + PlayMode 全量。
- 改 Demo 时打开 `SSFramework/Demo 教学/维护与校验`，运行 CodeRef 校验；目录元数据通过自动发现。
- 改公开设计时同步 ADR / guide / Demo / 最近层级 AGENTS；只改实现细节则不要制造文档噪音。
- 不直接修改第三方库，不手改 `.unity` / `.prefab` YAML。
