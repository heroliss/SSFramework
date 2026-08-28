# ADR-0017：DLC 热更（代码 + 内容）—— 单 CodePackage 承载代码 + 运行时按需加载 + 业务 RawFile 包统一构建

**Status:** Proposed（计划，待实现；2026-06-18 起草。实现前此 ADR 仅定方向与边界，不代表已落地）

## Context

DLC = 自洽内容单元（资源 + 玩法代码 + 配置），按需下载，进入时加载、退出时回收。目标是让"再加一个 DLC"= 加一个领域单元，而不是改框架。

现状基线：

- **资源侧成熟**：多 package + 包级「自动初始化 / 按需下载」策略、tag 下载器、按 tag 清缓存（ADR-0008、`docs/asset-system-flow.md`）。
- **代码侧只有单中央包**：`FrameworkHotUpdateProfile` 是「扁平 asmdef 列表 + 单个 `CodePackageName`」，Boot 启动期**一次性**把清单里全部热更 DLL 从 CodePackage（RawFile 包）加载完。没有「运行时按需加载某 DLC 代码」。
- **业务 RawFile 包无法统一构建**：`FrameworkAssetBuilder`（SBP / AssetBundle 管线）对 RawFile 包 fail-fast 指路；视频等大体积原始内容（RawFile）目前不能走统一资源构建。
- ADR-0008 已预留 DLC 方向（「一个领域单元 = 一个 asmdef = 热更列表一行 =（DLC 时）一个资源 package」），但与「边玩边下/版本灰度」一起列为本期不做。

## Decision

### 1. DLC 代码随单一 CodePackage 承载（不每 DLC 一个代码包）

- 所有热更 DLL（含各 DLC）收进同一个 CodePackage（RawFile 包），按 DLC 分 **tag / bundle**。
- **下载**：YooAsset 按 bundle hash 增量——未变 DLL 不重下；大体积 DLC 代码可标 tag 延迟下载（进 DLC 前再拉）。
- **加载**：Boot 启动只 `Assembly.Load` base 组；DLC 代码在**进入 DLC 时**才 `LoadMetadataForAOTAssembly` → 按拓扑序 `Assembly.Load` → 反射调 DLC 入口注册玩法。
- **为什么不每 DLC 一个独立 code 包**：单包复用现有 CodePackage + manifest + tag 机制，部署 / 管理最省。独立 code 包（独立 CDN / 版本隔离 / 第三方后发）留作 P2 扩展点，不焊死。

### 2. AOT 泛型元数据约束（与打包无关，硬约束——最易踩雷）

- **关键认知**：把 DLC 代码放进单 CodePackage **并不解除** AOT 泛型元数据限制。这是 HybridCLR + IL2CPP 对「热更代码里跨 AOT 类型的泛型实例化」的固有约束，只跟「类型 / 泛型实例」有关，**跟 DLL 落在哪个包 / bundle 无关**。换独立 code 包同样受限。
- **含义**：DLC 代码用到的跨 AOT 泛型实例必须在**出主包时 Generate All 已覆盖** ⇒ **DLC asmdef 必须在主工程里**（能被 Generate 扫到）。
- 因此「第三方完全独立后发、主包不知情、且引入新泛型实例的 DLC 代码」**不在能力内**；DLC 限定为「主包构建期已知 asmdef」。若某 DLC 只用已覆盖的泛型（纯逻辑、不新增泛型实例化），纯后发逻辑包可行。

### 3. 运行时加载服务归独立热更模块，隔离 HybridCLR 依赖

- 把加载链（init package/tag → 读 manifest → `LoadMetadataForAOTAssembly` → 拓扑序 `Assembly.Load` → 反射入口）抽成可复用 runtime 服务（如 `IHotUpdateCodeLoader.LoadModule(tag/manifest)`）。
- **落点**：新建小模块 `Game.Framework.HotUpdate`（热更程序集，引用 HybridCLR.Runtime + YooAsset），**不放内核**——内核当前刻意不依赖 HybridCLR，DLC loader 进核心会把热更机制依赖渗进内核。
- Boot（AOT 薄壳）仍只管 base 引导，**与 DLC loader 各持一份加载逻辑**——因为 Boot 须保持 AOT、不引用热更程序集，无法复用热更侧的 loader。少量重复换取边界干净，可接受。
- 加载顺序按引用图拓扑排（复用 ADR-0008 的清单拓扑序机制）。

### 4. 生命周期：增量加载、撤就整棵撤

- DLC 玩法层注册到**独立子 Context**；退 DLC 时整树 `Dispose`（呼应 ADR-0005「换血不允许，撤就整棵撤」）。
- **程序集不可卸载**（C# 限制）⇒ 退 DLC 回收的是对象 / Context，**不是 DLL**；重进 DLC 复用已加载程序集。

### 5. 构建管线：资源 Profile 显式声明归属，未来统一支持业务 RawFile 包

- 扩展 `FrameworkAssetBuilder`：遇 RawFile 包**额外跑 `RawFileBuildPipeline`**，而非 fail-fast。恢复与 YooAsset 原生能力对齐，不阉割 RawFile（视频 / 原始数据等常见内容可走统一构建）。
- **代码包由 Profile 显式排除，不让资源 Module 读取热更配置**：代码包带 `CompileDll` + manifest + AOT 补元数据的特殊配方，归 `Game.Framework.Build.HybridCLR.Editor`。它在资源 Profile 中必须关闭“参与构建”；资源 Module 不读取 `FrameworkHotUpdateProfile`、不按默认包名猜另一个可删除 Module。误启用或由 CLI 点名时，在任何产物写入前明确失败并指向专属配方。
- **目标结果**：执行「资源构建」= 构建 Profile 中启用的普通 AB 包 **+ 未来启用的业务 RawFile 包**；代码包保持禁用并独立走热更新工作台。删除热更新 Module 后，资源构建不需要修改源码或恢复虚构默认名称。
- **落地状态**：① 构建 Editor Module 依赖拆分、代码包显式禁用和 RawFile fail-fast——**已实现**（ADR-0045）；② 业务 RawFile 包走 `RawFileBuildPipeline`——**待实现**（无业务 RawFile 包消费方前不盲写，等首个真实 RawFile 包 / Demo DLC 一起验证）。

## Consequences

- ✅ DLC 代码复用现有 CodePackage / manifest / tag，新增面最小；下载增量、加载按需。
- ✅ 业务 RawFile 包（视频等大体积内容）走统一资源构建，不再被迫 fail 或手动关包。
- ✅ runtime loader 抽出后，base 与 DLC 共用一套加载链路（除 Boot 那份 AOT 副本）。
- ⚠️ **AOT 泛型约束不变**：DLC asmdef 必须主包构建期在场并 Generate——文档需在显著位置反复强调，这是最易踩的雷。
- ⚠️ 程序集不可卸载：DLC 反复进出靠对象 / Context 回收，不是卸 DLL。
- ⚠️ CodePackage 必须在资源 Profile 保持“不参与构建”；误启用会 fail-fast。未来加入业务 RawFile 配方时仍不得恢复资源 Module → 热更新 Profile 的反向依赖。

## 分期

- **P0（本 ADR）**：钉死方向与硬约束。
- **P1（最小可用）**：
  1. `FrameworkAssetBuilder` 支持业务 RawFile 包（独立小改，可先落，立即解决视频等内容的构建）；
  2. 抽 `IHotUpdateCodeLoader`（`Game.Framework.HotUpdate` 模块）；
  3. profile 支持「程序集 → DLC 组 / tag」映射；
  4. `FrameworkHotUpdateBuilder` 按组打 tag bundle + 子 manifest；
  5. 一个 demo DLC（含一段 RawFile 视频 + 一个 DLC 玩法 asmdef）端到端验证。
- **P2**：版本灰度 / 独立 code 包（独立 CDN / 第三方后发）/ 更细的就绪与回收。

## 关联

ADR-0008（热更机制基线）、ADR-0005（不热替换 / 撤就整棵撤）、ADR-0011（目录组织）、`docs/asset-system-flow.md`（包级下载策略）、`docs/framework-guide.md` §15。
