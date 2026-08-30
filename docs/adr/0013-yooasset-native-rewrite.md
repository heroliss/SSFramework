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

- **下载尺寸暴露**：3.0 `GetDownloadSize(location)` 给出字节数；当前 `GetLocationState` 只回答分类状态，仍不能支持“需下载 X MB”的下载提示 UX——已记入 roadmap，真实交互需要时再加尺寸 API。
- **框架资源 API**（`AssetPlayMode` / `AssetProviderConfig`）本轮判断"够用不改"；将来若接入 3.0 的 ArchiveBundle 加解密、Web 文件系统细分等新特性，再评估扩展。

## 2026-07 修订（Outpost M5 构建收口驱动，详见 ADR-0029）

- **运行模式拆成「编辑器 / 玩家包」两字段**：原 `AssetSystemConfigModel` 单一 `_playMode` 全局通吃，但 `EditorSimulate` 分支在 provider 里是 `#if UNITY_EDITOR` 编译的——场景配模拟模式进玩家包直接 `NotSupportedException`，且这个错误配置在编辑器 Play 完全无症状。新增 `_playerPlayMode`（默认 Offline），`ActualPlayMode` 按端选字段；`GetConfigError` 校验玩家包模式不得选 EditorSimulate（fail-fast 于启动校验而非 provider 初始化）。同一份场景配置由此两头通用：编辑器日常模拟、玩家包 Offline/Host。
- **`AssetUtility.Configure` 提升 public**：热更引导下 Boot 场景只能挂 AOT 组件，当时的场景资源三组件没法先于首场景存在——首场景加载前的资源初始化必须有代码化路径。入口（GameEntry）用 `MonoGameContextBase + AssetUtility` 双 AddComponent 搭最小引导栈：`Configure → Initialize → LoadScene → Destroy` 交棒；provider 对已初始化的包按名复用（Dispose 不销毁包）正是为这类「多 utility 实例并存」预留的语义，本轮首次被真实消费。场景路径后来由 ADR-0046 收敛为同一个 `AssetUtility` 单入口，代码引导契约不变。

## 2026-08 修订（取消等待与物理 operation 所有权）

YooAsset 3.0 的 `AsyncOperationBase` 没有通用外部取消；框架的 `WaitOp(..., ct)` 取消的是 UniTask 轮询等待，不会停止已经启动的底层 operation。若把短命 UI token 直接当物理初始化 / 清缓存 token，调用者取消后上层会过早释放“同包可重试”入口，而原 operation 仍在跑：初始化重试会命中 `Resource package is already initialized`，两个缓存维护 operation 也可能同时修改同包文件记录。

因此资源异步所有权拆成两个作用域：

- 初始化由 utility 生命周期建立唯一 per-package owner；各调用者只等待共享 TCS。调用者取消不改变 `InitState`，owner 最终统一落到 `Ready` / `Failed`。
- 同包三种 `ClearCache*` 与 `UnloadUnusedAssets` 共用 FIFO 维护 lane。条目开始后只受 utility owner token 控制；调用者取消只脱离等待，lane 要等物理调用真正返回才放行下一项。排队期间已取消的条目不启动。
- `YooAssetProvider` 之上还需要按实际 `ResourcePackage` 共享的进程级协调器：多个 Utility/Provider 会复用 YooAssets 全局注册表里的同一个包，只在 Utility 内串行看不到跨实例操作，也看不到按需 Load / 显式 Download。Adapter 用公平 Reader/Writer 队列让加载与下载并行、让初始化 / 清缓存 / 内存维护独占，并阻止新 Reader 越过已排队 Writer。
- `IAssetProvider` 仍是原有 Seam，不新增公共协调 Interface；跨实例协调是 YooAsset 这个全局后端的 Implementation 细节，其他真正实例隔离的后端无需照搬。

两层取消都遵循同一边界：最后一个 waiter 在获 lease 前离开，可以撤销排队而不产生副作用；lease 授予后 token 只让该 waiter detach，原生 operation 继续到真实终态。无人接收的 Asset / Scene 成功 handle 会被释放，后台失败会进入统一日志。清缓存终态还会推进 package 缓存世代（失败也可能留下部分变化），创建于旧世代的 downloader 在启动前 fail-fast，要求重建；同一 downloader 的多个调用共享一个物理 owner，底层 operation 只启动一次。

同步 downloader 工厂和公开 `GetLocationState` 内部使用的下载缓存查询不能 await Writer，也不能越过 Writer：协调器提供短同步 Reader admission，原子执行“读缓存世代 + 建快照”；Writer 活跃或已排队时 fail-fast，调用方在维护完成后重试。已完成 downloader 的每次后续 `Download()` 也会重新经过 Reader admission 再校验世代，不能用黏住的旧终态绕过刚完成的 Clear。

挂起场景另有一个第三方状态机边界：`allowSceneActivation=false` 时 Unity 会停在 0.9，`IsDone` 只有业务拿到 handle 并 `UnSuspend` 后才可能成立。Adapter 把“内容到达激活门”定义为这类 Reader 的可交接终态并返回 handle，避免 owner 与调用方互等。

协调器用 `ConditionalWeakTable<ResourcePackage, ...>` 建立身份映射：同一原生包跨 Provider 命中同一状态机，但协调器不会反向强持有已经从 YooAssets 注册表移除的包。纯协调器 EditMode 契约覆盖 Reader 并行 / Writer 公平与独占、排队取消、运行后 detach、失败终态、共享 owner、同步快照 admission、弃置结果和后台日志；EditorSimulate 集成测试覆盖已完成 downloader 经 Clear 后拒绝、重建成功，以及挂起场景到激活门后的恢复与完整卸载。

## 2026-08-24 修订（Demo 驱动的资源地址四态快照）

原决策“不重新设计框架资源 API”只描述 3.0 原生迁移当时的证据，并不禁止真实调用方后来暴露 Interface 缺陷。资源加载 Demo 需要先守卫 `IsInitialized`，再组合 `CheckLocationValid` 与 `IsNeedDownload`；两者在包未 Ready 时都返回 false，而 `IsNeedDownload=false` 又同时包含“地址无效”和“已在本地”。调用方即使记住第一层守卫，仍可能把第二层 false 解释错，多包时也容易误守卫默认包状态。

因此稳定 Interface 改为一次 `GetLocationState(package, location)`，返回四种互斥状态：

- `PackageNotReady`：非空地址当前不能查询；具体 Idle / Pending / Initializing / Failed 继续由 `GetInitState(package)` 表达。
- `Invalid`：空白地址，或 Ready 包的 manifest 中不存在。
- `AvailableLocally`：地址有效，内容已内置或已缓存。
- `RequiresDownload`：地址有效，内容需要远端下载。

没有采用“三态”，因为只拆出 NotReady 仍会让“Invalid”和“AvailableLocally”共用一个 false；也没有让未 Ready 直接抛异常，因为预检常用于不阻塞地驱动 UI，而初始化精确错误已有独立状态流。Core 的 `AssetUtility` 持有包生命周期真源：非 Ready 时不调用 Provider；Ready 后先验证地址，再读取下载缓存。两步之间若有维护 Writer 开始或排队，Yoo Adapter 继续 fail-fast，拒绝跨缓存世代拼出伪快照。

`IAssetProvider` 不扩张：它的两个 bool 是 Adapter Implementation 细节，其他后端只需实现原有 Seam。旧 `CheckLocationValid` / `IsNeedDownload` 从 `IAssetUtility` 移出，仅以 `[Obsolete]` 扩展方法保留源码迁移路径并精确保留旧 false 语义；新业务与 Demo 只使用四态 Interface。

## 2026-08-26 修订（资源失败证据进入日志 Seam）

资源 Module 的失败信息按产生位置保留来源，而不是全部压成一类“资源加载失败”：

- Core 的空 location / GUID 等调用错误在 `AssetUtility` 或 `AssetReference` 边界 fail-fast，不触发包初始化，也不下沉 Yoo Adapter；`AssetUtility` 日志携带自身 Unity context，便于 Console 定位并让文件/遥测 sink 保留对象来源。
- YooAsset manifest 找不到地址、原生 handle 失败或类型不匹配时，由 `YooAssetProvider` 以独立 category 记录；默认 Console 文案仍显示原有 `[YooAssetProvider]` 前缀，但分类不再埋在 message 里。
- 包初始化失败仍由 `AssetUtility` owner 统一把状态落到 `Failed`，同时把原始 exception、运行模式和修复提示交给日志 Seam；调用者随后通过 `EnsureInitialized` 收到的仍是同一个根异常对象。
- 不重新包装 YooAsset 自己产生的内部日志；需要全量落盘时由 `Log.CaptureUnityLogs()` 接管 Unity 日志流，避免 Adapter 重复输出同一第三方错误。

这次没有扩张 `IAssetProvider` / `IAssetUtility` Interface。日志是横切 Seam，资源 Core 与 Yoo Implementation 只依赖内核 `Game.Framework.Logging`；测试分别锁定 Core fail-fast、Adapter category，以及初始化异常/context 的透传。
