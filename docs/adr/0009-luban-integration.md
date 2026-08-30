# ADR-0009：Luban 配置表集成 —— 构建期生成 + 运行期经资源系统加载

**Status:** Accepted（2026-06-12 落地：框架模块 + 生成管线 + Demo 章节；本文件由 Proposed 设计稿更新而来。**2026-06-18 修订 §3**：配置从「Model + InitSystem 两件套」改为**单个自加载的配置 Utility 服务**；**2026-08-26 修订 §3.1**：把命令式就绪、根异常与调用方取消收进 Interface，避免业务重复轮询终态；**2026-08-28 修订 §1**：生成改为暂存校验后的双目录可恢复事务；**2026-08-30 修订 §3.2**：增加保留 Context 的短读取 / 门禁入口，并明确拒绝全局静态表与默认逐表权限矩阵）

## Context

项目需要配置表方案，已引入 Luban：运行时库走 UPM 包 `com.code-philosophy.luban`（`Luban.Runtime`：ByteBuf / BeanBase），codegen CLI 放 `Tools/Luban/`（构建期工具，不入库，官方 release 可重下——`.gitignore` 已注明）。需要确定：构建期如何生成、运行期如何加载、在框架里如何定位、与热更（[0008](0008-hybridclr-integration.md)）如何联动。

## Decision

### 1. 构建期（codegen）

- 表定义（XML）与数据（JSON / Excel）放各自的 conf 源目录（demo 那套在 `Demo/Configs~/`，`~` 后缀不被 Unity 导入）；Editor 工作台 `SSFramework/代码生成/配置表 (Luban)` 封装 Luban CLI。路径与目标收口在 `LubanConfigProfile`（每套配置一份 SO），生成逻辑在 `LubanCodeGenerator`（均属 `Game.Framework.Config.Editor`）。工程可并存多套 profile（demo + 正式游戏），`ResolveAll()` 返回全部、逐套生成。
- 一次生成产出**三件套**：配置 C# 类（cs-bin）+ 二进制数据（bin → 根目录 `*.bytes`，落资源收集范围内）+ **表清单**（`LubanTableManifest.g.cs`，由已验证的数据快照生成）。当前运行时与清单只支持 `cs-bin + bin`，两者由生成管线固定，不再作为 Profile 的假配置轴；旧公开 getter 仅返回常量供 Editor 源码迁移兼容。
- CLI 的 target / codeTarget / dataTarget / conf / `outputCodeDir` / `outputDataDir` 由管线统一提供，并强制 `validationFailAsError` 把 Luban validator 失败提升为非零退出；附加参数不能重复设置，watchDir 也因常驻循环被拒绝。CLI 非零、超时、只产出半套、出现空文件、非 UTF-8 C#、非 `.cs` / `.bytes` 文件、嵌套数据或大小写冲突时直接丢弃 staging，正式目录不变。通过校验的 C# 与 manifest 固定为无 BOM 的 LF，避免不同 OS 反复重写；长耗时 CLI 结束后、真正写盘前再次强制重采跨 Module 输出 claim。
- `LubanGenerationTransaction` 为代码与数据两棵独占目录计算联合差量：未变文件不写并保留 `.meta`，更新保留原 `.meta`，陈旧文件连配对 `.meta`、孤儿 `.meta` 与快照不再需要的空目录一并清理，manifest 随代码树最后发布；大小写目录变化和目录 / 文件拓扑替换会先清旧拓扑再写新产物。首次修改前完整备份两棵树；当前进程内任一步失败会同时回滚，回滚也失败则保留 recovery 路径并聚合两类错误。提交期间抑制 Unity 自动刷新，全部成功后才刷新一次；全未变不刷新。
- 这是 Config Editor 内部的专属 Implementation，不抽成与 Protobuf 共用的发布管线：后者只认领单目录 `*.g.cs` 后缀并必须保留邻居文件，Luban 则独占两个目录且 manifest 与数据集合耦合。跨两个任意目录无法承诺 Editor 被强杀或断电时仍原子；那需要持久 journal 与下次启动恢复，不属于当前保证。
- 输出格式定 **binary**（紧凑、解析快）；数据源按表选、同项目混搭（表定义的 `input` 决定）——demo 双样例：`item.json`（JSON 文本，git diff 可读、AI 可维护）+ `monster.xlsx`（Excel，策划直接编辑）。

### 2. 运行期加载：清单预载 + 同步构造

cs-bin 生成的 `Tables` 构造函数是**同步** `Func<string, ByteBuf>`，而框架资源加载是异步——所以不直接对接，而是：**按表清单并行预载全部数据文件进内存 → 用同步取字节的委托一次性构造 `Tables`**。清单与代码/数据同次生成，不存在手工维护漏表（机制同热更代码包 manifest）。配置数据打进 YooAsset 包（按文件名寻址），复用框架统一资源通道，数据文件随资源热更。

**数据走普通 AB 收集（TextAsset 取 `.bytes`），不走 RawFile**——落地实测：YooAsset 3.0 的 bundle 类型是**包级二选一**（`EBundleType`），AB 包混不进 RawFile 收集器（运行时按 AB 句柄加载 `.rawfile` 直接失败）；RawFile 需要专门的 RawFileBuildPipeline 独立包（如 CodePackage）。配置体积小，TextAsset 通道零额外构建配置、Host/Simulate 全兼容。预载直接 `Bag.LoadBytes`——provider 按包构建管线（manifest 记录）自动路由通道：普通 AB 包按 TextAsset 取内容、RawFile 包走原生文件，内容拷出即释放句柄（该路由同时根除了「LoadBytes 只对 RawFile 包有效」的 API 陷阱）。若未来出现大体积二进制资产需求，再立项「业务 RawFile 包」支持（构建器 + provider 文件系统参数 + 包配置三处扩展）。

### 3. 框架定位：独立模块 `Game.Framework.Config`，后端无关（自加载的配置 Utility 服务）

收进独立 asmdef（`autoReferenced:false`，在热更列表）。配置是**静态只读引用数据**——生成的 `Tables` / `TbXxx` 本身就是数据模型，框架不再为它套一层 Model；做成一个**自加载的 Utility 服务**，各层（含 View）只读取用：

| 角色 | 层 | 职责 |
|---|---|---|
| `MonoConfigUtilityBase<TTables>`（→ 自动注册 `IConfigUtility<TTables>`） | Utility | **自加载**：清单校验/快照 → 并行预载 → 调抽象工厂构造 → 持有 `Tables` + `ConfigInitState`，对各层只读暴露 |

**框架模块不引用 Luban**——只做「清单 → 字节 → 抽象工厂」的通用编排；Luban 接触面收口在项目侧子类的 `CreateTables`（一行 `new Tables(f => new ByteBuf(getBytes(f)))`）与生成代码所在 asmdef。泛型按项目表根闭合（一行子类），各层在已知就绪时经 `GetConfig<Tables>()` 直读、流程门禁经 `EnsureConfig<Tables>(token)`（View 也有 `ICanGetUtility`，无需查询 Command），查询直接用生成的强类型 API。

> **为什么是 Utility 而非 Model**（2026-06-18 修订，2026-08-30 对齐 ADR-0046）：初版仿资源系统拆“Model 持表 + InitSystem 编排”，但配置的访问形态是全层只读，本框架 Model 又刻意不向 View 开放，导致 View 读配置要绕查询 Command。改为 Utility 后 View 可直读，且 Utility 能取资源服务自加载。资源系统后来也按同一原则复核：其运行参数不是业务 Model，薄初始化编排不值得独占 System，已收敛为 `AssetUtility + AssetRuntimeSettings` 单入口；多包/CDN 的复杂性留在同一个深 Utility 内。

#### 3.1 Config Readiness：观察状态与阻断流程分开

`IConfigUtility<TTables>` 同时保留三种访问形态，各自只负责一种需要：

| Interface 成员 | 适用场景 | 契约 |
|---|---|---|
| `State` | 加载提示、按钮禁用态、失败提示 | 响应式观察 Idle → Loading → Ready/Failed；Ready 发布前 `Tables` 已写入 |
| `EnsureReady(token)` | 启动流程、进入玩法前的硬门禁 | 返回同一份表根；Failed 重新抛出该次加载的原始异常 |
| `Tables` | 已由上游证明 Ready 后的同步热路径 | 普通只读快照；不承担等待与错误传播 |

**为什么不让业务继续 `WaitUntil(State is Ready or Failed)`**：这种写法在每个调用方重复终态编排，而且 `Failed` 只是枚举，原始的缺资源、清单不一致或反序列化异常已经丢失。`EnsureReady` 把“等一次共享尝试 → 返回表根 / 抛根因”藏进 Interface，调用方不需要知道 Implementation 用 RP、completion source 还是别的同步原语。

**取消所有权**：传给 `EnsureReady` 的 token 只让该 waiter 离开，不传给物理加载；一个界面切走不能截断其他 System 共享的配置加载。组件与 Context 的 owner token 才会取消物理加载及剩余等待。加载仍是一锤子自加载，失败后不隐式重试；需要重试就重建所属 Context / 组件，保持同一作用域内表根身份稳定。

**失败与清单边界**：Implementation 在任何资源 I/O 前复制并校验 `TableFiles`，拒绝空清单、空 location 与重复 location；同时拒绝 `CreateTables` 返回 null。失败通过统一 `Log` Seam 记录一次根异常，并以 `ExceptionDispatchInfo` 保存给现在或稍后加入的 `EnsureReady` 调用者。完成信号只表达终态，不直接携带异常，避免无人等待时产生未观察的 UniTask 异常。

#### 3.2 Context 感知的短入口，而非全局静态表

`ConfigAccessExtensions` 提供两个语义明确的便利入口：已证明 Ready 的同步路径使用 `this.GetConfig<TTables>()` / `ctx.GetConfig<TTables>()`；需要阻断流程时使用 `await this.EnsureConfig<TTables>(token)`。前者统一 fail-fast 诊断，后者只转发既有 `EnsureReady`，两者都按调用方的精确 Context 解析同一个 `IConfigUtility<TTables>`，不缓存全局引用、不复制表根与 readiness 状态机。高频调用者应缓存返回的 `Tables`，之后直接 `_tables.TbItem[id]`。

不采用静态 `TbItem.Get(...)` / `Tables.Current`：为了少写一个 Context 跳转而引入 ambient state，会破坏父子 Context override、多配置集并存、销毁重建语义和测试隔离。也不默认按 View / System 为每张表生成访问 Interface：客户端已携带的只读数据不是安全边界，逐表名单会镜像 schema、形成浅 Module。需要真正隔离时，在更有 Leverage 的 Seam 处理——不应下发的数据用 Luban target / group、独立配置集或服务端归属；需要隐藏业务解释或冻结可变投影时，再建立领域查询 Interface 与至少两个 Adapter。

### 4. 程序集与热更归属（开放决策全部落定）

- 工具 CLI：`Tools/Luban/`（不入库）；缺 .NET 8 运行时时管线带 `DOTNET_ROLL_FORWARD=LatestMajor` 运行。
- `Luban.Runtime`（UPM 包）：**AOT**，稳定基础设施。
- `Game.Framework.Config`：引用热更内核 → **在热更列表**（0008 铁律自动校验通过，拓扑序 Framework → Asset.Yoo → Config → Game.Main）。
- 生成代码：归业务程序集（demo 归 `Game.Framework.Demo`，editor-only）；业务热更则生成代码天然在热更侧。

## Consequences

- ✅ 配置加载复用 `IAssetUtility`（自加载 Utility 直接 `GetUtility` 取它，靠 `IUtility : ICanGetUtility`），与资源系统同一套初始化/多包/CDN/热更机制。
- ✅ 框架模块后端无关：换 JSON / 自定义格式只换项目侧工厂；`Assets/Game/Framework/Config/` 可整目录删除，框架其余零感知。
- ✅ `State`、`EnsureReady` 与 `Tables` 分别覆盖观察、流程门禁和同步读取；调用方无需复制状态机，失败仍保留原始异常与堆栈。
- ✅ `GetConfig<TTables>` / `EnsureConfig<TTables>` 删除常见的 Utility 解析样板与分散的未就绪判断，同时保留精确 Context、多配置集和可测试性；热路径仍直接使用生成表，不增加逐表 façade。
- ✅ 清单在资源 I/O 前快照并 fail-fast，避免生成清单被运行中修改或重复 location 造成部分加载副作用。
- ✅ CLI 失败与校验失败不再触碰正式目录；可恢复发布把代码、数据和清单的代际一致性从“成功路径约定”变成测试锁定的事务契约，同时避免未变化文件引发无谓重编译。
- ⚠ `EnsureReady` 是一次有意的 Interface 源码扩展：继承 `MonoConfigUtilityBase<TTables>` 的项目 Adapter 自动获得实现；直接实现 `IConfigUtility<TTables>` 的自定义服务必须补齐同样的发布、根异常与取消语义。不能用默认 Interface 方法从 `Failed` 枚举凭空还原根因。
- ⚠ 生成代码 namespace（topModule）**不得嵌进含 `System` 子命名空间的层级**（如 `Game.Framework.*`）：生成代码裸写 `System.Func` 会被就近解析劫持（CS0234）。demo 用顶层 `DemoCfg`。
- ⚠ 代码与数据输出目录都由 Luban 事务独占；正式发布会把本次暂存快照没有的陌生文件视为陈旧产物清理，勿手放文件。输出路径现存链上的 symlink / junction 会被拒绝，避免递归备份或回滚越过 `Assets` 物理边界。
- ⚠ 回滚覆盖当前进程能捕获的文件系统异常；强杀 Editor、进程崩溃或断电期间的跨目录恢复仍需人工依据残留产物判断，不能把两棵目录树描述成文件系统级单次原子 rename。
- ⚠ 2026-08-28 起 Luban Editor 类型从误导性的 `Game.Framework.Build` namespace 迁到与程序集 / ADR 一致的 `Game.Framework.Config.Editor`；脚本 GUID 与 asmdef 名不变，现有 Profile 资产无需重建，直接引用这些 Editor 类型的项目源码需更新 `using`。
- ⚠ 配置只读、启动一次性加载：数据热更随资源包即可；表结构变化会改生成代码，需走代码热更/发版。
- ⚠ **无单表级懒加载**：Luban 的 `Tables` 是同步、一次性构造全表（且跨表 `ResolveRef` 要全表在场），框架据此「按清单预载全部 → 同步构造」。需要「用到才加载」时按两个更合适的粒度组合现成原语——**包级下载**（`.bytes` 随资源包按需下载 / 热更）+ **配置集拆分**（DLC / 活动 / 巨表自成一套 `Tables` + 服务，让服务组件晚实例化才触发其 `Start` 自加载，每套内部 `ResolveRef` 完整）——不另设单表 lazy API（绕过 `Tables` 单独构造 `TbXxx` 会丢 ResolveRef，对小配置得不偿失）。

用法手册见 `docs/framework-guide.md` §16；活样例见 demo「配置表 · Luban」章 + `Demo/Configs~/`。
