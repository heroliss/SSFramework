# ADR-0013：YooAsset 原生 3.0 重写 —— 去兼容层

**Status:** Accepted（`YooAssetProvider` 已重写为原生 3.0 API，`YOOASSET_LEGACY_API` define 已移除，编译 0 错 0 警告，PlayMode 141/141 全绿）

## Context

[ADR-0012](0012-yooasset-3-migration.md) 把 YooAsset 升级到 3.0.2-beta 时，先用官方兼容层（`YOOASSET_LEGACY_API` scripting define）快速恢复编译，并预告原生重写补本 ADR。兼容层让 `YooAssetProvider` 用了 40+ 处 `[Obsolete]` 的 2.x 风格 API，带来持续的 CS0618 警告噪音，也用不上 3.0 的 FileSystem 新能力。本 ADR 记录把 provider 重写为原生 3.0 API 的决策与映射。

## Decision

**只改一个文件 `YooAssetProvider.cs`**（它是 `IAssetProvider` 隔离边界，框架核心与业务零改动），全面切到原生 3.0 API，并移除 `YOOASSET_LEGACY_API` define（Standalone/Android/iOS/WebGL 四平台）。

**不重新设计框架资源 API。** 评估后认为现有 `IAssetProvider` / `IAssetUtility` / `Bag.Load` 设计已足够好（隔离有效、API 符合直觉、R3/UniTask 集成干净），原生重写过程没有暴露出非改不可的设计缺陷——故只换内部实现，公共接口不动。

核心 API 映射（兼容层 → 原生 3.0），依据 YooAsset 3.0.2 包源码 + 官方 Space Shooter 示例（`FsmInitializePackage`）：

| 兼容层 | 原生 3.0 |
|---|---|
| `InitializeAsync(InitializeParameters)` | `InitializePackageAsync(EditorSimulateModeOptions / OfflinePlayModeOptions / HostPlayModeOptions / WebPlayModeOptions)` |
| `EditorSimulateModeHelper.SimulateBuild` | `EditorSimulateBuildInvoker.Build(pkg, (int)EBundleType.VirtualAssetBundle)` |
| `CreateDefaultCacheFileSystemParameters` | `CreateDefaultSandboxFileSystemParameters(remoteService)` |
| `IRemoteServices`（GetRemoteMainURL/Fallback） | `IRemoteService.GetRemoteUrls → IReadOnlyList<string>` |
| `IDecryptionServices`（5 方法） | `IBundleOffsetDecryptor.GetFileOffset → long` + `IBundleMemoryDecryptor.GetDecryptedData → byte[]`，经 `EFileSystemParameter.AssetBundleDecryptor / RawBundleDecryptor / AssetBundleFallbackDecryptor` 注入 |
| `UpdatePackageManifestAsync(version)` | `LoadPackageManifestAsync(new LoadPackageManifestOptions(version, timeout))` |
| `LoadRawFileAsync` + `GetRawFileText/Data` | `LoadAssetAsync<RawFileObject>` + `RawFileObject.GetText()/GetBytes()` |
| `CreateResourceDownloader(tags,c,r)` + `DownloadUpdateCallback` + `BeginDownload` | `CreateResourceDownloader(new ResourceDownloaderOptions(tags,c,r))` + `DownloadProgressChanged` 事件 + `StartDownload` |
| `Succeed` / `LastError` / `CheckLocationValid` / `IsNeedDownloadFromRemote` / `GetAssetInfoByGUID` / scene `UnSuspend` / `UnloadAsync` | `Succeeded` / `Error` / `IsLocationValid` / `GetDownloadSize()>0` / `GetAssetInfoByGuid` / `AllowSceneActivation` / `UnloadSceneAsync` |

## Consequences

- ✅ 编译 **0 错 0 警告**：兼容层时期的 40 条 CS0618 obsolete 警告全部消除。
- ✅ 移除 `YOOASSET_LEGACY_API` define 后重编译仍 0 错——**证明 provider 不再依赖兼容层**（兼容层是过渡件，未来 YooAsset 移除它也不影响本项目）。
- ✅ PlayMode **141/141** 全绿（含 `YooAssetLoadTests`，在 EditorSimulate 模式实跑重写后的初始化 + 加载路径）。
- ✅ `IAssetProvider` 隔离再次验证：原生重写整个收敛在一个文件，框架/业务零改动。
- ⚠️ **CI 仅验证 EditorSimulate 模式**。Offline / Host / Web 三模式 + 偏移解密（`IBundleOffsetDecryptor`）路径**仅编译验证**，真机/CDN 行为待实际出包时验证——此范围与兼容层时期一致（当时也只有 EditorSimulate 进 CI）。
- ⚠️ `SceneHandle` 在新版 Unity 与 `UnityEngine.SceneManagement.SceneHandle` 同名冲突，provider 内显式用 `YooAsset.SceneHandle` 限定。

## 开放决策（后续）

- **下载尺寸暴露**：3.0 `GetDownloadSize(location)` 给出字节数，比现有 `IsNeedDownload` bool 更适合"需下载 X MB"的下载提示 UX——已记入 roadmap，按需再加 `IAssetUtility` API（本轮未做，避免扩面）。
- **框架资源 API**（`AssetPlayMode` / `AssetProviderConfig`）本轮判断"够用不改"；将来若接入 3.0 的 ArchiveBundle 加解密、Web 文件系统细分等新特性，再评估扩展。
