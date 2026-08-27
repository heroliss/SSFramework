# 资源加密与构建过程开关

资源后端（YooAsset）的加密分**构建侧**（写出加密产物）和**运行时侧**（读时解密）两端，**必须成对、参数一致**。
本框架把两端各自收口在一处：

| 端 | 收口位置 | 程序集 |
|---|---|---|
| 构建侧加密 | `FrameworkAssetBuilder.BuildPackage`（设 `ScriptableBuildParameters.BundleEncryptor` 等） | `Game.Framework.Build.Editor` |
| 运行时侧解密 | `YooAssetProvider.ApplyDecryptor`（按 `EFileSystemParameter` 注入解密器） | `Game.Framework.Asset.Yoo` |

> 业务代码、配置都**不直接碰 YooAsset 的加密类型**——只在上面两处接触，符合 ADR-0013「YooAsset 收口在 provider」。

---

## 1. 偏移加密（框架已内置，开箱即用）

偏移加密 = 在每个 bundle 文件头前插入 N 个字节，使产物不再以 AssetBundle 魔数开头，挡住「直接拖进 AssetStudio / AB 提取工具打开」的最低门槛。
**它是弱加密**（不改 bundle 正文、只换个头），解密近乎零成本（运行时带偏移加载或剥头）；要真正防逆向用第 3 节的内容加密。先读第 2 节「选型」再决定要不要升级。

### 启用：只改两个数字，且必须一致

| 改哪 | 字段 | 含义 |
|---|---|---|
| 构建配置 `FrameworkAssetBuildProfile`（工作台 `SSFramework/构建与发布/资源构建`） | `FileOffset` | 构建时每个 bundle 头插入的字节数 |
| 场景节点 `AssetSystemConfigModel`（Inspector「加密」栏） | `FileOffset` | 运行时跳过的字节数 |

两个值**必须完全相等**：构建插几个字节、运行时就跳几个字节，对不上会读坏**所有** bundle。`0` = 不加密（默认）。
（这与已有的「`profile.LocalServePort` 必须等于 `AssetSystemConfigModel.CdnUrls` 端口」是同一种「构建↔运行时手动对齐」约定。）

改完重新构建即生效。验证：加密产物的 hash 会变（YooAsset 对**加密后**的文件算 hash 写清单），首次构建后全量产物都会更新。

> ⚠ 别用 YooAsset 自带的 **Bundle Builder 窗口**构建：本框架统一走 `SSFramework/构建与发布/资源构建` 工作台（`FrameworkAssetBuilder`），那个窗口被绕过（SBP-only、对纯资源包会崩、配置也不读我们的 profile）。该窗口靠反射会把 `GameBundleOffsetEncryptor` 列进它的「Bundle Encryptor」下拉，但它用无参构造实例化——本类的无参构造是**空操作 + 警告**（不加密），就是为了「在那里误选也不崩、且不会无声产出未加密包」。要加密请回到框架工作台 + profile。

### 原理 / 落点

- 构建：`profile.FileOffset > 0` 时，`FrameworkAssetBuilder.BuildPackage` 挂上 `GameBundleOffsetEncryptor`——读原文件、头部插入 N 个**确定性**字节（按 bundle 名 FNV-1a 派生，保证内容不变的包每次构建产出同样的头，不破坏增量发布），返回加密数据。
- 运行时：`AssetSystemConfigModel.FileOffset` 经 `ToProviderConfig()` → `AssetProviderConfig.FileOffset` → `YooAssetProvider.ApplyDecryptor` 注册 `GameBundleOffsetDecryptor`（同时作 `AssetBundleDecryptor` / `RawBundleDecryptor` / `AssetBundleFallbackDecryptor`）。它实现 `IBundleOffsetDecryptor`（带偏移直接加载）+ `IBundleMemoryDecryptor`（内存兜底：剥头后从内存加载）。

---

## 2. 加密方案对比与选型（为什么默认偏移）

> 先认清前提：**客户端资源加密永远是「抬高门槛」，不是「真正安全」**。解密密钥必须随客户端下发——玩家设备得能解密才能用资源——所以足够执着的人总能从内存 / 反编译里拿到密钥。选型不是「哪个最安全」，而是「能拦住目标人群的前提下，代价多小」。

YooAsset 3.0 运行时有三种解密形态，对应三类方案、三种代价（落点见 `LoadLocalAssetBundleOperation`）：

| 方案 | 运行时接口 → 加载方式 | 强度 | 运行时代价 | 适用 |
|---|---|---|---|---|
| **偏移**（默认） | `IBundleOffsetDecryptor` → `AssetBundle.LoadFromFile(path, 0, offset)` | 弱（仅破坏魔数） | ≈0（内存映射直接加载，不解密正文、不额外占内存） | 绝大多数游戏：拦掉「拖进 AssetStudio」级别的扒取 |
| **内存整包**（XOR / AES） | `IBundleMemoryDecryptor` → `LoadFromMemory(解密后字节)` | XOR 弱 / AES 强 | 高（整包读进内存解密，CPU + 双倍内存峰值） | 小包（配置 / 关键数据），**不适合大资源包** |
| **流式**（AES-CTR） | `IBundleStreamDecryptor` → `LoadFromStream(解密流)` | 强 | 中（边读边解，内存友好） | 要真加密又有大包：当前最优的「强加密」选择 |

**为什么默认偏移、它也最流行：**
- **代价为零**：Unity 的 `AssetBundle.LoadFromFile` 原生带 offset 参数，带偏移直接内存映射加载，不必把整包读进内存——CPU / 内存 / 加载耗时都与不加密几乎一致。其余方案要么整包进内存（memory），要么逐块解密（stream），都有实打实代价。
- **正好打中真实威胁**：客户端资源最常见的「攻击」就是把 .bundle 拖进 AssetStudio / UnityEX 这类工具，它们靠 `UnityFS` 魔数识别 AB。头部插几个字节让魔数对不上，工具就打不开，挡掉绝大多数随手扒取。
- **不引入密钥管理**：没有密钥就没有「密钥怎么藏 / 怎么轮换」的麻烦，也不会因为密钥实现得差而产生虚假安全感。
- **代价**：知道偏移量（或扫文件找 `UnityFS` 魔数）就能秒剥——所以它防的是「顺手扒」，不是专业逆向。

**什么时候升级到 AES**：你的资源被逆向价值较高、且愿意为此付加载代价时。这时用**流式 `IBundleStreamDecryptor` + AES-CTR**，别用内存整包：
- ⚠ `AssetBundle.LoadFromStream` 要求**可随机定位（Seek）的流**——AB 加载会在流里来回跳。**AES-CBC 不能随机定位**，直接套 `CryptoStream(CBC)` 会加载失败；必须用 **CTR 计数器模式**（任意偏移的 keystream 可独立算出）自己实现一个可 Seek 的解密流。
- **XOR 不值得单独用**：强度只比偏移强一点点（AB 头部是已知明文，XOR 易被还原），却要走内存整包、付双倍内存代价——要么用偏移（更省），要么直接上 AES-CTR（更强），中间的 XOR 是两头不讨好。

> 一句话选型：**默认偏移**够用；**要真加密就 AES-CTR 流式**；**别在中间停在 XOR**。

---

## 3. 自定义内容加密（XOR / AES 等）

偏移加密不够时（要真正打乱 bundle 正文 / 加密清单），自己实现加解密器。同样**两端成对**（强度/代价取舍见上一节）：

### 构建侧：实现 `IBundleEncryptor`（在 `Game.Framework.Build.Editor`）

参考内置的 `GameBundleOffsetEncryptor`，新写一个：

```csharp
internal sealed class MyXorBundleEncryptor : IBundleEncryptor
{
    private const byte Key = 0x5A;
    public BundleEncryptResult Encrypt(BundleEncryptArgs args)
    {
        byte[] data = System.IO.File.ReadAllBytes(args.FilePath);
        for (int i = 0; i < data.Length; i++) data[i] ^= Key;   // 真实项目用更强算法 + 安全密钥管理
        return new BundleEncryptResult(true, data);             // IsEncrypted=false 则该包不加密
    }
}
```

**挂到构建（不改框架源码）**：在项目 Editor 程序集里用 `[InitializeOnLoadMethod]` 设到接入点 `GameAssetEncryption`，`FrameworkAssetBuilder` 构建时会优先用它（否则回退偏移加密）。批处理 / CI 构建也跑 InitializeOnLoad，故 CI 同样生效：

```csharp
using UnityEditor;
using Game.Framework.Build;

internal static class MyEncryptionInstaller
{
    [InitializeOnLoadMethod]
    private static void Install()
    {
        GameAssetEncryption.CustomBundleEncryptor = new MyXorBundleEncryptor();
        // 要加密清单时再设这两个（成对）；不加密清单就别设：
        // GameAssetEncryption.CustomManifestEncryptor = new MyManifestEncryptor();
        // GameAssetEncryption.CustomManifestDecryptor = new MyManifestDecryptor();
    }
}
```

- **优先级**：自定义加密器 > profile 偏移加密 > 不加密。二者都配会警告并以自定义为准——用自定义时把 `profile.FileOffset` 置 0。
- **清单也要加密**：`CustomManifestEncryptor`（`IManifestEncryptor`）+ **必须同时** `CustomManifestDecryptor`（`IManifestDecryptor`）——构建会回读旧清单做增量 / 版本对比，没有解密器读不了已加密的旧清单。
- bundle 加密只影响**正文字节**，文件 hash/CRC 按加密后的产物计算，运行时下载校验的就是加密产物，无需额外处理。

### 运行时侧：实现 `IBundleDecryptor` 的某个派生接口（在 `Game.Framework.Asset.Yoo`）

`IBundleDecryptor` 是空基接口，按加密形态**三选一**实现：

| 接口 | 适用 | 关键方法 |
|---|---|---|
| `IBundleOffsetDecryptor` | 偏移式（头部跳过 N 字节，正文不动） | `long GetFileOffset(args)` |
| `IBundleMemoryDecryptor` | 整包内存解密（如 XOR / AES，正文被打乱） | `byte[] GetDecryptedData(args)`（用 `args.FileData`） |
| `IBundleStreamDecryptor` | 大文件流式解密（边读边解，省内存） | `int GetBufferSize(args)` + `Stream CreateDecryptionStream(args)` |

XOR/AES 这类内容加密用 `IBundleMemoryDecryptor`（大文件用 `IBundleStreamDecryptor`）。清单加密则另实现 `IManifestDecryptor`。

**挂到运行时（不改框架源码）**：在资源初始化【之前】（启动引导里，先于 `AssetInitSystem` 跑）设到接入点 `GameAssetDecryption`，`YooAssetProvider.ApplyDecryptor` 会优先用它（否则回退偏移解密）：

```csharp
using Game.Framework;

// 启动引导（早于资源初始化）：
GameAssetDecryption.BundleDecryptorFactory = () => new MyXorBundleDecryptor(/* key... */);
// 清单加密时再设（不加密清单就别设）：
// GameAssetDecryption.ManifestDecryptorFactory = () => new MyManifestDecryptor();
```

框架内部据此把你的解密器登记为 `AssetBundleDecryptor` / `RawBundleDecryptor`，实现了 `IBundleMemoryDecryptor` 时还登记为内存兜底解密器；清单工厂非空时登记 `ManifestDecryptor`。**优先级**：自定义解密器 > 偏移解密（`FileOffset`）> 不加密。

### 注意

- **密钥别硬编码进明文常量**（上面只是示例）；真实项目做密钥分发 / 混淆，并评估「加密强度 vs 加载耗时」。
- **热更边界**：运行时解密器在 `Game.Framework.Asset.Yoo`（默认在热更程序集列表里）。若解密逻辑要随版本更新，注意它走代码热更；涉及 AOT 裁剪时确认 `Asset.Yoo/link.xml` 保留相关类型。
- 这两端是**同一套算法的两半**，任何一边改了另一边必须同步，否则运行时整批加载失败。建议把「加密方式 + 密钥来源」记一处、两边引用。

### 接入位小结（不改框架源码）

自定义加密通过两个**接入点静态类**挂载，项目不必动框架方法体（为框架抽成 UPM 包后「包代码不直接改」做好准备）：

| 端 | 接入点 | 程序集 | 设置时机 |
|---|---|---|---|
| 构建 | `Game.Framework.Build.GameAssetEncryption`（`CustomBundleEncryptor` 等） | `Game.Framework.Build.Editor` | 项目 Editor 程序集 `[InitializeOnLoadMethod]` |
| 运行时 | `Game.Framework.GameAssetDecryption`（`BundleDecryptorFactory` 等） | `Game.Framework.Asset.Yoo` | 启动引导，早于 `AssetInitSystem` |

- 这两端是**同一套算法的两半**，必须成对、算法一致；任一边漏设或不匹配 → 运行时整批加载失败。建议把「加密方式 + 密钥来源」收一处、两边引用。
- **框架不内置 AES**：正确的强加密（AES-CTR 可 Seek 流）实现复杂、有加载耗时，且密钥管理是项目级决策——硬塞一个固定密钥的 AES 是虚假安全。需要时由项目经上面接入位接入（或将来按需补一个独立可选模块），不污染框架核心。

---

## 4. 构建过程开关：Clear Build Cache / Use Asset Dependency DB

YooAsset Bundle Builder 窗口里的这两个开关是**本机构建过程**的旋钮——**不进产物、不影响线上行为**，所以**刻意不放进** `FrameworkAssetBuildProfile`（profile 只放「会随产物发布的内容配置」）。它们另走构建入口：

| 开关 | 作用 | 默认 | 框架怎么用 |
|---|---|---|---|
| **Clear Build Cache**（`ClearBuildCacheFiles`） | 清掉 SBP 增量构建缓存后**全量重建** | 关（走增量，更快） | 一次性排障动作：资源构建工作台“全量重建”；CI 加 `-clearBuildCache` |
| **Use Asset Dependency DB**（`UseAssetDependencyDB`） | 用资源依赖缓存数据库**加速收集阶段** | 关 | 本机持久偏好：菜单勾选项 `构建用资源依赖数据库 (加速收集)`（存 EditorPrefs，影响菜单两个构建入口）；CI 加 `-useAssetDependencyDB` |

- **为什么 Clear Build Cache 是「菜单动作」不是「持久开关」**：它是「这一次强制全量」，平时应保持增量；做成持久开关容易忘了关、每次都白等全量重建。
- **为什么 Use Asset Dependency DB 用 EditorPrefs 不用 profile**：它是「这台机器构建多快」，与「发布什么内容」无关，且按机器/开发者不同，放进版本控制的 profile 会污染他人。CI 是另一台机器（EditorPrefs 不随仓库走），所以 CI 用命令行 `-useAssetDependencyDB` 单独控制。

### Use Asset Dependency DB 默认关 = 不推荐开吗？

**不是**——它是一个**安全的**优化，默认关只是 YooAsset 的保守取向，不代表「别开」。

- **它做什么**：在 `Library/AssetDependencyDB` 维护一份「资源 → 依赖列表」的持久缓存，省掉每次构建都对每个资源跑 `AssetDatabase.GetDependencies`（大工程收集阶段的主要耗时）。
- **会不会用到旧依赖**：不会。缓存按 `AssetDatabase.GetAssetDependencyHash`（聚合了资源内容 / meta / 导入器版本 / 目标平台）做失效——资源一改 hash 就变、该条自动重算，只有没变的才复用。所以是**哈希失效的正确缓存**，不是「拍快照后不管」。
- **为什么仍默认关**：① 收益依赖**热缓存**——首次构建 / 冷 `Library` 要先把库建起来，没省反增；② 收益和**工程规模**强相关，小工程收集本就快、几乎无感；③ 库较新，保守起见交给用户显式开。

**怎么选**：
- 本机日常迭代构建、工程资源多 → **开**（菜单勾上 `构建用资源依赖数据库 (加速收集)`，一次勾选长期生效）。
- 小工程 / 偶尔构建 → 开不开都行，差别不大。
- CI → 只有当 `Library/` 在多次运行间被缓存复用时才有意义（冷 runner 每次重建库，不划算）；要开就加 `-useAssetDependencyDB`。

CI 用法（`build-assets.yml` 的 `workflow_dispatch` 已有对应勾选项，自动转成下面的开关式参数）：

```bash
Unity -batchmode -quit -nographics -projectPath . -buildTarget Android \
      -executeMethod Game.Framework.Build.FrameworkAssetBuilder.BuildAll \
      -version 1.2.3 [-output ...] [-packages A,B] \
      [-clearBuildCache] [-useAssetDependencyDB]   # 开关式：加了才生效，不带值
```

> 偏移加密**没有** CLI 开关：它要与运行时一致，统一由入库的 `FrameworkAssetBuildProfile.FileOffset` 决定（属于「内容配置」，随产物发布）。

---

## 相关

- 设计决策与取舍（为何这么选）：[ADR-0018](adr/0018-asset-encryption.md)
- 运行模式 / 加载流程：`docs/asset-system-flow.md`
- YooAsset 集成踩坑：`docs/yooasset-pitfalls.md`
- 构建管线职责（构建 / 部署）：`FrameworkAssetBuilder` 类注释
