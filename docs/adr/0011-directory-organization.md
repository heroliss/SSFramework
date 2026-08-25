# ADR-0011：项目目录组织与第三方隔离

**Status:** Accepted

## Context

`Assets/` 根下混杂着第一方代码与各种插件/Unity 自动生成的目录（TextMesh Pro、UI Toolkit、Settings、Plugins、Screenshots……），文件容易乱。目标：`Assets/Game/` 放第一方内容（可复用 `Framework` + 本项目 Art/Prefabs/Scenes/业务），第三方/自动生成目录尽量隔离或移出。

## Decision

- **第一方内容**集中在 `Assets/Game/`：`Framework`（可复用框架）+ 项目资产 + 业务代码。
- **框架代码与项目配置实例分离**：`Framework/` 只放**可复用代码**（随 UPM 抽包，[0010](0010-framework-reusability-upm.md)）；框架那些 `ScriptableObject` 配置的**资产实例**是**项目数据**，放项目自己的配置位，**不在 `Framework/` 内**——否则抽包时项目配置会被错误打进包。Profile 都按**类型扫描**定位、不认路径；现有项目无需迁移，框架无法推导业务布局时，新建单例默认落 `Assets/Settings/SSFramework/`。当前仓库已有实例仍保留在 `Assets/Game/Settings/`；`Resources.Load` 绑定的 YooAssetSettings 则必须位于任意 `Resources/` 目录。
- **能转 UPM 的第三方优先转 UPM**，离开 `Assets`（R3、YooAsset 已是包；UniTask 计划转 `com.cysharp.unitask` UPM 包）。
- **Screenshots 移出 `Assets`** 到项目根 `Screenshots/`（已 gitignore）；MCP 截图用 `unity_screenshot_game` / `unity_screenshot_scene` 并把 `path` 指到项目根 `Screenshots/`，避免被导入为纹理 / 入库。
- **高风险/项目配置类目录暂留**：`TextMesh Pro`（与 TMP Settings 的 Resources 路径耦合）、URP `Settings` / `UI Toolkit`（被 ProjectSettings 按 GUID 引用）——搬动收益小风险高，留待需要时走 `AssetDatabase.MoveAsset` 保 GUID 并逐项 editor 验证。
- **资源搬迁铁律**：一律走 Unity `AssetDatabase`（保 `.meta`/GUID），禁止裸文件移动；每搬一项即验证引用未断，异常立即回退。

## Consequences

- ✅ 第一方与第三方边界清晰；版本控制不被临时截图/可重建产物污染。
- ✅ 项目配置（构建 / 热更 / UI 生成 / 收集器）与可复用框架源码分离；工具按类型发现，既兼容项目自定目录，也为新建资产提供中性的 `Assets/Settings/SSFramework/` 落点。
- ⚠️ 部分 Unity 强管理目录（TMP 等）暂时仍在 `Assets` 根，属已知妥协。
- 关联：[0010](0010-framework-reusability-upm.md)。
