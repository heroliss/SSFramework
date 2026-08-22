# YooAsset 集成踩坑与「何时可移除规避代码」

记录框架集成 YooAsset 时遇到的库级坑、我们的规避代码、以及**库更新后这些规避是否还需要**。
碰到下面任一现象、或升级了下方版本，请回来核对——有些规避代码在库修了之后就是冗余、应删掉。

## 适用版本（升级后请重新评估本文）
- Unity **6000.3.22f1**
- YooAsset **3.0.5**
- Scriptable Build Pipeline **2.6.1**（`com.unity.scriptablebuildpipeline@36e3b5898ee2`）

2026-08-22 已重新核对并实构建四个启用包：YooAsset 3.0.5 没有修复下述 SBP 内置 shader 空布局问题，
逐包空包容错与每包独立下载根目录也仍符合当前 API 语义，因此本文所有规避继续保留。

---

## 坑 1（可移除）：SBP 内置 shader 包 obsolete 任务，遇到「无内置 shader 的包」直接崩

**现象**：构建某个包报
`CreateBuiltInShadersBundle failed ... IBundleExplictObjectLayout was not available`。
尤其用 YooAsset 自带 **Bundle Builder 窗口**构建纯 Sprite / 纯数据的包时必崩。

**根因**：
- YooAsset 的 SBP 任务链（`SBPBuildTasks.Create`）在「内置 shader 包名非空」时加入 SBP 的 `CreateBuiltInShadersBundle` 任务。
- 该任务在当前 SBP 版本已 `[Obsolete]`，内部转调 `CreateBuiltInBundle`；当包里**没有任何引用 Unity 内置资源（`unity_builtin_extra`，含内置 shader）的资产**时，`CreateBuiltInBundle` 收集到 0 个对象、把 layout 置 null，而 obsolete 包装任务不判 null、硬取 `IBundleExplictObjectLayout` → 抛异常。
- **窗口必崩、我们的构建器不崩**的原因：窗口总把 `BuiltinShadersBundleName` 设非空；我们的构建器按包决定是否设（见规避）。

**我们的规避代码**：
- `FrameworkAssetBuildProfile.PackageBuildEntry.GenerateBuiltinShaderBundle`：按包开关（零内置 shader 的包配成关）。
- `FrameworkAssetBuilder.ResolveBuiltinShaderBundleName`：关时返回空串，跳过该任务。
- `FrameworkAssetBuilder.Build`：构建失败且该包开了 shader 包时，在失败信息里提示「可能没内置 shader，去配置关掉开关」（**不自动重试**——失败原因多样，无脑重建只是浪费）。

**何时可移除**：YooAsset 把 `SBPBuildTasks` 改用非 obsolete 的 `CreateBuiltInBundle`（或 Unity 修了 obsolete 包装的空 layout 判断）后——届时所有包都能安全开内置 shader 包，可：默认开、删掉 per-package 开关与失败提示、`ResolveBuiltinShaderBundleName` 直接返回包名。

---

## 坑 2（保留，非 bug）：空包构建中断整批

**现象**：构建一个「收集到 0 资源」的包，YooAsset `TaskGetBuildMap` 抛
`[ErrorCode...] Pack asset list is empty.`（`ErrorCode.PackAssetListIsEmpty`）。

**说明**：这是 YooAsset 的**预期行为**（空包无意义），不是 bug。但默认会让整批多包构建中断。

**我们的处理（UX，非规避 bug）**：`FrameworkAssetBuilder` 逐包容错——
`IsCollectorEmpty` 预检（收集器无 collector 直接跳过、不尝试构建）+ `IsEmptyAssetError` 兜底（匹配该错误串 → 跳过该包、继续其余）。

**何时可移除**：不需要移除（与库版本无关）。仅 `IsEmptyAssetError` 依赖 YooAsset 的英文错误串 `"Pack asset list is empty"`，库改文案时需同步。

---

## 注 3（我们的选择，非 bug）：编辑器下载缓存重定向

YooAsset 编辑器期默认把下载缓存放 `项目根/yoo`（`YooAssetConfiguration.GetEditorCacheRoot`，注释原话「方便调试查看」）。我们用
`FileSystemParameters.CreateDefaultSandboxFileSystemParameters(remote, packageRoot)` 重定向到 `AssetBuild/Downloaded/<包>`（见 `YooAssetProvider.CreateInitOptions` Host 分支，`#if UNITY_EDITOR`）。

**易错点**：`packageRoot` 是**每包根目录**，YooAsset **不自动追加包名**（`SandboxFileSystem.OnCreate`）——必须自己拼 `/<包>`，否则多包撞同一目录。

**后端可移植性**：这是 **YooAsset 特有能力**（Addressables 等用 Unity 缓存 / persistentDataPath、不支持任意重定向）。真机上各后端一律 persistentDataPath。换后端时由各 provider 自行决定缓存落点，不是框架统一契约。详见 `AssetBuildLayout.DownloadedDir` 注释。

---

## 注 4：构建只走我们的构建器，YooAsset 自带窗口仅供查看

因坑 1，YooAsset 自带 Bundle Builder 窗口在本工程对零 shader 的包会崩；且窗口不读我们的构建配置（profile）、输出路径也写死。**正式构建一律走 `SSFramework/资源构建` 菜单 / CI（`FrameworkAssetBuilder`）**，窗口只当查看/调试工具。

---

## 坑 5（已规避）：地址规则生效的前提是包级 `EnableAddressable = true`

`AddressByFileName` 等地址规则只是**生成** address 字符串写进清单；运行时 location 字典**收不收录**这些短地址由 `BundleCollectorPackage.EnableAddressable` 决定（默认 **false**）。关闭时字典只有完整 AssetPath（`Assets/.../xxx.bytes`），用短地址 `LoadAssetAsync("hotupdate_manifest")` 直接报 **`Location is invalid`**——清单里明明看得到 address，加载就是找不到，非常迷惑。

**规避**：代码创建收集器配置时必须显式 `pkg.EnableAddressable = true`（见 `FrameworkHotUpdateBuilder.EnsureCollector`，对已存在的包也强制刷一遍）。手工在 Collector 窗口建包同理，勾选 Enable Addressable。

**升级注意**：这是 YooAsset 设计语义（地址规则与寻址开关分离），不是 bug，预计长期如此。

---

## 坑 6（已规避）：Host 全新安装时，只有 BuiltinCatalog 不等于已有 ActiveManifest

**现象**：包明明把版本清单和全部 bundle 放进了 `StreamingAssets`，断网启动仍在拉远端 `.version` 失败后报
“本地无可用清单”；或者清单回退成功，但加载首场景时仍访问 CDN。

**根因**：`BuiltinCatalog` 只告诉 Builtin FileSystem“随包有哪些文件”，不会自动把版本清单激活成 `ResourcePackage.PackageValid`。
Host 的主文件系统又是 Sandbox；全新安装没有缓存过的 ActiveManifest，不能把“内置文件存在”误当成“包已有有效清单”。后一种现象则是首包配置与实际拷贝产物不一致。

**我们的处理**：

- Host 初始化前先用同一套跨平台读取探测内置 `<包>.version`；存在时才给 Builtin FileSystem 打开 `CopyBuiltinPackageManifest`。编辑器自定义缓存根同时显式指定 `CopyBuiltinPackageManifestDestRoot`，确保复制目标与主 Sandbox 的 `ManifestFiles` 一致；不存在则按纯 CDN 包正常初始化，不让内置文件系统抢先报错。
- 远端版本 / 清单失败且当前包尚无有效清单时，用 `UnityWebRequest` 读取内置 `<包>.version`（兼容 Android `jar:file`），再调用 `LoadPackageManifestAsync` 显式激活；已有清单则继续用当前版本。
- `FrameworkAssetBuilder.ValidateBundledOutput` 在构建成功后检查内置清单；`ClearAndCopyAll` 会逐个核对输出 bundle 是否真的进入 `StreamingAssets`；按 tag 则读取**本次** `.report` 计算应复制集合并逐个核对，因此零命中和 `OnlyCopyByTags` 目录里的旧 bundle 残留都不能伪装成本次成功。

**边界**：回退不把远端内容凭空变成本地内容。零内置 / 按 tag 未命中的 bundle 仍须 CDN；真正要求断网可启动的代码包、首场景包必须配置为全部内置或覆盖完整的启动资源 tag。
