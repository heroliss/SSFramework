# ADR-0012：YooAsset 3.0 迁移 —— 先用官方兼容层

**Status:** Superseded by [ADR-0013](0013-yooasset-native-rewrite.md)（兼容层是过渡方案；原生 3.0 重写已完成、`YOOASSET_LEGACY_API` define 已移除。本 ADR 保留为升级当时的历史记录）

## Context

项目把 YooAsset 从 2.3.18 升级到 **3.0.2-beta**。3.0 是大版本重写：初始化改为 **FileSystem 化**（`FileSystemParameters` + `AddParameter(EFileSystemParameter.AssetBundleDecryptor, ...)`），`IDecryptionServices`→`IBundleDecryptor` 派生族、`IRemoteServices`→`IRemoteService`、`Buildin`→`Builtin`、`Cache`→`Sandbox`、`RawFileHandle`→`BundleFileHandle`、`AssetInfo.IsInvalid`→`IsValid`、`AsyncOperationBase` 改为 `IEnumerator` 且不再有 2.x 的 `.Task`、`YooAssets.SetDefaultPackage` 移除。

升级后编译错误**全部、且仅**集中在 `YooAssetProvider.cs` 一个适配文件——验证了"YooAsset 藏在 `IAssetProvider` 后面"的隔离设计：底层库大版本破坏只波及适配层，框架核心/业务零影响。

## Decision

**先用 YooAsset 3.0 自带的官方兼容层**（`Runtime/Compatibility/`，由 scripting define `YOOASSET_LEGACY_API` 门控）恢复 2.x 风格 API，快速恢复绿色构建；原生 3.0 FileSystem 重设计作为后续（待官方 init 示例/文档到位再做稳）。

落地：
1. 启用 `YOOASSET_LEGACY_API`（已对 Standalone/Android/iOS/WebGL 四个构建目标组设置；编辑器按活动目标组编译）。兼容层在 `namespace YooAsset` 内恢复 `InitializeParameters` 类族、`IRemoteServices`/`IDecryptionServices`/`DecryptFileInfo`/`DecryptResult`、旧工厂方法、`ResourcePackage.InitializeAsync` 等。
2. 兼容层只覆盖到 Handle 类，未覆盖 Operation 类，故补 4 处残留修复：
   - `RawFileHandle` → `BundleFileHandle`（3.0 raw 加载返回类型）。
   - 移除 `YooAssets.SetDefaultPackage`（provider 只用 package 实例、本就不需要）。
   - `assetInfo.IsInvalid` → `!assetInfo.IsValid`。
   - Operation 的 `.Task.AsUniTask().AttachExternalCancellation(ct)` → `UniTask.WaitUntil(() => op.IsDone, cancellationToken: ct)`（3.0 operation 是 IEnumerator、由 YooAsset 内部 PlayerLoop 驱动，轮询 IsDone 桥接；取消只中断等待，与 2.x 行为一致）。

## Consequences

- ✅ 编译绿、PlayMode 124/124 测试全过（含 YooAsset 加载测试）。
- ⚠️ 依赖 `YOOASSET_LEGACY_API` scripting define——**新增构建目标平台时需补设此 define**，否则该平台编译断裂。
- ⚠️ 兼容层是**过渡件**，未来 YooAsset 可能移除；且 3.0 的 FileSystem 新特性（Web 文件系统、ArchiveBundle 加解密等）走兼容层用不上。
- 🔮 后续：拿到 3.0 官方 init 示例后，把 `YooAssetProvider` 重写为原生 3.0 FileSystem API（去掉 `YOOASSET_LEGACY_API` 依赖），并评估是否借机优化框架资源 API（`AssetPlayMode`/`AssetProviderConfig`）。届时补 ADR-0013。
- 关联：`IAssetProvider` 隔离设计见代码注释；资源系统使用不变量见 [`Assets/Game/AGENTS.md`「模块使用不变量」](../../Assets/Game/AGENTS.md#模块使用不变量)。
