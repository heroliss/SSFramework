# ADR-0018：资源加密 —— 偏移内置为默认 + 代码接入位承载自定义，不内置 AES

**Status:** Accepted（偏移加密 + 接入位已实现；2026-08-31 修订首场景配置派生、现实上限与 Web 内存解密）

## Context

资源后端（YooAsset 3.0）支持对 bundle / 清单加密，分**构建侧**（写加密产物）与**运行时侧**（读时解密）两端，必须成对、参数一致。需要决定：框架内置哪种加密、自定义加密怎么接、加密配置放哪、要不要内置 AES、以及与 YooAsset 自带构建窗口的关系。

约束基线（决定后续所有取舍）：

- **客户端加密是「抬高门槛」不是「真安全」**：解密密钥必须随客户端下发（设备要能解密才能用资源），足够执着的人总能从内存 / 反编译拿到密钥。选型看「拦住目标人群下代价多小」，不是「哪个最安全」。
- **运行时三种解密形态决定三类代价**（见 `LoadLocalAssetBundleOperation`）：
  - 偏移 `IBundleOffsetDecryptor` → `AssetBundle.LoadFromFile(path, 0, offset)`，内存映射直接加载，代价≈0；
  - 内存整包 `IBundleMemoryDecryptor` → `LoadFromMemory`，CPU + 双倍内存峰值；
  - 流式 `IBundleStreamDecryptor` → `LoadFromStream`（要求**可 Seek** 的流），边读边解、内存友好。
- **运行时密钥不能进构建 profile**：profile 是会发布的资产、且运行时根本不加载它；密钥进去既泄露又用不上。
- **YooAsset 接触面收口在 provider**（ADR-0013）：加密器 / 解密器是 YooAsset 类型，属于「扩展资源后端」的合法边界。
- 框架计划抽成 UPM 包（ADR-0010）：扩展点要让项目「不改框架源码」即可接入。

## Decision

### 1. 偏移加密内置为默认；它也是绝大多数游戏的正确选择

`GameBundleOffsetEncryptor`（构建）/ `GameBundleOffsetDecryptor`（运行时）内置开箱即用。理由：

- **代价为零**：`LoadFromFile` 原生带 offset，不解密正文、不额外占内存，加载耗时与不加密几乎一致；其余方案都有实打实代价。
- **正好打中真实威胁**：最常见的扒取就是把 .bundle 拖进 AssetStudio / UnityEX，它们靠 `UnityFS` 魔数识别 AB；头部插 N 字节让魔数失配即挡掉绝大多数随手扒取。
- **不引入密钥管理**，也不产生「实现得差的密钥」带来的虚假安全感。
- 代价：知道偏移量或扫魔数即可秒剥——防的是顺手扒，不是专业逆向。

插入的偏移字节**确定性派生自 bundle 名**（FNV-1a）：因 YooAsset 对**加密后**文件算 hash 写清单（`TaskUpdateBundleInfo`：`PackageSourceFilePath = EncryptedFilePath`），随机字节会让内容未变的包每次 hash 都变 → CDN 误判全量重传，破坏增量发布。

### 2. 场景运行值手动对齐，首场景代码值从构建 Profile 派生

偏移之所以能做成普通字段，正因为它是**非密钥、两端相同**的数字。`FrameworkAssetBuildProfile.FileOffset` 与场景 `AssetUtility.Settings.FileOffset` 仍须人工一致；但首场景在场景内 Utility 出现前已经要读 bundle，不能依赖场景字段，也不能让 `GameEntry` 再手写第三份数字。

因此 `AssetPackageConstantsGenerator` 除包名外，从构建 Profile 派生 `AssetBundleFileOffset`；代码引导的 `Configure` DTO 使用该生成常量。普通 AssetBundle 构建前以同一渲染函数逐字校验生成物，陈旧时拒绝写产物，不在构建中自动改 `.cs`（否则 Domain Reload 前的旧程序集仍会继续执行）。该常量只描述普通 AssetBundle 格式，不作用于独立 RawFile / CodePackage。修改偏移后必须把“生成 + 编译 Game.Main / Player + 构建资源 + 部署”视为同一发布事务；`const` 内联意味着源码新鲜不等于已部署 DLL 新鲜。

内置偏移实现共享 1 MiB 现实上限：偏移只破坏魔数，继续增大不会增加安全性，只会为每个 bundle 放大磁盘、网络与内存成本；构建侧另以 `long` 检查“正文 + 文件头”不能超过单个 `byte[]` 的长度边界。YooAsset 3 的 WebServer / WebNetwork 文件系统支持内存解密，因此 WebGL 也注入同一 `GameBundleOffsetDecryptor`，下载后剥头；自定义 Web 解密器必须实现 `IBundleMemoryDecryptor`，不能只提供文件偏移或流接口。

### 3. 自定义加密走代码接入位，不做多态配置 UI

自定义（XOR / AES 等）经两个静态接入点挂载，项目**不改框架方法体**：

| 端 | 接入点 | 程序集 | 时机 |
|---|---|---|---|
| 构建 | `Game.Framework.Build.GameAssetEncryption` | `Build.Editor` | 项目 Editor `[InitializeOnLoadMethod]` |
| 运行时 | `Game.Framework.GameAssetDecryption` | `Asset.Yoo` | 早于 `AssetUtility.Start` / 显式 `Initialize` |

优先级一律 **自定义 > 偏移 > 不加密**（builder 与 `ApplyDecryptor` 都先看接入点）。

**为什么不做「下拉选算法 + 内联参数」的多态 UI**（曾考虑、否决）：

- 运行时密钥进不了 profile（见 Context），所以任何「算法 + 参数」UI 只能驱动**构建侧 + 非密钥参数**。realistic 方案集是 `{none, offset, custom-keyed}`——只有 offset 适合参数化，而 offset 已是简单字段；custom-keyed 的参数 / 密钥本就进不了 profile。多态 UI 等于给单个 offset 参数加机械。
- 加密器（editor asmdef）与解密器（runtime asmdef）**跨 asmdef 边界**，没法用单个对象承载「两半」；代码接入位（两半项目自管 + 密钥自管）是诚实模型。
- 接入点本身就是「为 UPM 抽包做的扩展位」——抽包后改包内源码违反「包代码不直接改」（根 `AGENTS.md`），接入点正好规避。

### 4. 不内置 AES

正确的强加密是 **AES-CTR 可 Seek 流**（`LoadFromStream` 要求可随机定位，AES-CBC 不可 Seek 会加载失败）——实现复杂、有每次加载的 CPU 代价，且密钥管理是**项目级决策**。内置一个固定密钥的 AES 是虚假安全。故框架提供「偏移内置 + 任意强加密可经接入位插入」，AES 由项目按需自接（或将来按需做成独立可选模块），不进框架核心。**也别停在 XOR**：强度仅比偏移高一点（AB 头是已知明文易还原）却要付内存整包代价，两头不讨好。

### 5. 加密清单是可选项，偏移默认不加密清单

清单（`<包>_<版本>.bytes`）是「地址→bundle 解析图 + 哈希 / 依赖 / tags」，泄露的是**内容结构目录**（资源名、分包、依赖关系）。偏移默认不碰它，因为：casual 工具扒的是 bundle 不读清单；加密清单要付运行时每次启动解密 + 构建侧额外配解密器（构建会回读旧清单做增量）的代价。只有专门要防「枚举资源目录 / 数据挖掘」时才值得，此时已在做真加密。接入点已为清单加 / 解密留位。

### 6. 「构建过程开关」不进 profile

`ClearBuildCacheFiles` / `UseAssetDependencyDB` 是**本机构建过程**旋钮、不进产物、不影响线上，故刻意不放 profile（profile 只放随产物发布的内容配置）：清缓存=一次性排障，走菜单「全量重建」+ CI `-clearBuildCache`；依赖 DB=本机提速偏好（哈希失效的安全缓存），走 EditorPrefs 勾选 + CI `-useAssetDependencyDB`。

### 7. YooAsset 自带 Bundle Builder 窗口被绕过

统一构建走 `FrameworkAssetBuilder`（SBP-only、读我们的 profile），不用 YooAsset 窗口（它对纯资源包会崩、不读我们的配置）。但该窗口靠 `TypeCache` 反射会把 `GameBundleOffsetEncryptor` 列进下拉、用无参构造实例化——故给它一个无参构造作**空操作 + 警告**（不加密），避免在那里误选时抛 `MissingMethodException` 或无声产出未加密包。`FrameworkAssetBuilder` 只在 `FileOffset>0` 时用带参构造，因此偏移为 0 只可能来自该窗口路径。

## Consequences

- **开箱**：场景与构建 Profile 两个人工值对齐，首场景代码值自动派生；对 95% 场景足够，且零性能代价。
- **可扩展**：强加密 / 清单加密经接入点接入，不 fork 框架；为 UPM 抽包预留干净扩展位。
- **诚实的边界**：框架不假装提供「真安全」；强加密的密钥与性能取舍交还项目。
- **对齐与发布成本**：场景运行值仍需与构建 Profile 对齐；代码引导由生成门禁防漂移，但修改后必须重编并原子部署实际 Game.Main / Player 与资源。自定义加 / 解密两半仍由项目保证一致。
- **AES 缺位**：需要强加密的项目要自行实现 AES-CTR 流式解密器（含可 Seek 流），有一定门槛——这是有意的取舍，不是遗漏。
- 完整 how-to / 选型 / 代码示例见 `docs/asset-encryption.md`。
