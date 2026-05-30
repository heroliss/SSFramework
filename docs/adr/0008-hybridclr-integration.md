# ADR-0008：HybridCLR 热更集成 —— AOT/热更程序集分界

**Status:** Proposed（设计；端到端落地依赖热更构建管线）

## Context

项目需要 C# 热更新能力，已安装 HybridCLR（UPM 包 `com.code-philosophy.hybridclr`）。HybridCLR 通过 IL2CPP 差分解释执行，让"热更程序集"的 C# 代码在发版后可更新，而"AOT 程序集"随包固化。需要确定：哪些代码属 AOT、哪些属热更，以及启动时如何加载热更程序集。

## Decision

### 1. 程序集分界

- **AOT / 稳定层**：`Game.Framework`、`Game.Framework.Editor` 及全部第三方库（R3 / UniTask / YooAsset / Odin / Luban runtime 等）。框架是长期稳定的基础设施，随包固化，**不进热更**。
- **热更层**：业务代码（Model / System / Command / View 的具体实现、配置表生成代码）放入独立的**热更 asmdef**（如 `Game.HotUpdate`）。

Phase A 的 asmdef 边界清理（[0004](0004-assembly-structure-and-rp-location.md)）正是此分界的前置：框架已是干净的独立程序集，业务用自己的 asmdef 引用框架即可被标记为热更。

### 2. 引导流程（在 `AppStartScene` / `MonoGlobalContext` 之前）

1. 初始化资源系统（YooAsset，可能需要先更新热更资源）。
2. 经 `IAssetUtility.LoadBytes` 拉取 **AOT 补充元数据 DLL**（`HybridCLR` 的 AOT generic 补元数据）与**热更程序集 DLL**（打包为资源）。
3. `RuntimeApi.LoadMetadataForAOTAssembly(aotDllBytes, HomologousImageMode.SuperSet)` 逐个补元数据。
4. `Assembly.Load(hotUpdateDllBytes)` 加载热更程序集。
5. 反射调用热更入口（约定一个入口类型/方法，如 `GameLauncher.Entry()`），由它创建 `MonoGlobalContext` / 启动游戏。

框架提供一个 `HotUpdateLauncher`（MonoBehaviour）+ 文档化的引导样板；纯 AOT（编辑器/未启用热更）下走直连入口，热更下走上述流程，用一个 define（如 `ENABLE_HYBRIDCLR`）或运行时判断切换。

### 3. 反射兼容

框架的 [InjectionPlan](../../Assets/Game/Framework/Scripts/Internal/InjectionPlan.cs) / [LayerInterfacesCache](../../Assets/Game/Framework/Scripts/Internal/LayerInterfacesCache.cs) / `GameContext.FindContextField` 对热更类型有效（它们都是真实 `System.Type`）。需在文档列出框架反射实例化/泛型用到的**泛型形状**（`RP<T>`、`Subject<T>`、`Dictionary<Type,object>` 等），配合 HybridCLR 的 `AOTGenericReferences` 扫描 + `link.xml` 防裁剪，确保 AOT 侧已实例化所需泛型。

## Consequences

- ✅ 框架与第三方固化在 AOT，业务热更——职责清晰，框架升级走发版、业务迭代走热更。
- ✅ 复用 `IAssetUtility` 加载热更 DLL，不引入第二套下载通道。
- ⚠️ 端到端需要热更构建管线（HybridCLR 的 Generate/Compile + 资源打包），本 ADR 先定方案与引导脚手架，完整验证待管线就绪。
- ⚠️ AOT 泛型补元数据是 HybridCLR 常见坑：框架侧的泛型用法需纳入 AOT 扫描清单。

## 开放决策（落地时定）

- 业务热更 asmdef 的划分粒度（单一 `Game.HotUpdate` vs 多个）。
- Demo 是否参与热更（建议否，Demo 走 AOT 直连，简化）。
- 热更入口约定（类型名 / 方法签名）。
- 是否需要"边玩边下"与版本灰度。
