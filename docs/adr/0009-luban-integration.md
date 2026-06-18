# ADR-0009：Luban 配置表集成 —— 构建期生成 + 运行期经资源系统加载

**Status:** Accepted（2026-06-12 落地：框架模块 + 生成管线 + Demo 章节；本文件由 Proposed 设计稿更新而来。**2026-06-18 修订 §3**：配置从「Model + InitSystem 两件套」改为**单个自加载的配置 Utility 服务**——配置是全层只读引用数据，做成 Utility 让各层含 View 直读、消掉查询 Command，详见 §3）

## Context

项目需要配置表方案，已引入 Luban：运行时库走 UPM 包 `com.code-philosophy.luban`（`Luban.Runtime`：ByteBuf / BeanBase），codegen CLI 放 `Tools/Luban/`（构建期工具，不入库，官方 release 可重下——`.gitignore` 已注明）。需要确定：构建期如何生成、运行期如何加载、在框架里如何定位、与热更（[0008](0008-hybridclr-integration.md)）如何联动。

## Decision

### 1. 构建期（codegen）

- 表定义（XML）与数据（JSON / Excel）放各自的 conf 源目录（demo 那套在 `Demo/Configs~/`，`~` 后缀不被 Unity 导入）；Editor 菜单 `SSFramework/配置表构建` 封装 Luban CLI。路径与目标收口在 `LubanConfigProfile`（每套配置一份 SO），生成逻辑在 `LubanCodeGenerator`（均属 `Game.Framework.Config.Editor`）。工程可并存多套 profile（demo + 正式游戏），`ResolveAll()` 返回全部、逐套生成。
- 一次生成产出**三件套**：配置 C# 类（cs-bin）+ 二进制数据（bin → `*.bytes`，落资源收集范围内）+ **表清单**（`LubanTableManifest.g.cs`，CLI 跑完后由管线扫数据目录补写）。
- 输出格式定 **binary**（紧凑、解析快）；数据源按表选、同项目混搭（表定义的 `input` 决定）——demo 双样例：`item.json`（JSON 文本，git diff 可读、AI 可维护）+ `monster.xlsx`（Excel，策划直接编辑）。

### 2. 运行期加载：清单预载 + 同步构造

cs-bin 生成的 `Tables` 构造函数是**同步** `Func<string, ByteBuf>`，而框架资源加载是异步——所以不直接对接，而是：**按表清单并行预载全部数据文件进内存 → 用同步取字节的委托一次性构造 `Tables`**。清单与代码/数据同次生成，不存在手工维护漏表（机制同热更代码包 manifest）。配置数据打进 YooAsset 包（按文件名寻址），复用框架统一资源通道，数据文件随资源热更。

**数据走普通 AB 收集（TextAsset 取 `.bytes`），不走 RawFile**——落地实测：YooAsset 3.0 的 bundle 类型是**包级二选一**（`EBundleType`），AB 包混不进 RawFile 收集器（运行时按 AB 句柄加载 `.rawfile` 直接失败）；RawFile 需要专门的 RawFileBuildPipeline 独立包（如 CodePackage）。配置体积小，TextAsset 通道零额外构建配置、Host/Simulate 全兼容。预载直接 `Bag.LoadBytes`——provider 按包构建管线（manifest 记录）自动路由通道：普通 AB 包按 TextAsset 取内容、RawFile 包走原生文件，内容拷出即释放句柄（该路由同时根除了「LoadBytes 只对 RawFile 包有效」的 API 陷阱）。若未来出现大体积二进制资产需求，再立项「业务 RawFile 包」支持（构建器 + provider 文件系统参数 + 包配置三处扩展）。

### 3. 框架定位：独立模块 `Game.Framework.Config`，后端无关（自加载的配置 Utility 服务）

收进独立 asmdef（`autoReferenced:false`，在热更列表）。配置是**静态只读引用数据**——生成的 `Tables` / `TbXxx` 本身就是数据模型，框架不再为它套一层 Model；做成一个**自加载的 Utility 服务**，各层（含 View）只读取用：

| 角色 | 层 | 职责 |
|---|---|---|
| `MonoConfigUtilityBase<TTables>`（→ 自动注册 `IConfigUtility<TTables>`） | Utility | **自加载**：清单并行预载 → 调抽象工厂构造 → 持有 `Tables` + `ConfigInitState`，对各层只读暴露 |

**框架模块不引用 Luban**——只做「清单 → 字节 → 抽象工厂」的通用编排；Luban 接触面收口在项目侧子类的 `CreateTables`（一行 `new Tables(f => new ByteBuf(getBytes(f)))`）与生成代码所在 asmdef。泛型按项目表根闭合（一行子类），各层经 `GetUtility<IConfigUtility<Tables>>().Tables` 直读（View 也有 `ICanGetUtility`，无需查询 Command），查询直接用生成的强类型 API。

> **为什么是 Utility 而非 Model**（2026-06-18 修订）：初版仿资源系统拆「Model 持表 + InitSystem 编排」两件套，但配置的访问形态是「全层只读」，而本框架 Model 把 View 挡在外面（无 `GetModel`），导致 View 读配置要绕查询 Command。改为 Utility 后 View 直读（`IUtility : ICanGetUtility`，且 Utility 可取资源服务自加载）；配置加载也比资源系统简单（无多包 / CDN / 下载编排），不必拆 System，合成一个组件。资源系统仍是三件套（加载复杂、且其 Model 持的是可变的运行期配置）。

### 4. 程序集与热更归属（开放决策全部落定）

- 工具 CLI：`Tools/Luban/`（不入库）；缺 .NET 8 运行时时管线带 `DOTNET_ROLL_FORWARD=LatestMajor` 运行。
- `Luban.Runtime`（UPM 包）：**AOT**，稳定基础设施。
- `Game.Framework.Config`：引用热更内核 → **在热更列表**（0008 铁律自动校验通过，拓扑序 Framework → Asset.Yoo → Config → Game.Main）。
- 生成代码：归业务程序集（demo 归 `Game.Framework.Demo`，editor-only）；业务热更则生成代码天然在热更侧。

## Consequences

- ✅ 配置加载复用 `IAssetUtility`（自加载 Utility 直接 `GetUtility` 取它，靠 `IUtility : ICanGetUtility`），与资源系统同一套初始化/多包/CDN/热更机制。
- ✅ 框架模块后端无关：换 JSON / 自定义格式只换项目侧工厂；`Assets/Game/Framework/Config/` 可整目录删除，框架其余零感知。
- ⚠ 生成代码 namespace（topModule）**不得嵌进含 `System` 子命名空间的层级**（如 `Game.Framework.*`）：生成代码裸写 `System.Func` 会被就近解析劫持（CS0234）。demo 用顶层 `DemoCfg`。
- ⚠ Luban 会清理生成代码目录里的陌生文件（表清单须在 CLI 之后补写——管线已按此顺序实现），该目录勿手放文件。
- ⚠ 配置只读、启动一次性加载：数据热更随资源包即可；表结构变化会改生成代码，需走代码热更/发版。
- ⚠ **无单表级懒加载**：Luban 的 `Tables` 是同步、一次性构造全表（且跨表 `ResolveRef` 要全表在场），框架据此「按清单预载全部 → 同步构造」。需要「用到才加载」时按两个更合适的粒度组合现成原语——**包级下载**（`.bytes` 随资源包按需下载 / 热更）+ **配置集拆分**（DLC / 活动 / 巨表自成一套 `Tables` + 服务，让服务组件晚实例化才触发其 `Start` 自加载，每套内部 `ResolveRef` 完整）——不另设单表 lazy API（绕过 `Tables` 单独构造 `TbXxx` 会丢 ResolveRef，对小配置得不偿失）。

用法手册见 `docs/framework-guide.md` §16；活样例见 demo「配置表 · Luban」章 + `Demo/Configs~/`。
