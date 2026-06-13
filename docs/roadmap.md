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

### Phase 2 —— UI Toolkit

核心已就绪，新增 UI Toolkit 适配层即可：
- 一个包装 `VisualElement` 的纯 C# View 基类，实现 `IView` + `IHasGameContext`，复用 `ViewExtensions`（`ExecuteCommand`）/ `EventExtensions`（`RegisterEvent`）/ `DisposableBag`（订阅与资源）。
- 为 UI Toolkit 的数据绑定（`DataBinding` / `INotifyBindablePropertyChanged`）桥接 `ReadOnlyReactiveProperty<T>`。
- UGUI 与 UI Toolkit 可共存于同一 Context，按界面选择视图技术。

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
| 配置表（Luban） | ✅ 已落地 | 构建期菜单跑 CLI 生成「代码 + 数据 + 表清单」三件套；运行期 `Bag.LoadBytes` 清单预载 + 三段式（`Game.Framework.Config`，后端无关、不引用 Luban）。数据源 JSON/Excel 混搭，demo 双活样例。ADR-0009 |
| UPM 抽包 | 🔮 规划 | 框架稳定后从 `Assets/Game/Framework` 抽成内嵌/独立 UPM 包。ADR-0010 |

## 规划中的模块（待选型研究）

以下能力已纳入路线，**具体方案后续研究选型再定**，遵循框架"融合优秀库、藏在接口后"的一贯做法（像 `IAssetProvider` 隔离 YooAsset 那样隔离第三方）。

| 模块 | 候选方案 | 设计方向 |
|---|---|---|
| **本地存储** | SQLite（关系/大数据）、PlayerPrefs（轻量 KV）、MemoryPack（高性能二进制序列化） | 统一 `IStorageUtility` / `IStorageProvider` 抽象，按数据规模选后端；存档/配置/KV 分场景；序列化器可插拔 |
| **网络** | BestHTTP、UnityWebRequest 封装、gRPC（MagicOnion）、WebSocket | `INetworkUtility` / 服务抽象隔离传输层；请求/长连接/重试/取消接 UniTask + CancellationToken；回包转 Command/Event |
| **UI 框架** | UGUI 之上的窗口/栈/层级管理 + 资源加载 + 生命周期 | View 层之上的 UI 调度（打开/关闭/层级/缓存）；与对象池、资源系统、Bag 协同 |
| **UI Toolkit 融合** | 见 Phase 2 | 包装 `VisualElement` 的纯 C# View 实现 `IView + IHasGameContext`，复用 ViewExtensions/EventExtensions/Bag；数据绑定桥接 `ReadOnlyReactiveProperty<T>` |
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

## 文档地图

- [framework-guide.md](framework-guide.md) —— 完整用户手册（理念 + 各层用法 + 数据流）
- [ai-collaboration-guide.md](ai-collaboration-guide.md) —— AI 协作方案设计原理
- `Assets/Game/AGENTS.md` —— 框架 **API 使用规则**（写业务代码时就近加载）
- `Assets/Game/Framework/AGENTS.md` —— 框架 **内部编码规则**（改框架源码时就近加载）
- [adr/](adr/) —— 架构决策记录（为什么这样设计）
- [unity-mcp-tips.md](unity-mcp-tips.md) —— Unity MCP 调用陷阱
