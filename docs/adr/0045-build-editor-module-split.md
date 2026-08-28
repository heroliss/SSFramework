# ADR-0045：资源构建与 HybridCLR 热更新构建按依赖拆分

- **状态（Status）**：已采纳（Accepted）
- **日期（Date）**：2026-08-28

## 背景

原 `Game.Framework.Build.Editor` 同时引用 YooAsset、YooAsset.Editor、`Game.Framework.Boot`、HybridCLR.Editor 与 dnlib。只需要普通资源构建的项目，即使已经删除 Boot 和热更新功能，仍必须安装整套 HybridCLR Editor 工具链；删除第三方依赖会让资源构建程序集直接失去编译条件。

依赖方向还存在更隐蔽的问题：`FrameworkAssetBuilder` 与资源 Profile Inspector 会反向读取 `FrameworkHotUpdateProfile`，按代码包名称提供特殊跳过。这个便利把可选的热更新 Implementation 变成资源构建 Interface 的前置知识，删除测试失败，也使通用资源工具出现项目配置不存在时的隐式默认名称。

资源构建与代码热更新确实共享版本格式、部署目录、构建前预检和产物路径安全，但这些能力已经由资源构建 Module 的深 Implementation 提供。当前没有第三个独立消费者，额外抽一个“Build Common”程序集只会制造浅 Module。

## 决策

1. 保留 `Game.Framework.Build.Editor` 的程序集名与既有 Profile/CI 入口，让它只拥有 YooAsset 普通 AssetBundle 的 Profile、SBP 构建、部署、本地 CDN、包名生成、加密接入和产物路径校验。
2. 新增可删除的 `Game.Framework.Build.HybridCLR.Editor`。它单向引用资源构建 Module、Boot、HybridCLR.Editor、YooAsset/YooAsset.Editor 与 dnlib，拥有热更 Profile、程序集图、Generate 新鲜度、目标 DLL 编译、RawFile 代码包和热更工作台。
3. 热更新测试迁入 `Game.Framework.Build.HybridCLR.Editor.Tests`；资源构建测试不再引用 Boot、HybridCLR、YooAsset.Editor 或 dnlib。两侧分别锁定自己的工具/配置注册和程序集引用方向。
4. 资源构建器不再读取 `FrameworkHotUpdateProfile` 或按 `CodePackageName` 猜特殊包。资源 Profile 的“参与构建”是唯一选择源；使用 `PackRawFile` 的包若被启用或由 CLI 点名，会在写入构建产物前明确失败，并要求关闭资源构建后改走拥有对应 RawFile 配方的 Module。
5. 当前项目的 CodePackage 条目继续显式设为“不参与资源构建”。热更新 Module 仍复用资源 Profile 的版本格式、`FrameworkAssetBuilder.Deploy`、构建预检与 `FrameworkBuildArtifactPath`，不复制第二份路径和部署逻辑。
6. 通用 Module Audit 继续用反射查找 `FrameworkHotUpdateProfile` / `FrameworkHotUpdateBuilder`，但证据字段和文案明确称为“HybridCLR 热更新构建 Module”；资源构建是否安装与热更派生证据正交。
7. 保留移动脚本的 `.meta` GUID，既有 ScriptableObject Profile 通过 MonoScript GUID 继续加载；契约测试扫描并加载当前工程内已有 Profile，防止程序集迁移造成静默丢失。

### 迁移说明

既有 `FrameworkHotUpdateProfile` 资产不需要重建，移动脚本保留的 MonoScript GUID 会让 Unity 在域重载后把它解析到新程序集。源码消费者若自己的 asmdef 直接使用 `FrameworkHotUpdateProfile`、`FrameworkHotUpdateBuilder`、`HotUpdateBuildMenu` 或 `HotUpdateBuildWindow`，需要把程序集引用从 `Game.Framework.Build.Editor` 改为 `Game.Framework.Build.HybridCLR.Editor`；引用旧程序集的预编译 Editor 扩展也需要重新编译。只使用普通资源构建 API 的消费者无需迁移。

## 影响与取舍

### 收益

- 只需要 YooAsset 普通资源构建的项目可以删除 `Build/HybridCLR`、Boot 与 HybridCLR/dnlib 依赖，不修改资源构建源码。
- 第三方变化集中在实际拥有它的 Module，资源构建 Interface 更小，删除测试和依赖审计更可信。
- 热更新仍复用成熟的版本、部署和路径安全 Implementation，保持杠杆（Leverage）与局部性（Locality），没有复制浅工具层。
- 工具中心与配置中心的卡片继续由各 Module 自注册；删除热更新 Module 后相关入口会随域重载自然消失。

### 代价与边界

- 热更新构建仍以 YooAsset RawFile CodePackage 为发布载体，因此它合理地依赖资源构建 Module；本决策不是后端中立的热更新发布抽象。
- 用户必须在资源 Profile 显式关闭由其它配方拥有的 RawFile 包。相比按名称静默跳过，这多一项配置，但误配会 fail-fast，不会产出残缺代码包。
- 未来若业务 RawFile 视频等出现真实消费者，应按 ADR-0017 为资源构建新增通用 RawFile 配方；代码包的 CompileDll/manifest/AOT 步骤仍由热更新 Module 独占。
- 这次只建立 asmdef 级物理删除边界；正式 UPM 安装、添加/移除 Package 和干净消费工程仍由后续发布矩阵证明。

## 验证

- 资源构建程序集元数据不得引用 `Game.Framework.Boot`、`Game.Framework.Build.HybridCLR.Editor`、HybridCLR.Editor 或 dnlib。
- 热更新构建程序集必须显式引用资源构建、Boot、HybridCLR.Editor 与 dnlib。
- 两个可选 Editor Module 均保持 `autoReferenced:false` 与 `overrideReferences:true`，测试 Module 跟随各自 owner 删除。
- 已有 `FrameworkHotUpdateProfile` 资产能在拆分和域重载后加载；Demo CodeRef 指向迁移后的真实源码。
- Unity 编译、相关 EditMode、完整 EditMode / PlayMode 与 Module Audit 契约通过。
