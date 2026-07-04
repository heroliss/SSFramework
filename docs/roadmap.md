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

### Phase 3 —— DOTS / ECS

DOTS 是数据/Job/Burst 范式，与引用式 OOP 不同。框架的定位是**协调 ECS，而非替换**：
- `System`/`Utility` 包装 ECS `World`，对外仍暴露接口；`Command` 调度 ECS 系统或写入 `EntityCommandBuffer`。
- Model 中的大规模实体数据交给 ECS，框架负责"用户意图 → ECS 调度"的接缝。
- 当前架构不阻断这条路；具体接入时补充 ADR 与适配层。

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
| UPM 抽包 | 🔮 规划 | 框架稳定后从 `Assets/Game/Framework` 抽成内嵌/独立 UPM 包。ADR-0010 |

## 规划中的模块（待选型研究）

以下能力已纳入路线，**具体方案后续研究选型再定**，遵循框架"融合优秀库、藏在接口后"的一贯做法（像 `IAssetProvider` 隔离 YooAsset 那样隔离第三方）。

| 模块 | 候选方案 | 设计方向 |
|---|---|---|
| **网络** | BestHTTP（付费）、UnityWebRequest 封装、gRPC（MagicOnion）、WebSocket | `INetworkUtility` / 服务抽象隔离传输层；请求/长连接/重试/取消接 UniTask + CancellationToken。**消息建模分两类**：请求-响应 = `UniTask<TResp>` 返回值（不硬塞进事件）；服务器推送/广播 = 转框架 Event（`record struct XxxPushEvent : IEvent`，天然接 R3 订阅）。**序列化随服务器技术栈定**：跨语言后端 / 既有 proto 契约 → Protobuf；双端 C#（如 MagicOnion）→ MemoryPack 更快更省——无论哪种都藏在 provider 后，业务只见强类型消息 |
| **DOTS / 多线程** | 见 Phase 3 | 框架协调 ECS（System/Utility 包 `World`，Command 调度 Job / `EntityCommandBuffer`）；主线程契约与 Job 边界明确 |
| **Cysharp 生态选型** | 见下 | 从 [Cysharp 仓库](https://github.com/orgs/Cysharp/repositories) 评估可融入的库 |

**Cysharp 生态候选**（已用 UniTask + R3）：
- **MessagePipe** —— 高性能消息/事件管线，评估与框架 Event 总线的关系（替代/互补）。
- **MemoryPack** —— 高性能二进制序列化，可作存储/网络的序列化后端。
- **ZLogger** —— 零分配结构化日志，评估与 `FrameworkLog` 的整合。
- **MagicOnion** —— 基于 gRPC 的实时通信，作网络模块候选。
- **ObservableCollections** —— 可观察集合，补 R3 在集合响应式上的空缺。
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
4. **本地化**：表驱动（吃现成配置表）+ 资源按 locale 分包（吃现成多 package），基本是组合既有原语。
5. **兜底字库 + 运行时系统字体**：CJK 全量字库体积大——策略 = 精简常用字集随包 + TMP fallback 链兜生僻字 + 运行时 `Font.CreateDynamicFontFromOSFont` 生成动态 `TMP_FontAsset` 作最后兜底（用户名 / 聊天等不可预知文本）。与本地化一起设计（字体本身也按 locale 切换）。
6. **框架诊断面板（Editor 窗口）**：把散在各组件 Inspector「运行时诊断」折叠组里的信息聚合成一个总览窗口——Context 树 + 各容器本地注册表、事件订阅计数（各 Subject 订阅数，异常增长 = 泄漏嫌疑）、Command 流水（挂 LoggingCommandSystem 装饰器即得，正好验证可插拔设计）、DisposableBag 存活计数、对象池占用/空闲。定位是「框架状态一屏看穿」的调试与泄漏排查入口。
7. **ObservableCollections 评估**：UI 列表绑定是 R3 单值订阅覆盖不到的空缺。

### 长期（已有 ADR / 规划，时机到再动）

- 网络模块、DOTS 接缝（Phase 3）、UPM 抽包（ADR-0010）、Odin 解耦（ADR-0015）。
- **第二个 `IAssetProvider` 实现**（如 Addressables）——目的不是替换 YooAsset，而是用第二实现**验证抽象边界**：只有一个实现的接口不算真抽象。

## 文档地图

- [framework-guide.md](framework-guide.md) —— 完整用户手册（理念 + 各层用法 + 数据流）
- [ai-collaboration-guide.md](ai-collaboration-guide.md) —— AI 协作方案设计原理
- `Assets/Game/AGENTS.md` —— 框架 **API 使用规则**（写业务代码时就近加载）
- `Assets/Game/Framework/AGENTS.md` —— 框架 **内部编码规则**（改框架源码时就近加载）
- [adr/](adr/) —— 架构决策记录（为什么这样设计）
- [unity-mcp-tips.md](unity-mcp-tips.md) —— Unity MCP 调用陷阱
