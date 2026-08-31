# ADR-0021：本地存储（存档）—— IStorageUtility + 原子写文件 provider + 可插拔序列化

**Status:** Accepted（2026-07-03；**2026-08-31 修订 §6**：Provider 物理完成线程与 Utility 的 serializer / FIFO / 公共提交线程解耦）

## Context

roadmap 中期新模块第一项：本地存储 / 存档——需求普适（所有游戏都要存设置和进度）、能立刻验证「接口在内核、实现可替换」的抽象，并顺带定下「存档版本迁移」的姿势。

既有约束与先例：

- **框架理念**：用类型代替字符串 / 单向数据流 / 主线程独占 / 公共异步 API 返回 `UniTask` 且无同步版本时省略 `Async` 后缀。
- **ports & adapters**：重第三方依赖的实现必须隔离在独立模块 asmdef（`IAssetProvider` ← `Game.Framework.Asset.Yoo`）；零依赖的能力可整体留内核（`PoolUtility` 先例）。
- **失败语义先例**（资源系统）：「资源级问题给 null、系统级问题给异常」。
- 候选后端盘点（roadmap）：PlayerPrefs（轻量 KV）、文件（存档主力）、SQLite（关系 / 大数据）、MemoryPack / Newtonsoft（序列化）。项目当前**没有** Newtonsoft / MemoryPack 依赖。

## Decision

### 1. API 形态：显式 key + 类型化整存整取，不做散装 KV

```csharp
public interface IStorageUtility : IUtility
{
    UniTask Save<T>(string key, T data, CancellationToken ct = default) where T : class;
    UniTask<T> Load<T>(string key, CancellationToken ct = default) where T : class; // 无可用数据 → null
    bool Exists(string key);
    UniTask Delete(string key, CancellationToken ct = default);                     // 不存在 = no-op
    UniTask<IReadOnlyList<string>> ListKeys(string prefix = null, CancellationToken ct = default);
}
```

- **类型整存整取**：设置 = 一个 `SettingsData` 类、存档 = 一个 `PlayerSaveData` 类，整对象 Save / Load。**刻意不提供** `GetInt/SetString` 散装 KV——那是 PlayerPrefs 的形态，字符串 key 散落各处正是框架「用类型代替字符串」要消灭的东西；真要碎片 KV（如引导标记）Unity 的 `PlayerPrefs` 本身够用，框架不重复包装。
- **key 显式传、不从类型名推导**：key 是**持久契约**（落成文件名），类型改名 / 移动不该丢存档——推导会把 C# 标识符变成隐式持久契约。多槽位天然是 key 的运行时组合（`"save/slot1"`），特性声明也覆盖不了。
- **key 字符集**：`[A-Za-z0-9-_/]`，`/` 分段（映射子目录做槽位分组），禁空段 / 首尾 `/` / `..`。非法 key 抛 `ArgumentException`（系统级：代码写错了）。
- `where T : class`：让「无可用数据 → null」语义成立（struct 的 default 无法与真实数据区分）；存档类型本就该是类。

### 2. 失败语义：对齐资源系统「预期内缺失给 null、系统级失败抛异常」

| 情形 | 行为 |
|---|---|
| `Load` key 不存在 | 返回 null（新玩家没有存档是常态） |
| `Load` 主文件损坏、备份可用 | 回退备份返回 + `LogWarning`（自动兜住） |
| `Load` 主备都损坏 | 返回 null + `LogError`（业务当新档处理，游戏能继续） |
| `Save` 磁盘满 / 权限 / IO 失败 | **抛异常**（数据没落盘必须让业务知道） |
| key 非法 / data 为 null | 抛 `ArgumentException` / `ArgumentNullException` |
| Dispose 后调用 | 抛 `ObjectDisposedException`（写丢失必须 fail-fast，不学池的宽容警告） |

### 3. 防损坏：原子写 + 上一版自动备份（provider 兜住，这是模块的核心价值）

手写存档最常见的事故是「写一半崩溃 / 断电 = 存档全丢」。`FileStorageProvider` 固定走：

- **写**：序列化字节 → 写临时文件（`.tmp`）→ 原子替换主文件（`File.Replace`，旧主文件自动变 `.bak`；首写用 `File.Move`；`File.Replace` 平台不支持时手动三步兜底）。任何时刻磁盘上都有一份完整可读的数据。
- **读**：主文件缺失或反序列化失败 → 自动回退 `.bak`。备份的提升不做（下次成功 Save 自然重写主文件），避免读路径写盘。
- **列举**：主文件或备份任一存在都算已提交 key，主备去重；仅有 `.tmp` 不算已提交数据。手动替换在「旧主移入备份、临时文件尚未转正」之间中断时，槽位列表仍不会漏掉可尝试回退的存档。
- 每个 key 三个文件：`<key>.sav` / `<key>.sav.bak` / `<key>.sav.tmp`（残留 tmp 下次写覆盖）。扩展名固定 `.sav`（格式中立；默认序列化下内容就是 UTF-8 JSON，随便用文本编辑器打开调试）。

### 4. 版本迁移的姿势：数据里的版本字段 + Load 后业务迁移，框架不做迁移管线

- **默认序列化是 JSON，字段级演进天然免迁移**：新增字段旧档读出取默认值、删除字段被忽略——覆盖绝大多数存档演进。
- **结构性改动**（字段含义变化 / 类型重组）：约定在数据类型里放 `public int Version` 字段，`Load` 后检查并链式迁移（v1→v2→v3 就是业务代码里一个 switch），迁移完 Save 回写。
- 框架**刻意不提供**迁移注册表 / 特性 / 管线：迁移逻辑本质是业务代码，一个 switch 足够直白；管线只是把 switch 搬进框架还丢了可读性（no-over-engineering）。姿势文档化在 guide / demo。

### 5. 两个正交扩展点，接口在内核、默认实现零依赖也留内核

```
IStorageUtility（业务入口，GetUtility 解析）
   └─ StorageUtility（编排：key 校验 + 序列化 + FIFO 串行 + 备份回退）
        ├─ IStorageSerializer（对象 ↔ 字节）── 默认 JsonUtilityStorageSerializer
        └─ IStorageProvider（字节 ↔ 介质）── 默认 FileStorageProvider（persistentDataPath）
```

- **换后端**（SQLite / 云存档 / PlayerPrefs 桥）= 换 provider；**换格式**（MemoryPack / Newtonsoft）= 换 serializer。两者经 `StorageUtility` 构造函数注入，互不牵连。
- 默认实现全部住内核 `Core/Storage/`：纯 BCL（`System.IO`）+ `JsonUtility`，零第三方依赖——同 `PoolUtility` 先例；将来出现重依赖后端（SQLite）才开独立模块 asmdef（同 `Asset.Yoo` 先例）。
- provider 接口保留 `Async` 后缀（适配层惯例，同 `IAssetProvider`）；`IStorageUtility` 公共 API 无后缀（无同步版本）。
- 默认 `JsonUtilityStorageSerializer` 的已知限制随文档声明：仅序列化 `[Serializable]` 类型的**字段**（不含属性），不支持 `Dictionary` / 多态 / 可空值类型。存档类型照此设计（List + 平铺字段）；确需更强格式，换 serializer 是一行构造参数。

### 6. 并发与线程：全局 FIFO 串行 + IO 下线程池

- 公共 API **主线程调用**（框架统一契约）；内部所有操作进**全局 FIFO 队列**逐个执行——同 key 并发写、读写交错、`Save` 未落盘就 `Delete` 等竞态全部消失。存档操作天然低频，串行无感知；per-key 并行是没有收益的复杂度。
- 队列实现：尾任务链（前驱在 finally 里必然完成，异常只传给各自调用方、不毒化队列）。
- `Dispose` 分成两个时刻：同步发布“逻辑已释放”并拒绝新请求；此前已入队操作仍按 FIFO 排空，provider 作为 terminal 最后释放。
  它不会为了等待未完成的队列而同步阻塞，也不会让 SQLite / 云存储等有连接的 provider 与在途操作并发释放；但队列已空时同步
  `provider.Dispose` 可以内联执行，因此 Adapter 的释放实现仍应短小。物理释放失败只能记 Error，
  因为延后发生的异常无法再可靠地同步交还 `Dispose` 调用方；重复 `Dispose` 仍只安排一次 terminal。
- **序列化在主线程**（`JsonUtility` 最稳妥；典型存档体积的序列化耗时可忽略），**文件 IO 切线程池**（大存档写盘不卡帧）。`IStorageProvider` 的异步物理终态允许停在任意线程；`StorageUtility` 在反序列化、推进 FIFO gate、释放 provider 和交付 Save / Load / Delete / ListKeys 的成功、异常或取消前统一恢复 Unity 主线程。调用方 token 即使从 worker 取消也不改变这条提交边界。
- 默认 `FileStorageProvider` 明确让 `RunOnThreadPool(..., configureAwait: false)` 保持在线程池终结，避免 Adapter 先排一次 PlayerLoop、Utility 再重复兜底；线程切换的所有权只在 Utility 一处。自定义 SQLite / 云存档 Adapter 无需复制主线程调度，但同步 `Exists` 与 `Dispose` 仍会由 Utility 从主线程串行调用。
- `Exists` 是同步快照（不排队）：`await` 完 `Save` 再查询是一致的；对 fire-and-forget 的写它可能暂时返回 false——文档明示「别 fire-and-forget Save」。

### 7. 存储位置与注册

- 根目录 = `Application.persistentDataPath/<folder>/`（默认 `storage`），Editor 与真机同语义；调试经 demo 章「打开存档目录」。
  `MonoStorageUtility` 的 Inspector 字段是**单个可移植目录名**，长度 1–255，仅允许 `[A-Za-z0-9_-]`，并拒绝 Windows 保留设备名；它不是相对路径或绝对路径。
  解析后还会证明结果是 `persistentDataPath` 的直接子目录，避免 `..` / rooted path 越界和意外递归扫描。配置非法会在注册前 fail-fast，
  不 Trim、不兜底、不自动搬数据；确需显式绝对路径的工具或测试应代码构造 `FileStorageProvider`。
- 注册与池同款三选一：`builder.RegisterOwnedUtility(new StorageUtility())`（推荐，自动推导具体类型与 Utility Interface，并随 Context Dispose 释放 provider）/ `RegisterUtility`（生命周期由外部 owner 管理）/ `MonoStorageUtility` 挂 Context 节点（Inspector 配目录名，组合纯 C# 实现）。

### 8. 刻意不做（记录在案，等真实需求）

- **加密 / 防篡改**：单机本地防不住（内存修改器绕过一切），联网游戏真源在服务器；serializer 可插拔已是接入位（写个加密 serializer 包一层即可）。
- **SQLite / 关系后端**：查询型大数据需求出现时再做第二个 provider（顺带验证抽象边界，同资源系统的 Addressables 论）。
- **云同步 / 自动定时保存**：业务节奏与平台 SDK 领域，不进框架。
- **PlayerPrefs 包装**：Unity 原生 API 已够薄。

## Consequences

- 业务存档 = 定义 `[Serializable]` 类 + `Save/Load` 两行；断电 / 崩溃安全由框架兜住，不再依赖各项目手写「临时文件 + 替换」样板。
- key 成为持久契约：改 key = 丢旧档（等同改资源 location），业务用常量 / 只增不改。
- 默认 JSON 的体积与解析速度对「设置 + 常规存档」绰绰有余；重度存档（几 MB 起）换 MemoryPack serializer，接口不动。
- `Load` 把「损坏」折叠进 null（打过日志）：业务无法区分「新玩家」与「双份全坏」——这是刻意的（两种情况的正确反应都是开新档）；需要区分的运营场景（上报损坏率）看日志 / 后续再议。
- 全局 FIFO 意味着一个超大存档的写会让后续存储操作排队（不卡主线程，只是延后完成）——低频场景可接受，热路径本就不该同步等存档。
