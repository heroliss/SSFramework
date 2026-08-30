using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Storage;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·本地存储：类型化整存整取的存档 / 设置——Save / Load / Exists / Delete / ListKeys 五个 API，
    /// 防损坏（原子写 + 上一版备份自动回退）由框架兜住，版本迁移姿势 = 数据里的 Version 字段 + Load 后 switch。
    /// 本章的 <see cref="IStorageUtility"/> 经 <see cref="InstallBindings"/> 用 RegisterOwnedUtility 注册（纯 C# 服务的标准路径）。
    /// </summary>
    public sealed class StorageDemoModule : DemoModuleBase
    {
        public override string Id => "local-storage";
        public override string Title => "本地存储 · 存档";
        public override string Category => "能力";
        public override int Order => 30;
        public override string Summary =>
            "类型化整存整取的持久化：Save/Load/Exists/Delete/ListKeys；断电安全（原子写+备份回退）框架兜住；" +
            "版本迁移 = 数据里的 Version 字段 + Load 后 switch。介质与格式两个扩展点可插拔，设计见 ADR-0021。";

        // 当前存档结构版本：结构性改动时 +1，并在 MigrateIfNeeded 里补一个 case。
        private const int CurrentSaveVersion = 2;

        // key 是持久契约（落成文件名）：用常量管理、只增不改——改 key 等同丢弃旧数据。
        private const string ProfileKey = "profile";
        private const string SlotPrefix = "save/";
        private const string Slot1Key = SlotPrefix + "slot1";
        private const string Slot2Key = SlotPrefix + "slot2";
        private const string LegacyKey = "legacy";
        // “重置本章数据”只逐个删除这份白名单，绝不按目录递归清理 persistentDataPath。
        private static readonly string[] DemoKeys = { ProfileKey, Slot1Key, Slot2Key, LegacyKey };

        /// <summary>供教学契约测试锁定“只删本章已知 key”的白名单；生产重置与测试读取同一个真源。</summary>
        internal static IReadOnlyList<string> ResetKeys => DemoKeys;

        /// <summary>演示用存档类型：必须 [Serializable]（默认 JSON 序列化只认 [Serializable] 类的字段）。</summary>
        [Serializable]
        private class DemoSaveData
        {
            public int Version = CurrentSaveVersion; // 版本迁移姿势的锚点字段（ADR-0021 §4）
            public int Level;
            public string PlayerName = "";
            public List<string> Unlocked = new List<string>();

            public override string ToString() =>
                $"Version={Version}  Level={Level}  PlayerName=\"{PlayerName}\"  Unlocked=[{string.Join(",", Unlocked)}]";
        }

        // demo 自建文件后端的根目录（独立于正式默认目录）；损坏演示要直捣文件，Build 里按同一路径定位。
        private static string DemoRootPath => Path.Combine(Application.persistentDataPath, "storage-demo");

        // 白盒损坏步骤绕过 IStorageUtility 的 FIFO 直接碰文件，而“重置本章数据”又横跨所有已知 key；
        // 因此本章全部存储动作共用额外 gate，保证重置完成后不会有较早点击的保存续体重新写回数据。
        // gate 跟模块实例走：UIDocument 重建会重新 Build，但旧队列操作可能仍在响应取消，不能在新 Build 中把互斥状态归零。
        private readonly DemoOperationGate _profileOperationGate = new();

        /// <summary>
        /// 纯 C# 服务的标准注册路径：RegisterOwnedUtility 自动推导具体类型与 Utility Interface，
        /// 并随 Context Dispose 自动释放（这里即退出 Play / 关闭 demo）。
        /// 挂场景节点、要 Inspector 配目录的项目用 MonoStorageUtility（同一套逻辑的 Mono 壳）。
        /// 本阶段只声明注册关系；Build 需要的运行时对象仍从 Context 解析，让所有权与 View 权限保持清晰。
        /// </summary>
        public override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterOwnedUtility(new StorageUtility(new FileStorageProvider(DemoRootPath)));
        }

        public override void Build(DemoModuleHost host)
        {
            var storage = this.GetUtility<IStorageUtility>();

            // ── 定位 ──
            host.AddPositioning("类型化整存整取，防损坏由框架兜住");
            host.AddNote("每类持久数据定义一个 `[Serializable]` 类（设置 = `SettingsData`、存档 = `PlayerSaveData`），整对象 `Save` / `Load`——延续框架「用类型代替字符串」的理念，**刻意不提供** `GetInt/SetString` 散装 KV（碎片标记 Unity 的 `PlayerPrefs` 本身够用）。写路径 = 临时文件 → 原子替换 → 上一版自动备份，读路径 = 主文件损坏自动回退备份：**写一半崩溃 / 断电不丢档**，业务不再手写这类样板。",
                new CodeRef("Assets/Game/Framework/Core/Storage/IStorageUtility.cs", "public interface IStorageUtility", "存储入口契约"));
            host.AddSubNote("「key 是持久契约」：落成文件名，显式传、用常量管理、只增不改（改 key = 丢旧档，与资源 location 同一心智）。字符集限字母/数字/-/_，`/` 分段做槽位分组；非法 key 抛异常（本章 key 都是本文件顶部的 const）。");

            // ── 注册方式 ──
            host.AddSectionTitle("注册：纯 C# 服务的三选一");
            host.AddNote("本章的 `IStorageUtility` 在 `InstallBindings` 里 `RegisterOwnedUtility` 注册：自动登记具体类型与 Utility Interface，并随 Context Dispose 自动释放 provider。另两条路：生命周期由外部持有时用 `RegisterUtility`；要 Inspector 配根目录 / 跟随场景节点用 `MonoStorageUtility`（同一套逻辑的 Mono 壳，挂 Context 子节点即注册）。它的目录字段只接受 `persistentDataPath` 下的单个可移植名称（英文字母/数字/-/_），不是相对或绝对路径；改名等同切换数据集。",
                CodeRef.Here("builder.RegisterOwnedUtility(new StorageUtility(new FileStorageProvider", "本章的注册代码"));

            // ── 基础操作 ──
            host.AddSectionTitle("基础操作：Save / Load / Exists / Delete（原子按钮）");
            var current = new DemoSaveData();
            var stateLabel = host.AddValueDisplay("内存中的对象：" + current);
            stateLabel.style.whiteSpace = WhiteSpace.Normal;
            var opLabel = host.AddValueDisplay("对象在内存里改动 ≠ 已持久化——显式 Save 才落盘。");
            opLabel.style.whiteSpace = WhiteSpace.Normal;
            DemoSaveData recoveredProfile = null;
            bool profileMainCorrupted = false;

            void RefreshState() => stateLabel.text = "内存中的对象：" + current;

            // IStorageUtility 操作会进内部 FIFO；唯独“故意写坏文件”是教学用的白盒直写。
            // 本章所有存储步骤再共用这个 gate：既避免白盒直写争抢 profile，也让跨 key 重置拥有稳定终态。
            async UniTask RunProfileOperation(
                CancellationToken ct,
                Label feedback,
                Func<CancellationToken, UniTask> operation)
            {
                if (!_profileOperationGate.TryEnter(out var lease))
                {
                    feedback.text = "本章另一个存储步骤正在进行，请稍候。";
                    return;
                }

                using (lease)
                {
                    await operation(ct);
                }
            }

            host.AddActionRow("改一点状态（Level+1，仅内存）", () =>
            {
                if (_profileOperationGate.IsEntered)
                {
                    opLabel.text = "profile 正在读写，请等本次快照落盘后再修改内存对象。";
                    return;
                }
                current.Level++;
                RefreshState();
                opLabel.text = "内存对象已改，但还没 Save——现在退出，改动就没了。";
            });
            host.AddAsyncActionRow("保存（Save）", chapterCt => RunProfileOperation(chapterCt, opLabel, async ct =>
            {
                await storage.Save(ProfileKey, current, ct); // 单次保存按钮：失败由调用方观察
                ct.ThrowIfCancellationRequested();
                recoveredProfile = null;
                profileMainCorrupted = false;
                opLabel.text = $"已保存 ✓ key=「{ProfileKey}」。IO 失败会抛异常（磁盘满 / 权限），业务要能感知。";
            }), CodeRef.Here("storage.Save(ProfileKey, current, ct); // 单次保存按钮", "保存"));
            host.AddAsyncActionRow("读取（Load）", chapterCt => RunProfileOperation(chapterCt, opLabel, async ct =>
            {
                var loaded = await storage.Load<DemoSaveData>(ProfileKey, ct); // 常规读取
                ct.ThrowIfCancellationRequested();
                if (loaded == null)
                {
                    opLabel.text = "Load 返回 null——没有可用数据（从未保存 / 已删除 / 主备全坏）。新玩家没有存档是常态，业务按「开新档」处理。";
                }
                else
                {
                    current = loaded;
                    RefreshState();
                    opLabel.text = "已读取 ✓ 内存对象替换为磁盘上的数据。";
                }
            }), CodeRef.Here("storage.Load<DemoSaveData>(ProfileKey, ct); // 常规读取", "读取"));
            host.AddActionRow("Exists（是否有已落盘数据）", () =>
            {
                if (_profileOperationGate.IsEntered)
                {
                    opLabel.text = "profile 正在读写；Exists 是不进队列的同步快照，请等操作完成后再查。";
                    return;
                }
                bool exists = storage.Exists(ProfileKey);
                opLabel.text = exists ? $"Exists(「{ProfileKey}」) = true ✓（主或备份任一存在）" : $"Exists(「{ProfileKey}」) = false（还没保存过，或已删除）";
            }, CodeRef.Here("storage.Exists(ProfileKey)", "存在性查询"));
            host.AddAsyncActionRow("删除（Delete，含备份）", chapterCt => RunProfileOperation(chapterCt, opLabel, async ct =>
            {
                await storage.Delete(ProfileKey, ct);
                ct.ThrowIfCancellationRequested();
                recoveredProfile = null;
                profileMainCorrupted = false;
                opLabel.text = "已删除 ✓（主 + 备份一并删；删不存在的 key 是 no-op）。";
            }), CodeRef.Here("storage.Delete(ProfileKey, ct)", "删除"));
            host.AddNote("失败语义与资源系统同一套：**预期内缺失给 null**（没存过 / 主备全坏——后者已打 error，业务当新档），**系统级失败抛异常**（Save 磁盘满 / key 非法 / Dispose 后调用）。全部操作内部走全局 FIFO 串行——同 key 竞态、读写交错天然消失；**别 fire-and-forget Save**（await 它，`Exists` 是不排队的同步快照）。");
            host.AddSubNote("FIFO 只覆盖经 `IStorageUtility` 进入的操作；本章“故意写坏文件”是教学白盒、刻意绕过接口，所以另用模块级闸门与 profile 的 Save / Load / Delete 互斥。真实业务不要直碰 `.sav`，自然无需这层补丁。");

            // ── 槽位 ──
            host.AddSectionTitle("多槽位：key 分段 + ListKeys 前缀列举");
            var slotsLabel = host.AddValueDisplay("槽位就是 key 的分段约定（save/slot1、save/slot2…），没有专门的槽位 API。");
            slotsLabel.style.whiteSpace = WhiteSpace.Normal;
            host.AddAsyncActionRow("保存到槽位 1（save/slot1）", chapterCt => RunProfileOperation(chapterCt, slotsLabel, async ct =>
            {
                await storage.Save(Slot1Key, current, ct);
                ct.ThrowIfCancellationRequested();
                slotsLabel.text = "已保存到 save/slot1 ✓（内容 = 当前内存对象）";
            }), CodeRef.Here("storage.Save(Slot1Key", "存槽位"));
            host.AddAsyncActionRow("保存到槽位 2（save/slot2）", chapterCt => RunProfileOperation(chapterCt, slotsLabel, async ct =>
            {
                await storage.Save(Slot2Key, current, ct);
                ct.ThrowIfCancellationRequested();
                slotsLabel.text = "已保存到 save/slot2 ✓";
            }));
            host.AddAsyncActionRow("列出全部槽位（ListKeys(\"save/\")）", chapterCt => RunProfileOperation(chapterCt, slotsLabel, async ct =>
            {
                var keys = await storage.ListKeys(SlotPrefix, ct);
                ct.ThrowIfCancellationRequested();
                slotsLabel.text = keys.Count == 0
                    ? "没有任何槽位——先点上面「保存到槽位」。"
                    : $"共 {keys.Count} 个槽位：{string.Join("、", keys)}（排序稳定，直接喂存档选择 UI）";
            }), CodeRef.Here("storage.ListKeys(SlotPrefix, ct)", "前缀列举"));
            host.AddSubNote("`ListKeys` 列的是**已提交槽位**：主文件或备份任一存在都会出现，主备同时存在只算一个；孤立 `.tmp` 是未提交写入，不会误显示。这样即使替换途中中断、只剩 `.bak`，存档选择页仍能让玩家尝试进入并由 `Load` 回退。");

            // ── 防损坏演示 ──
            host.AddSectionTitle("防损坏：故意写坏主文件，看备份回退");
            host.AddExperimentNotice(
                "只会改坏本章专属目录 storage-demo 下的 profile.sav；不会触碰项目正式存档、槽位或其他 persistentDataPath 内容。",
                "③ 会从 profile.sav.bak 读回上一版，并在 Console 精确留下 2 条 Warning：主文件反序列化失败、已回退备份。",
                "③ 成功后执行④：把回退数据连续保存两次，第一遍重建主文件，第二遍再建立健康备份；也可用下方“重置本章数据”从零开始。");
            var corruptLabel = host.AddValueDisplay("步骤：① 保存两次（产生备份）→ ② 损坏主文件 → ③ 回退读取（2 条 Warning）→ ④ 连存两次恢复健康主备份。");
            corruptLabel.style.whiteSpace = WhiteSpace.Normal;
            host.AddExperimentAsyncActionRow("① 连存两次（产生 .bak）", chapterCt => RunProfileOperation(chapterCt, corruptLabel, async ct =>
            {
                recoveredProfile = null;
                profileMainCorrupted = false;
                await storage.Save(ProfileKey, current, ct);
                ct.ThrowIfCancellationRequested();
                current.Level++;
                RefreshState();
                await storage.Save(ProfileKey, current, ct);
                ct.ThrowIfCancellationRequested();
                corruptLabel.text = $"已连存两次 ✓ 磁盘上现在有主文件（Level={current.Level}）和上一版备份（Level={current.Level - 1}）。";
            }));
            host.AddExperimentActionRow("② 模拟损坏主文件（仅本章）", () =>
            {
                if (!_profileOperationGate.TryEnter(out var lease))
                {
                    corruptLabel.text = "另一个 profile 存储步骤正在进行，请稍候。";
                    return;
                }

                using (lease)
                {
                    string main = Path.Combine(DemoRootPath, ProfileKey + ".sav");
                    if (!File.Exists(main)) { corruptLabel.text = "主文件不存在——先点 ①。"; return; }
                    recoveredProfile = null;
                    File.WriteAllBytes(main, Encoding.UTF8.GetBytes("### corrupted by demo ###"));
                    profileMainCorrupted = true;
                    corruptLabel.text = "主文件已被写坏 ✗（仅 storage-demo/profile.sav）。现在点 ③；预期 Console 精确出现 2 条 Warning。";
                }
            }, CodeRef.Here("File.WriteAllBytes(main", "演示用的搞破坏代码"));
            host.AddExperimentAsyncActionRow("③ 回退读取（预期 2 Warning）", chapterCt => RunProfileOperation(chapterCt, corruptLabel, async ct =>
            {
                if (!profileMainCorrupted)
                {
                    corruptLabel.text = "主文件尚未由本实验写坏——请先按 ① → ②；此时直接 Load 不应产生那 2 条 Warning。";
                    return;
                }

                var loaded = await storage.Load<DemoSaveData>(ProfileKey, ct); // 损坏演示：允许 provider 回退备份
                ct.ThrowIfCancellationRequested();
                if (loaded == null)
                {
                    recoveredProfile = null;
                    corruptLabel.text = "返回 null——主备都不可用（是不是没先点 ①？首写没有备份）。";
                    return;
                }

                recoveredProfile = loaded;
                current = loaded;
                RefreshState();
                corruptLabel.text = $"回退成功 ✓ 读到上一版：{loaded}。Console 应有且只有 2 条 Warning。主文件此刻仍坏，请继续点④恢复双份健康数据。";
            }), CodeRef.Here("storage.Load<DemoSaveData>(ProfileKey, ct); // 损坏演示", "读取（自动回退）"));
            host.AddExperimentAsyncActionRow("④ 连存两次（恢复健康主备份）", chapterCt => RunProfileOperation(chapterCt, corruptLabel, async ct =>
            {
                if (recoveredProfile == null)
                {
                    corruptLabel.text = "还没有可恢复的数据——请按 ① → ② → ③ 完成备份回退。";
                    return;
                }

                // 第一次覆盖会让健康数据成为主文件，但原来的坏主文件会落到 .bak；第二次再覆盖，
                // 才会把上一份健康主文件推进 .bak，重新得到主文件 + 备份两份健康数据。
                await storage.Save(ProfileKey, recoveredProfile, ct); // 第一次：重建健康主文件
                ct.ThrowIfCancellationRequested();
                DemoSaveData healthyMain = recoveredProfile;
                await storage.Save(ProfileKey, healthyMain, ct); // 第二次：把健康主文件推进备份
                ct.ThrowIfCancellationRequested();
                recoveredProfile = null;
                profileMainCorrupted = false;
                corruptLabel.text = "恢复完成 ✓ 主文件与上一版备份现在都能读取；再次 Load 不会再出现损坏 Warning。";
            }), CodeRef.Here("storage.Save(ProfileKey, recoveredProfile, ct)", "连续保存两次恢复主备份"));
#if UNITY_EDITOR
            host.AddActionRow("打开存储目录（看 .sav / .bak 文件，内容是明文 JSON）", () =>
            {
                Directory.CreateDirectory(DemoRootPath);
                UnityEditor.EditorUtility.RevealInFinder(DemoRootPath);
            }, new CodeRef("Assets/Game/Framework/Core/Storage/FileStorageProvider.cs", "private static void ReplaceAtomic(", "原子写实现"));
#endif
            host.AddNote("每个 key 至多三个文件：`<key>.sav`（主）/ `.sav.bak`（上一版备份）/ `.sav.tmp`（写入途中）。写路径「临时文件 → 原子替换 → 旧版变备份」保证任何时刻磁盘上都有一份完整可读的数据。默认序列化是带缩进的明文 JSON——`.sav` 可直接用文本编辑器打开调试；体积敏感 / 要混淆就换 serializer（见下方扩展点）。");

            // ── 版本迁移 ──
            host.AddSectionTitle("版本迁移的姿势：Version 字段 + Load 后 switch（框架不做迁移管线）");
            var migrateLabel = host.AddValueDisplay("默认 JSON 对字段增删天然宽容（新增字段旧档取默认值）——绝大多数演进免迁移；下面演示结构性改动的姿势。");
            migrateLabel.style.whiteSpace = WhiteSpace.Normal;
            host.AddAsyncActionRow("① 写入一份 v1 旧档（没有 PlayerName 语义）", chapterCt => RunProfileOperation(chapterCt, migrateLabel, async ct =>
            {
                var legacy = new DemoSaveData { Version = 1, Level = 99, PlayerName = "" };
                await storage.Save(LegacyKey, legacy, ct);
                ct.ThrowIfCancellationRequested();
                migrateLabel.text = $"已写入 v1 旧档 ✓：{legacy}";
            }));
            host.AddAsyncActionRow("② 读取 → 迁移 → 回写（MigrateIfNeeded）", chapterCt => RunProfileOperation(chapterCt, migrateLabel, async ct =>
            {
                var data = await storage.Load<DemoSaveData>(LegacyKey, ct);
                ct.ThrowIfCancellationRequested();
                if (data == null) { migrateLabel.text = "没有旧档——先点 ①。"; return; }
                bool migrated = MigrateIfNeeded(data);
                if (migrated) await storage.Save(LegacyKey, data, ct); // 迁移完回写，下次启动不再迁
                ct.ThrowIfCancellationRequested();
                migrateLabel.text = migrated
                    ? $"已迁移 ✓ v1 → v{CurrentSaveVersion} 并回写：{data}"
                    : $"无需迁移（已是 v{data.Version}）：{data}";
            }), CodeRef.Here("private static bool MigrateIfNeeded", "迁移样板（switch 链）"));
            host.AddNote("姿势固定三步：数据类型里放 `int Version` 字段 → `Load` 后 `MigrateIfNeeded` 按版本**链式** switch（v1→v2→v3 自然逐级经过）→ 迁移完 `Save` 回写。框架**刻意不提供**迁移注册表 / 管线——迁移逻辑本质是业务代码，一个 switch 最直白（ADR-0021 §4）。");

            // ── 重置本章数据 ──
            host.AddSectionTitle("复测：只重置本章自己的持久数据");
            host.AddExperimentNotice(
                "只删除白名单 key：profile、save/slot1、save/slot2、legacy；每个 key 的主文件、备份和临时文件由 Delete 一并处理。",
                "按钮可重复点击：不存在的 key 是 no-op。完成后 Exists(profile)=false，槽位列表为空。",
                "重置后从本章任一保存按钮重新开始；不会递归删除 storage-demo 目录，更不会触碰其他游戏或框架数据。");
            var resetLabel = host.AddValueDisplay("本章数据会跨 Play 保留；需要干净起点时再执行重置。");
            resetLabel.style.whiteSpace = WhiteSpace.Normal;
            host.AddExperimentAsyncActionRow("重置本章数据（仅 4 个 key）", chapterCt => RunProfileOperation(chapterCt, resetLabel, async ct =>
            {
                foreach (string key in DemoKeys)
                {
                    await storage.Delete(key, ct);
                    ct.ThrowIfCancellationRequested();
                }

                recoveredProfile = null;
                profileMainCorrupted = false;
                current = new DemoSaveData();
                RefreshState();
                resetLabel.text = "已重置 ✓ 删除 profile、save/slot1、save/slot2、legacy 的主/备/临时文件；再次点击仍是安全 no-op。";
                opLabel.text = "本章持久数据已清空；当前内存对象也已恢复初始值。";
                slotsLabel.text = "槽位已清空。";
                corruptLabel.text = "损坏实验状态已清空，可从①重新开始。";
                migrateLabel.text = "迁移实验旧档已清空，可从①重新开始。";
            }), CodeRef.Here("foreach (string key in DemoKeys)", "白名单重置"));

            // ── 扩展点与刻意不做 ──
            host.AddSectionTitle("扩展点与刻意不做");
            host.AddConcept("换介质 = IStorageProvider", "SQLite / 云存档 / PlayerPrefs 桥实现它（字节 ↔ 介质，写必须防损坏）；经 `StorageUtility` 构造注入，业务零改动。");
            host.AddConcept("换格式 = IStorageSerializer", "MemoryPack（重度存档提速）/ Newtonsoft（要 Dictionary/多态）/ 加密（包一层对称加解密）实现它（对象 ↔ 字节）。默认 JsonUtility：只认 `[Serializable]` 类的字段，不支持 Dictionary / 多态——存档类型用 List + 平铺字段建模。");
            host.AddConcept("不做散装 KV", "`GetInt/SetString` 是 PlayerPrefs 的形态，字符串 key 散落各处正是框架要消灭的；碎片标记直接用 Unity `PlayerPrefs`。");
            host.AddConcept("不做加密防篡改", "单机本地防不住（内存修改器绕过一切），联网真源在服务器；serializer 已是加密接入位。");

            host.AddTip("速记：[Serializable] 类 + 常量 key + await Save/Load；断电安全框架兜住；迁移 = Version 字段 + switch；多槽位 = key 分段 + ListKeys 前缀。深度见 framework-guide 存储章 / ADR-0021。");
        }

        // ↓ 版本迁移姿势样板（可照搬）：从旧版本逐级升到当前，每级一个 case，跨多版自然链式经过。
        private static bool MigrateIfNeeded(DemoSaveData data)
        {
            bool migrated = false;
            while (data.Version < CurrentSaveVersion)
            {
                switch (data.Version)
                {
                    case 1: // v1 → v2：引入 PlayerName——旧档没有这个语义（JSON 宽容读出为空），补默认值
                        if (string.IsNullOrEmpty(data.PlayerName)) data.PlayerName = "无名玩家";
                        data.Version = 2;
                        break;
                    default: // 未知版本（比当前还新 / 断档）：放弃迁移、保留原样，让调用方决定
                        Debug.LogError($"[StorageDemo] 未知存档版本 {data.Version}，放弃迁移。");
                        return migrated;
                }
                migrated = true;
            }
            return migrated;
        }
    }
}
