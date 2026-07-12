# SSFramework 愿景与路线图

## 愿景

打造一个**面向未来的先进 Unity 游戏框架**：

- **结构优秀** —— 清晰的分层（MVCS：View / Command / System / Model+Event / Utility）、编译期权限约束、单向数据流，规模增长不腐化。
- **人类可读** —— 命名、注释、文档解释"为什么这样设计、用错会怎样、框架替你兜住了什么"，而非逐行翻译代码。
- **AI 友好** —— 规则与约束沉淀在就近自动加载的 `AGENTS.md`、决策沉淀在 `docs/adr/`、踩坑沉淀在 `docs/unity-mcp-tips.md` 与 memory；文档与代码保持一致，让 AI 不被过时信息误导。
- **面向未来技术栈** —— 第一阶段兼容 UGUI 等传统栈，逐步接入 UI Toolkit、DOTS 等先进栈，且核心层对 UI/范式保持中立。

## 核心理念（详见 [framework-guide.md](framework-guide.md) §1）

1. **拆开 Controller**：System 管"怎么做"、Command 管"做什么"，一条清晰接缝隔开逻辑与视图开发者。
2. **单向数据流**：View → Command → System → Model；反向只读订阅。任何状态改动有迹可循。
3. **用类型代替字符串/枚举**：事件、Model、Command 都用类型区分，IDE 可追踪、重命名安全。
4. **生命周期统一为 IDisposable**：订阅、资源句柄、子作用域都进 `DisposableBag`，宿主销毁批量清理。
5. **编译期权限**：`ICanGetModel`/`ICanSendEvent` 等接口在编译期约束每层能做什么，不靠口头约定。
6. **引擎组件可跨层**：`Rigidbody`/`Transform` 等天生贯穿数据/逻辑/视图，框架允许它们正交于五层被共享。

## 关键不变量：核心层范式无关

框架的核心层——`Context` / DI 容器 / `Command` / `Model` / `System` / `Utility` / `Event` / `DisposableBag` / 权限接口——**全是范式无关的纯 C#**，不绑定任何 UI 技术或 MonoBehaviour。

唯一绑定 UGUI/Mono 的是：
- `MonoViewBase`（继承 `SerializedMonoBehaviour`）
- `DisposableBag` 的 `UnityEvent` / `Button.onClick` 便利重载

这意味着接入新 UI/范式时，**核心零改动**，只需新增"适配层"。

## 阶段路线图

### Phase 1 —— UGUI + 核心架构（当前）

- ✅ MVCS 五层 + 自研精简 DI 容器（主线程独占、父级回退、运行时覆盖）
- ✅ R3 响应式（`RP<T>` / `ReadOnlyReactiveProperty<T>`）+ UniTask 异步 + YooAsset 资源
- ✅ `MonoXxxBase` 自动注册/注入 + `DisposableBag` 统一生命周期 + `AssetReference<T>` Inspector 拖拽
- ✅ 程序集边界：`Game.Framework` / `.Editor` / `.Demo` / `.Test`
- ✅ 自研对象池（`IPoolUtility`：C# 对象池 + GameObject/Prefab 池，`Bag.Rent` / `Bag.Spawn` 自动归还，替代第三方库）

### Phase 2 —— UI Toolkit ✅ 已落地（ADR-0016）

- ✅ 纯 C# View 基类 `UIToolkitViewBase`（包装 `VisualElement`，实现 `IView + IHasGameContext`），复用 `ViewExtensions` / `EventExtensions` / `DisposableBag`——与 `MonoViewBase` 同享自动注入 / Bag / `ExecuteCommand`。
- ✅ 数据绑定走 R3 订阅（`UIBindingExtensions`：`BindText` / `BindEnabled` / `SubscribeClick`），与 UGUI 一套心智；**刻意不引入** UI Toolkit 原生 DataBinding。
- ✅ UGUI 与 UI Toolkit 共存于同一 Context，按界面选视图技术；核心层对 UI 技术无感。

### Phase 3 —— DOTS / ECS ✅ 组合姿势已验证（ADR-0030）

DOTS 是数据/Job/Burst 范式，与引用式 OOP 不同。框架的定位是**协调 ECS，而非替换**：
- `System`/`Utility` 包装 ECS `World`，对外仍暴露接口；`Command` 调度 ECS 系统或写入 `EntityCommandBuffer`。
- Model 中的大规模实体数据交给 ECS，框架负责"用户意图 → ECS 调度"的接缝。
- ✅ **已由切片 M6 验证**（ADR-0030）：`EcsBattleSim`（Entities chunk + Burst job，自建 World 藏在纯 C# 接缝后）整体置换 OOP 后端，Command/Model/View/事件翻译层零改动；对拍证明行为等价（关 Burst 逐位全等）；4.2 万实体 3.5~4.9× 提速。**框架侧零改动、暂不需要 DOTS 专用模块**——既有原语接得住；可复用样板成形（World 生命周期助手、ECS↔R3 桥）再按五件套立项。

## 正交能力（不分阶段，按需推进）

| 能力 | 状态 | 说明 |
|---|---|---|
| 自研对象池 | ✅ 已落地 | `IPoolUtility`：C# 对象池（`Bag.Rent`）+ GameObject/Prefab 池（`Bag.Spawn`、分帧 `Prewarm`、`PooledObject` 自动路由），随 Bag 自动归还。ADR-0007 |
| 资源系统（YooAsset） | ✅ 原生 3.0 | 经 `IAssetProvider` 隔离；`YooAssetProvider` 已用原生 3.0 API 重写（FileSystem 初始化 + 拆分解密 + `IRemoteService` + RawFileObject），兼容层 define 已移除，obsolete 警告归零。ADR-0012/0013 |
| 热更新（HybridCLR） | ✅ 已落地 | 列表驱动热更范围（`FrameworkHotUpdateProfile` 单一真源），框架本体也可热更；薄 Boot 程序集引导（专用 RawFile 代码包 + 清单 + 拓扑序加载），编辑器旁路零负担；Windows IL2CPP 端到端验证通过（改入口版本→只重打代码包→玩家包生效）。ADR-0008 |
| 配置表（Luban） | ✅ 已落地 | 构建期菜单跑 CLI 生成「代码 + 数据 + 表清单」三件套；运行期 `Bag.LoadBytes` 清单预载 + 一个自加载的配置 Utility 服务持表（`Game.Framework.Config`，后端无关、不引用 Luban）。数据源 JSON/Excel 混搭，demo 双活样例。ADR-0009 |
| UI 框架（UGUI + UI Toolkit） | ✅ 已落地 | 渲染后端无关的窗口/层级/栈/模态/缓存/生命周期调度（`IUIUtility`），`IUIBackend` 后两个 adapter（Canvas / UIDocument）；`[UIWindow]` 特性声明层/缓存/模态；绑定走 R3。核心可单测（脱离场景）。ADR-0016 |
| 本地存储 / 存档 | ✅ 已落地 | `IStorageUtility`：`[Serializable]` 类整存整取（Save/Load/Exists/Delete/ListKeys）；原子写 + 上一版备份自动回退（断电不丢档）；`IStorageProvider`（介质）/ `IStorageSerializer`(格式) 双扩展点，默认文件 + JsonUtility 零依赖；迁移姿势 = Version 字段 + 链式 switch。ADR-0021 |
| 音频服务 | ✅ 已落地 | `IAudioUtility`：音乐单通道（切换自动交叉淡变、同 clip 幂等）+ 池化音效（一次性自动回收、循环 handle 进 Bag 随宿主自动停）+ 分组音量（主 × 组 × 单次，即时生效）。刻意不上 AudioMixer / 不做 provider 层——接口本身就是 FMOD / Wwise 的接缝。ADR-0022 |
| 游戏流程状态机 | ✅ 已落地 | `IGameFlow`：宏观阶段显式化为 `FlowState` 一次性实例（传参走构造），每状态一个子 Context 退出整棵撤（切阶段漏清理被结构性消灭）；转换串行 + 最新意图胜。刻意不做转换表 / HSM / 场景绑定 / 历史栈。ADR-0023 |
| 本地化 | ✅ 已落地 | `ILocalizationUtility`：响应式 Locale（SetLocale 推送、绑定全量刷新）+ 缺 key 裸 key 上屏 + 文本源单方法接缝（业务包配置表 / 内置字典源）；per-locale 资源刻意零 API（多 package 组合）。字体切换归 ADR-0025。ADR-0024 |
| 响应式集合 / 列表绑定 | ✅ 已落地 | `ObservableList<T>` 持有集合状态（如单值用 `RP<T>`）+ `Bag.BindList` 增量绑定（Toolkit / UGUI 双后端，只动变化项、不整表重建，每行独享子 bag）。后端中立增量引擎单点可测、内核零改动；藏在 `Bag.BindList` 后隔离 ObservableCollections。ADR-0027 |
| 网络（HTTP / WebSocket） | ✅ 已落地 | 消息建模双轨：请求-响应 = `IHttpUtility` UniTask 返回值（REST 动词 + `Send` 逃生舱，非 2xx 抛 `NetworkException` 分级）；服务器推送 = `IWebSocketUtility` 经 envelope 映射为框架 Event（`RegisterPush`）。传输（UnityWebRequest / ClientWebSocket）× 序列化（默认 JSON）双接缝可插拔，零第三方依赖留内核。接收循环后台收帧→切主线程扇出。刻意不做自动重试 / 重连 / WebGL 的 WS（给样板 + 留接缝）。ADR-0028 |
| 字体（多语言字体链） | ✅ 已落地 | `MonoLocaleFonts` / `LocaleFontChain`：三层字体策略（①精简主字体随包 + ②per-locale 补充字体 + ③OS 字体运行时兜底）写进主字体 fallback 表，订阅 `Locale` 自动切换、业务零调用；未配置 locale 降级不炸、销毁还原原始表。Editor「生成常用字集」菜单产 charset 喂 TMP Font Asset Creator。刻意不做全字库随包 / atlas 调优 / 远程字体协议。ADR-0025 |
| UPM 抽包 | 🔮 规划 | 框架稳定后从 `Assets/Game/Framework` 抽成内嵌/独立 UPM 包。ADR-0010 |

## 规划中的模块（待选型研究）

以下能力已纳入路线，**具体方案后续研究选型再定**，遵循框架"融合优秀库、藏在接口后"的一贯做法（像 `IAssetProvider` 隔离 YooAsset 那样隔离第三方）。

| 模块 | 候选方案 | 设计方向 |
|---|---|---|
| **DOTS / 多线程** | 见 Phase 3 | 框架协调 ECS（System/Utility 包 `World`，Command 调度 Job / `EntityCommandBuffer`）；主线程契约与 Job 边界明确 |
| **Cysharp 生态选型** | 见下 | 从 [Cysharp 仓库](https://github.com/orgs/Cysharp/repositories) 评估可融入的库 |

**Cysharp 生态候选**（已用 UniTask + R3 + ObservableCollections）：
- **MessagePipe** —— 高性能消息/事件管线，评估与框架 Event 总线的关系（替代/互补）。
- **MemoryPack** —— 高性能二进制序列化，可作存储/网络的序列化后端。
- **ZLogger** —— 零分配结构化日志，评估与 `FrameworkLog` 的整合。
- **MagicOnion** —— 基于 gRPC 的实时通信；网络模块（ADR-0028）已落地 JSON 起步，MagicOnion 是整套 RPC 范式（非本模块传输接缝），真用时「直接用 + 框架管其余」。
- ~~**ObservableCollections**~~ —— ✅ 已融入（ADR-0027）：`ObservableList<T>` + `Bag.BindList` 补 R3 集合响应式空缺，藏在绑定接口后。
- **ZString** —— 零分配字符串构造，UI/日志高频拼接场景。
- 选型原则：先确认"框架真的需要"，再评估与既有栈（UniTask/R3/YooAsset/Odin）的契合度与 AOT/热更兼容性，最后藏在框架接口后引入。

## 建议推进节奏（2026-07 全面审查后）

### 每个功能的固定节奏（完成定义）

一个功能算"做完"= 五件套齐：**① ADR 定决策 → ② 接口在内核、实现在模块（ports & adapters）→ ③ 测试 → ④ demo 章节 → ⑤ guide 章节（+ 必要的 AGENTS 规则）**。这是现有模块（资源 / 热更 / 配置 / UI）已经验证过的节奏，新模块照走。

### 近期：打磨已有（优先于加新模块）

1. **UI 框架补常见刚需**（ADR-0020）：
   - 异步过渡 hook ✅ 已落地：`OnOpenTransition/OnCloseTransition` + 框架全屏挡输入（计数挡板）；逻辑关闭先于表现；CloseAll/销毁直通。
   - Android Back / Esc ✅ 已落地：`Back()` 升级为 Popup→Window→Page 逐层返回导航（`BackClosable` 拦截、过渡中吞掉、空返回 false）+ `MonoUIBackKeyDriver` 接线组件（新旧输入系统双路径）。
   - 安全区适配 ✅ 已落地：UGUI `UGuiSafeArea`（锚进 Screen.safeArea）/ Toolkit `SafeAreaContainer`（padding 换算，UXML 可摆）——opt-in 内容避让，层根/背景保持全屏出血。
   - Top 层常用件 ✅ 已落地：`ShowToast / ShowLoading / HideLoading` 为 IUIUtility 一等方法（后端无关），内置窗口类型表由入口注册；Toast 不拦输入自动关，Loading 模态+拦返回键。
2. **代码生成收尾** ✅ 已全部落地（UI 节点自动绑定——含目录配置 / 占位符 / 引用为源同步 / 变体遮蔽）：
   - ③ **资源 Package 名常量生成** ✅ 已落地：菜单 `SSFramework/资源构建/生成包名常量代码`（构建 profile 配输出路径/命名空间），从收集器包列表生成 `AssetPackages.Xxx` 常量类，替代裸字符串包名（包名改错编译期暴露）。
   - ④ **服务注册代码生成** ✅ 已落地（ADR-0019）：`ServiceInstallerProfile` 配「扫描目录 → 安装器类」，菜单 `SSFramework/服务注册/生成服务安装器代码` 生成显式 `XxxInstaller.Install(builder)`，Context 里一行接线——刻意不做运行时反射扫描：启动零反射、AOT/热更友好、注册关系在 git diff 里可见可审。配套内核语义：构建期值绑定实例在 Context 构造时自动 Inject + AttachTo（纯 C# 与 Mono 路径「注册即注入」对称）。demo 活样板见「服务注册生成 · 安装器」章（`Modules/ServiceInstaller/`）。
3. **资源运营流程 demo** ✅ 已落地：demo「资源运营 · 端到端」章——运营侧发版（构建+部署 = 覆盖 CDN `.version`）→ 客户端启动检查 → 强更下载（进度 / 重建重试 / 断点续传）→ `ClearCache(Unused)` 回收旧版本；核心是可整段搬走的启动器流程活样板 `RunUpdateFlow`。顺带补了唯一缺口 API：`IAssetUtility.GetPackageVersion`（只读当前清单版本，设置页 / 客服排查用）。
4. **CI 护栏** ✅ 已落地：`Tools/run-tests.ps1` 命令行 batchmode 全量跑 PlayMode 测试 + NUnit 结果解析（需先关闭编辑器）。后续可选：接 git pre-push hook / 云端 CI。

### 中期：新功能模块（按"所有游戏都要"排序）

1. **本地存储 / 存档** ✅ 已落地（ADR-0021）：`IStorageUtility` 类型化整存整取 + 原子写/备份回退防损坏 + `IStorageProvider`/`IStorageSerializer` 双扩展点（默认文件 + JsonUtility 零依赖）；迁移姿势 = Version 字段 + 链式 switch（刻意不做迁移管线）。五件套齐：ADR / 内核实现（`Core/Storage/`）/ 测试 / demo「本地存储 · 存档」章 / guide §18 + AGENTS #26。
2. **音频服务** ✅ 已落地（ADR-0022）：`IAudioUtility` 音乐单通道（切换自动交叉淡变、同 clip 幂等）+ 池化音效（`ObjectPool` 原语复用、一次性自动回收、循环音效 handle 进 Bag 随宿主自动停）+ 分组音量（主 × 组 × 单次，即时生效；持久化归业务）；刻意不上 AudioMixer / 不做 provider 层（接口即接缝）。五件套齐：ADR / 内核实现（`Core/Audio/`）/ 测试 / demo「音频 · BGM 与音效」章 / guide §19 + AGENTS #27。
3. **游戏流程状态机** ✅ 已落地（ADR-0023）：`IGameFlow` 显式 Flow——`FlowState` 一次性实例（传参走构造）+ 每状态一个子 Context（私有服务/订阅/资源退出整棵撤）+ 串行转换最新意图胜（在途 OnEnter 协作取消；Enter 失败 = 明确无状态、异常冒给调用方）+ `FlowChangedEvent` 单事件观察；刻意不做转换表/HSM（子 flow 组合即嵌套）/场景绑定/历史栈。五件套齐：ADR / 内核实现（`Core/Flow/`）/ 测试 / demo「游戏流程 · 阶段状态机」章 / guide §20 + AGENTS #28。
4. **本地化** ✅ 已落地（ADR-0024）：`ILocalizationUtility` 小内核——响应式 `Locale`（RP，SetLocale 推送即全量刷新、同值幂等）+ `Get`/格式化（缺 key 回退链 fallbackLocale → 裸 key 上屏 + 一次性警告；模板错返回原文不炸）+ `ILocalizedTextSource` 单方法接缝（构造注入，内置字典源）；Toolkit `Bag.BindLocalizedText`，UGUI/动态参数走 Subscribe/CombineLatest 一行组合；per-locale 资源刻意零 API（多 package/后缀约定组合）。五件套齐：ADR / 内核实现（`Core/Localization/`）/ 测试 / demo「本地化 · 多语言」章 / guide §21 + AGENTS #29。
5. **字体（多语言字体链）** ✅ 已落地（ADR-0025）：三层字体策略——①精简常用字集随包 + ②per-locale 补充字体 + ③OS 字体运行时兜底（`CreateFontAsset(族名, null, 90)`），三层都写进**主字体 fallback 表**（双后端 per-font 表 public 可写，比全局 settings 更对称）；`MonoLocaleFonts` 订阅 `Locale` 自动切换、业务零调用，未配置 locale 降级不炸、销毁还原原始表 + 销毁运行时资产。双后端差异实测：TMP 缺字真豆腐（②③刚需），Toolkit 引擎内建 OS 兜底（②管字形归属）。Editor「生成常用字集」菜单扫配置表/代码/文案出 charset 喂 TMP Font Asset Creator。五件套齐：ADR / 模块实现（`Fonts/`，独立 asmdef 收口 TMP 依赖）/ 测试（`FontFallbackTests`）/ demo「字体 · 多语言字体链」章 / guide §22 + AGENTS #30。
6. **框架诊断面板（Editor 窗口）** ✅ 已落地（ADR-0026）：菜单 `SSFramework/诊断/框架诊断面板`，UI Toolkit 调试器风格（左树 · 右明细 · 下命令表格，搜索过滤 / 双击定位场景对象 / 趋势 sparkline / TSV 导出）——存活 Context 作用域树（纯 C# Context 靠新增 `DebugName` 首次可见）+ 各容器本地注册表（不触发工厂）+ 事件订阅计数 + DisposableBag 存活计数 + 池借出/空闲（`CountActive` 补齐）+ Command 流水（`LoggingCommandSystem` 从文档示例变实物，opt-in 装饰器、验证可插拔设计，demo 已接入）。采集层 `#if UNITY_EDITOR` 编译消除、玩家包零成本；展示层经 InternalsVisibleTo 白盒读取，诊断数据面不进公共 API。五件套：ADR / 内核采集（`Core/Diagnostics/`）+ 窗口（`Editor/`）/ 测试（`DiagnosticsTests`）/ guide §23（demo 章不适用——面板无业务 API，现有 demo 场景即观察素材）。
7. **响应式集合与列表绑定** ✅ 已落地（ADR-0027）：R3 单值订阅覆盖不到的集合空缺——集合状态用 `ObservableList<T>` 持有（如单值用 `RP<T>`），UI 用 `Bag.BindList` 增量绑定（Toolkit 绑 `VisualElement`、UGUI 绑 `Transform`，同一套心智）：集合增删移换只动对应子视图、不整表重建；每行独享子 bag 随行进出自动退订。后端中立的增量引擎（`Game.Framework.UI/ReactiveListBinding.cs`）单点实现、纯 C# 可测，内核零改动、不新增内核依赖。刻意不做虚拟化（大列表用 Toolkit 原生 `ListView`）/ 过滤视图 / 字典绑定。ObservableCollections 从「Cysharp 候选」变成「已融入、藏在 `Bag.BindList` 后」。五件套齐：ADR / 引擎 + 双后端适配 / 测试（`ReactiveListBindingTests`）/ demo「响应式列表 · 集合绑定」章 / guide §24 + AGENTS #31。
8. **网络（HTTP / WebSocket）** ✅ 已落地（ADR-0028）：消息建模双轨——请求-响应 = `IHttpUtility`（REST 动词 `Get/Post` 非 2xx 抛 `NetworkException` 分级 + `Send` 逃生舱交换完成即返回）；服务器推送 = `IWebSocketUtility` 经 JSON envelope `{type,payload}` + `RegisterPush<TEvent>` 映射为框架 Event，`Bag.Subscribe` 消费。传输（默认 UnityWebRequest / ClientWebSocket）× 序列化（默认 JSON）双接缝构造注入、零第三方依赖留内核；超时与外部取消严格区分；接收循环后台收帧→切主线程→扇出（事件系统主线程铁律）。刻意不做自动重试 / 重连 / WebGL 的 WS（给退避样板 + 留 provider 接缝）。环境实测坑：Mono HttpListener 做不了 WS 服务端（demo 用 TcpListener + 手写 RFC6455）、ClientWebSocket 默认直连绕系统代理。五件套齐：ADR / 内核（`Core/Network/`）/ 测试（`HttpTests` + `WebSocketTests`，307 全绿）/ demo「网络 · HTTP 与 WebSocket」章（内嵌离线服务器）/ guide §25 + AGENTS #32。

### 垂直切片 Outpost（13 模块整合验收，ADR-0029）

M0 骨架 → M1 战斗核心 → M2 升级 → M3 存档/音频/本地化 → M4 网络排行（Protobuf + WS 二进制）→ M5 构建收口（玩家包端到端，ADR-0029 六处接缝发现即修）→ **M6 DOTS 后端置换 ✅**（2026-07-11，ADR-0030）：`Game.Outpost.Sim.Ecs`（AOT、永不入热更）的 `EcsBattleSim` 整体置换 OOP 后端、消费方零改动；对拍两级验证（关 Burst 12 波逐 tick 全等 = 移植零逻辑偏差；开 Burst 规格级等价 + **跨编译域浮点 ulp 边界发现**）；4.2 万实体 3.5×（编辑器）/4.9×（近玩家包）提速 → **M7 真弹道碰撞 + 残骸互动 ✅**（2026-07-12，ADR-0031）：hitscan 改飞行弹 + 扫掠碰撞、残骸减速泥地（均匀密度网格，规则本身两后端 O(1) 同实现）、敌人推挤残骸（纯表现）+ 泥地热力图开关；对拍两级复用（关 Burst 逐 tick + **密度网格逐格全等**）；把真实玩法推进"OOP 会掉帧"的量级——真实平台期 Reference p95 14ms（破帧预算）vs Ecs 5ms、合成千级在飞弹 Reference 39ms（15-25fps）vs Ecs 12ms，**后端置换收益从"数字"变成"手感"** → **M8 残骸实体化 + 推挤入模拟 ✅**（2026-07-12，ADR-0032）：把 M7 的表现层推挤扶正为模拟规则（残骸从密度计数升为逐实体 SoA、密度记账跟随位置＝车辙被踩穿），负载随残骸累积增长——**后端差距从"平台期恒定 2.6×"变成"随战局拉大"**：成长期 w12 两后端持平（都 ~0.25ms），平台期 w24 残骸满 2 万时 Reference 13.8ms（破 60fps）vs Ecs 3.25ms（~4.3×）；对拍两级再加"逐槽残骸位置逐位比对"维度（关 Burst 12 波全等），表现层净简化（删整套表现层推挤通道、残骸层改模拟槽位镜像）。残骸上限后调 3 万→10 万，放大后端差距的时间维度。**切片核心目标全部完成**；剩余两个独立小里程碑（2026-07-11 排期）：

- **proto 生产化 ✅**（2026-07-12）：官方 protoc + Google.Protobuf 写 `GoogleProtobufNetworkSerializer : IWebSocketEnvelopeSerializer`（全泛型适配器住业务侧、内核第三方零依赖），.proto 契约 + protoc 编辑器菜单（Luban 姿势，protoc 随仓 `Tools/Protoc/`）→ 生成 `IMessage`；生成类型名对齐旧手写 DTO → 消费方零改动，NewRecordPushEvent 补 IEvent partial。**接缝缺口发现即修**：`RegisterPush<TEvent>` 约束 struct→IEvent（struct 是绑死 JsonUtility 的、挡 class 消息）。Google.Protobuf 3.29.3 经 NuGetForUnity 装入 + link.xml 防 IL2CPP 裁剪。验证：无头往返 + 手写 ProtoWire 解 Google 字节互通 + envelope 逐字节一致 + PlayMode 325/325。
- **服务端生产化 ✅**（2026-07-12）：dev server 移植成独立 ASP.NET Core（Kestrel 原生 WS 扔掉手写 RFC6455）+ SQLite 持久化 + Docker（多阶段 + /data 挂卷），放 Outpost `Server~/`（不进框架、随切片走）。wire 复用同一套 ProtoWire + envelope 契约，客户端切真后端零改动。dotnet build 0 警告 + 实跑 python 标准 protobuf 客户端端到端验证（POST 名次 / GET 榜单 / WS 推送 / SQLite 重启持久）。

**框架 demo 侧**（2026-07-12）：进阶新增「DOTS/ECS · 与框架融合」章（`DotsIntegrationModule`）——讲「框架对 DOTS 零耦合、把它藏在纯 C# 接缝后」的融合模式（五步接入 + World 驱动契约 + 对拍两级 + 何时值得）；刻意只跳转框架自身接缝先例（`IAssetProvider`），Outpost 仅文字指路，保证框架/切片拆包后零断链。

### 长期（已有 ADR / 规划，时机到再动）

- UPM 抽包（ADR-0010，**前置验收已由切片 M0-M6 全程完成**）、Odin 解耦（ADR-0015）。DOTS 接缝已验证（Phase 3 / ADR-0030），框架侧可选模块待真实需求再立项。
- **第二个 `IAssetProvider` 实现**（如 Addressables）——目的不是替换 YooAsset，而是用第二实现**验证抽象边界**：只有一个实现的接口不算真抽象。

## 文档地图

- [framework-guide.md](framework-guide.md) —— 完整用户手册（理念 + 各层用法 + 数据流）
- [ai-collaboration-guide.md](ai-collaboration-guide.md) —— AI 协作方案设计原理
- `Assets/Game/AGENTS.md` —— 框架 **API 使用规则**（写业务代码时就近加载）
- `Assets/Game/Framework/AGENTS.md` —— 框架 **内部编码规则**（改框架源码时就近加载）
- [adr/](adr/) —— 架构决策记录（为什么这样设计）
- [Outpost 导读](../Assets/Game/Outpost/Documentation~/outpost-guide.md) / [技术笔记](../Assets/Game/Outpost/Documentation~/outpost-tech-notes.md) —— 垂直切片 demo 的对照地图与实现方案（随游戏放在其 `Documentation~/`，将来随包提取）
- [unity-mcp-tips.md](unity-mcp-tips.md) —— Unity MCP 调用陷阱
