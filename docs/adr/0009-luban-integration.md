# ADR-0009：Luban 配置表集成 —— 构建期生成 + 运行期经资源系统加载

**Status:** Proposed（设计；样例表待 schema 就绪）

## Context

项目需要配置表方案，已引入 Luban。根目录 `Luban/` 下是 Luban 的 **codegen CLI DLL**（`Luban.Core` / `Luban.CSharp` / `Luban.DataLoader.Builtin` 等，构建期工具，非运行时库）。需要确定：构建期如何生成、运行期如何加载、在框架里如何定位。

## Decision

### 1. 构建期（codegen）

- 用 Editor 菜单/脚本封装 Luban CLI（`dotnet Luban.dll ...`，读 Excel/配置定义 → 生成配置 C# 类 + 数据文件）。
- **工具 DLL 移出仓库根目录**：候选 `Tools/Luban/` 或 Assets 外的非导入目录（避免 Unity 导入这些非运行时 DLL）。**最终位置后定**（用户："后面再移动"）。
- 生成产物：配置 C# 代码 + 数据文件（推荐 **binary** 格式，紧凑、解析快）。

### 2. 运行期加载

- 生成的 `Tables` 类构造接收一个字节加载委托 `Func<string, ByteBuf>`（或等价）。把它接到 **`IAssetUtility.LoadBytes`**——配置数据打进 YooAsset 包，复用框架统一资源通道，不另起加载方式。

### 3. 框架定位（镜像资源系统三段式）

仿照资源系统的 `AssetSettingsModel` / `AssetInitSystem` 拆分：

| 角色 | 层 | 职责 |
|---|---|---|
| `ConfigModel`（持有只读 `Tables`） | Model | 加载完成后持有 Luban `Tables` 实例，对外只读暴露 |
| `ConfigInitSystem` | System | 进入游戏时编排：经 `IAssetUtility.LoadBytes` 读数据文件、构造 `Tables`、写入 `ConfigModel`、暴露就绪状态 |

业务经 `this.GetModel<ConfigModel>()`（System/Command）或查询 Command 取配置；配置是只读数据，落 Model 最自然。生成代码归属哪个 asmdef 见"开放决策"。

## Consequences

- ✅ 配置加载复用 `IAssetUtility`，与资源系统同一套初始化/多包/CDN 机制；配置可热更（数据文件随资源更新）。
- ✅ 三段式与资源系统一致，心智统一、可 Inspector 观察。
- ⚠️ 生成代码量可能大；需纳入程序集划分（与热更分界 [0008](0008-hybridclr-integration.md) 联动）。
- ⚠️ 本 ADR 先定方案 + 加载接线脚手架；样例表/codegen 脚本待 schema 就绪后补。

## 开放决策（落地时定）

- 输出格式（推荐 binary；若需可读性/调试用 json）。
- 生成代码归属 asmdef：独立 `Game.Config` 程序集，还是并入热更程序集（[0008](0008-hybridclr-integration.md)）——若配置要热更则应在热更侧。
- Luban 工具 DLL 最终位置（`Tools/Luban/` vs Assets 外）。
- Luban runtime（ByteBuf 等）归属 AOT 还是热更（建议 AOT，作稳定基础设施）。
