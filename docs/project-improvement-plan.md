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

## 已验证基线（2026-08-24）

| 维度 | 当前事实 |
|---|---|
| Unity | 6000.3.22f1 |
| Framework Module | 23 个 asmdef Module；依赖与删除测试见 `framework-module-map.md` |
| Demo | 32 个自动发现章节；Catalog 集中拥有 Adapter 生命周期，并按 Capability / Concept / Workflow 校验真实 Build 教学语义 |
| 教程 | `framework-guide.md` 28 章 |
| ADR | 0001–0037；0035 为 Container Factory 所有权，0036 为 AI PlayMode 预检，0037 为 UI Loading 所有权 |
| 测试 | PlayMode 422 + EditMode 106，全绿；交互式 MCP 先预检，命令行入口默认 EditMode + PlayMode |
| Demo CodeRef | 299 处可打开源码跳转全部精准命中；注释、文案与外部文档路径不计入源码构造点 |
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
- repo skills 以 `.agents/skills` 为跨工具正文，`.claude/skills` 只做路由；四个入口均通过 validator。
- Demo 目录校验 Id / Title / Category / Order / Summary；CodeRef 防腐校验已实跑。
- 新增跨工具 AI 协作指南与 Framework Module 地图。
- 新增跨工具 PlayMode 自动化预检：显式保存有路径脏场景、未命名场景 fail-fast，不用全局 Hook 劫持人工 Play；Editor 契约测试锁定“整批先验证再写入”，并以每用例唯一且显式持有的临时目录防止误删用户资产。
- Editor 工具补齐响应式布局：诊断/配置窗口按宽度重排，`AssetReference` 在窄 Inspector 自动纵向降级，UI Binding 的 Inspector、节点/总览 Popup 与 Overlay 在窄宽度或低工作区仍保留全部操作。
- Popup 高度按所在显示器工作区预算并把完整内容放进滚动视口；布局测试覆盖极窄宽度、负坐标显示器、无效分辨率与偏好状态恢复，不会为测试意外创建配置资产或残留全局 `EditorPrefs`。

### P1 · Demo 教学质量与分层术语对齐

- 为 32 章建立“定位 → 可操作行为或可验证样板 → 设计取舍 → 适用边界/下一步”的渐进教学契约；概念章不为凑按钮伪造交互，顶部 Summary 由运行期强制 ≤160 字、≤2 句，避免导航说明挤成正文。
- Demo 外壳新增“本组 / 全部”进度与章节底部上一步/下一步导航；入门/核心提示顺读，能力/进阶明确可按需跳转，实际按钮切章与滚动复位已验证。
- `DemoModuleHost` 在真实 Build 中记录教学语义，Catalog 按能力/概念/工作流分别检查定位、解释结构、交互或步骤；源码注释、死代码和早退不再能靠 token 数量假绿。
- 场景依赖缺失统一用结构化降级页说明“为什么不可用 → 如何恢复 → 接下来怎么学”，并强制提供接线源码；UGUI、UI 框架、多 Context 与字体的顶层早退已迁移。
- 新建独立 Demo PlayMode Module，在真实 DemoScene 中穿过 Context、Catalog 与 Shell 逐章 Build 32 个 Adapter，并用真实 UGUI/UI 框架章节覆盖降级路径；当前 CodeRef 防腐覆盖 299 处精准源码构造。
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
- 相邻公共 Interface 注释补齐 `LocaleFontChain` 的字体表快照、OS 资产所有权、Dispose 义务与 Apply 异常，以及 `LocaleFontProfile` 的只读查看和非所有权语义。
- 迁移按 Module 做定向回归并保留原 Console 文案与 Unity Object context；Logging 自身实现、第三方 Adapter 和编辑器工具不做机械替换。
- DisposableBag 补齐释放异常隔离：取消回调或单个 `IDisposable` 失败不会截断余下清理，并有契约测试锁定。

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

- 新增 `SSFramework/诊断/模块裁剪审计`：以当前目标平台 Player 编译图确定候选，再读 DLL 真实元数据引用，避免把 auto-reference 的“编译可见”误算成运行时依赖。
- 报告 Core-only / Core + UGUI / Core + Toolkit / 全部 Runtime / 当前 HybridCLR 热更档位的原始托管闭包，并机器执行 Core、两个 UI 后端与 Bridge 的删除测试。窗口改为“健康结论 → 关键数字 → 通俗建议 → 常用组合卡片”的渐进披露；完整模块、热更配置、程序集清单和原始报告默认折叠，620px 以下按钮与指标卡纵排且使用内容高度，避免裁剪和重叠。
- Core、Fonts、Proto、UI 与两个后端的真实外部依赖全部回写 asmdef 显式声明，审计不再发现隐式依赖；ADR-0010/0027、Module 地图、guide §24/§26、Framework AGENTS 与 Demo 接入章同步依赖语义。
- 原始 DLL 字节明确不等于最终包体；先用它发现值得实测的候选，再以 WebGL/小游戏 Player BuildReport 决定是否拆 `ReactiveListBinding` 或 Core 能力，避免为理论体积制造浅 Module。

### P2 · Demo 动态字体资产仓库卫生

- 清空 `DemoLatin SDF` 与 `DemoNotoSansSC SDF` 中由编辑器会话生成的 glyph / character / atlas 缓存，保留 Dynamic 模式、源字体引用、atlas 配置与 `Clear Dynamic Data On Build`；序列化资产合计减少约 4.2 MiB，运行时仍按需生成字形。
- 这是源码仓库体积与 diff 稳定性优化，不宣称玩家包同步减少：两份资产原本就启用了构建时清理，最终包体仍由目标平台 BuildReport 判断。

## 下一批候选（按杠杆排序）

| 优先级 | 候选 | 证据 / 完成标准 |
|---|---|---|
| P1 | 日志调用面继续收敛 | ADR-0034 已 Accepted；Fonts Runtime 已收敛，Asset / Audio / UI / Boot 等仍有历史裸 `Debug.*`。按 Module 渐进审查，保留 Logging Implementation、第三方 Adapter、Editor 工具及 AOT Boot 的必要原生日志；测试守住消息、context、异常和双击定位语义。 |
| P1 | 公共 API 注释审计 | 优先生命周期、取消、异常、所有权与 Adapter 接缝；删除复述代码或记录历史的注释。以“调用者能否仅靠悬浮提示正确释放/取消”为完成标准。 |
| P1 | CI 真正接线 | 当前脚本已可作为门禁；选择 GitHub Actions / 自建 Runner 后再落配置，避免仓库里放一份无人运行的“装饰性 CI”。 |
| P2 | 大文件按职责复查 | `DiagnosticsWindow`、`YooAssetProvider`、`AssetUtility` 等只在发现两个独立变化轴或测试 Seam 时拆；单纯行数不是理由。 |
| P2 | 轻量/Web 真实构建矩阵 | Module 审计已提供编译图、真实托管引用与删除测试；下一步用 Core-only / 单 UI 后端最小消费场景跑目标平台 Player BuildReport，记录链接压缩后的真实增量，再决定是否拆 ReactiveListBinding / InputSystem Back Driver / Core 能力。 |
| P2 | UPM 抽包准备 | 按 Module 删除测试清理业务反向依赖、Samples~/Documentation~ 与第三方声明；达到真实复用需求再执行 ADR-0010 路线。 |

## 每批完成门禁

- `git diff --check`；新增 C# 经 Unity 编译零错误；PowerShell 经 AST parser。
- 相关 fixture 定向测试先绿，再跑 EditMode + PlayMode 全量。
- 改 Demo 时运行 `SSFramework/诊断/校验 Demo 源码跳转锚点`，目录元数据通过自动发现。
- 改公开设计时同步 ADR / guide / Demo / 最近层级 AGENTS；只改实现细节则不要制造文档噪音。
- 不直接修改第三方库，不手改 `.unity` / `.prefab` YAML。
