using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    /// 本章的 <see cref="IStorageUtility"/> 经 <see cref="InstallBindings"/> 用 RegisterOwned 注册（纯 C# 服务的标准路径）。
    /// </summary>
    public sealed class StorageDemoModule : DemoModuleBase
    {
        public override string Id => "local-storage";
        public override string Title => "本地存储 · 存档";
        public override string Category => "能力";
        public override int Order => 30;
        public override string Summary =>
            "类型化整存整取的持久化：Save/Load/Exists/Delete/ListKeys；断电安全（原子写+备份回退）框架兜住；" +
            "版本迁移 = 数据里的 Version 字段 + Load 后 switch。介质与格式两个扩展点可插拔。ADR-0021。";

        // 当前存档结构版本：结构性改动时 +1，并在 MigrateIfNeeded 里补一个 case。
        private const int CurrentSaveVersion = 2;

        // key 是持久契约（落成文件名）：用常量管理、只增不改——改 key 等同丢弃旧数据。
        private const string ProfileKey = "profile";
        private const string SlotPrefix = "save/";
        private const string LegacyKey = "legacy";

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

        /// <summary>
        /// 纯 C# 服务的标准注册路径：RegisterOwned = 随 Context Dispose 自动释放（这里即退出 Play / 关闭 demo）。
        /// 挂场景节点、要 Inspector 配目录的项目用 MonoStorageUtility（同一套逻辑的 Mono 壳）。
        /// ⚠ 本方法在临时实例上被调（见 DemoModuleBase 说明），Build 要用的对象不能存字段、只能从 Context 解析。
        /// </summary>
        public override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterOwned(new StorageUtility(new FileStorageProvider(DemoRootPath)), typeof(IStorageUtility));
        }

        public override void Build(DemoModuleHost host)
        {
            var storage = this.GetUtility<IStorageUtility>();

            // ── 定位 ──
            host.AddSectionTitle("定位：类型化整存整取，防损坏由框架兜住");
            host.AddNote("每类持久数据定义一个 `[Serializable]` 类（设置 = `SettingsData`、存档 = `PlayerSaveData`），整对象 `Save` / `Load`——延续框架「用类型代替字符串」的理念，**刻意不提供** `GetInt/SetString` 散装 KV（碎片标记 Unity 的 `PlayerPrefs` 本身够用）。写路径 = 临时文件 → 原子替换 → 上一版自动备份，读路径 = 主文件损坏自动回退备份：**写一半崩溃 / 断电不丢档**，业务不再手写这类样板。",
                new CodeRef("Assets/Game/Framework/Core/Storage/IStorageUtility.cs", "public interface IStorageUtility", "存储入口契约"));
            host.AddSubNote("「key 是持久契约」：落成文件名，显式传、用常量管理、只增不改（改 key = 丢旧档，与资源 location 同一心智）。字符集限字母/数字/-/_，`/` 分段做槽位分组；非法 key 抛异常（本章 key 都是本文件顶部的 const）。");

            // ── 注册方式 ──
            host.AddSectionTitle("注册：纯 C# 服务的三选一");
            host.AddNote("本章的 `IStorageUtility` 在 `InstallBindings` 里 `RegisterOwned` 注册（随 Context Dispose 自动释放 provider，纯 C# 服务推荐路径；也正是「服务注册生成」章会替你生成的那类代码）。另两条路：全局唯一不管释放用 `RegisterValue`；要 Inspector 配根目录 / 跟随场景节点用 `MonoStorageUtility`（同一套逻辑的 Mono 壳，挂 Context 子节点即注册）。",
                CodeRef.Here("builder.RegisterOwned(new StorageUtility(_provider)", "本章的注册代码"));

            // ── 基础操作 ──
            host.AddSectionTitle("基础操作：Save / Load / Exists / Delete（原子按钮）");
            var current = new DemoSaveData();
            var stateLabel = host.AddValueDisplay("内存中的对象：" + current);
            stateLabel.style.whiteSpace = WhiteSpace.Normal;
            var opLabel = host.AddValueDisplay("对象在内存里改动 ≠ 已持久化——显式 Save 才落盘。");
            opLabel.style.whiteSpace = WhiteSpace.Normal;

            void RefreshState() => stateLabel.text = "内存中的对象：" + current;

            host.AddActionRow("改一点状态（Level+1，仅内存）", () =>
            {
                current.Level++;
                RefreshState();
                opLabel.text = "内存对象已改，但还没 Save——现在退出，改动就没了。";
            });
            host.AddActionRow("保存（Save）", async () =>
            {
                await storage.Save(ProfileKey, current);
                opLabel.text = $"已保存 ✓ key=「{ProfileKey}」。IO 失败会抛异常（磁盘满 / 权限），业务要能感知。";
            }, CodeRef.Here("storage.Save(ProfileKey, current)", "保存"));
            host.AddActionRow("读取（Load）", async () =>
            {
                var loaded = await storage.Load<DemoSaveData>(ProfileKey);
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
            }, CodeRef.Here("storage.Load<DemoSaveData>(ProfileKey)", "读取"));
            host.AddActionRow("Exists（是否有已落盘数据）", () =>
            {
                bool exists = storage.Exists(ProfileKey);
                opLabel.text = exists ? $"Exists(「{ProfileKey}」) = true ✓（主或备份任一存在）" : $"Exists(「{ProfileKey}」) = false（还没保存过，或已删除）";
            }, CodeRef.Here("storage.Exists(ProfileKey)", "存在性查询"));
            host.AddActionRow("删除（Delete，含备份）", async () =>
            {
                await storage.Delete(ProfileKey);
                opLabel.text = "已删除 ✓（主 + 备份一并删；删不存在的 key 是 no-op）。";
            }, CodeRef.Here("storage.Delete(ProfileKey)", "删除"));
            host.AddNote("失败语义与资源系统同一套：**预期内缺失给 null**（没存过 / 主备全坏——后者已打 error，业务当新档），**系统级失败抛异常**（Save 磁盘满 / key 非法 / Dispose 后调用）。全部操作内部走全局 FIFO 串行——同 key 竞态、读写交错天然消失；**别 fire-and-forget Save**（await 它，`Exists` 是不排队的同步快照）。");

            // ── 槽位 ──
            host.AddSectionTitle("多槽位：key 分段 + ListKeys 前缀列举");
            var slotsLabel = host.AddValueDisplay("槽位就是 key 的分段约定（save/slot1、save/slot2…），没有专门的槽位 API。");
            slotsLabel.style.whiteSpace = WhiteSpace.Normal;
            host.AddActionRow("保存到槽位 1（save/slot1）", async () =>
            {
                await storage.Save(SlotPrefix + "slot1", current);
                slotsLabel.text = "已保存到 save/slot1 ✓（内容 = 当前内存对象）";
            }, CodeRef.Here("storage.Save(SlotPrefix + \"slot1\"", "存槽位"));
            host.AddActionRow("保存到槽位 2（save/slot2）", async () =>
            {
                await storage.Save(SlotPrefix + "slot2", current);
                slotsLabel.text = "已保存到 save/slot2 ✓";
            });
            host.AddActionRow("列出全部槽位（ListKeys(\"save/\")）", async () =>
            {
                var keys = await storage.ListKeys(SlotPrefix);
                slotsLabel.text = keys.Count == 0
                    ? "没有任何槽位——先点上面「保存到槽位」。"
                    : $"共 {keys.Count} 个槽位：{string.Join("、", keys)}（排序稳定，直接喂存档选择 UI）";
            }, CodeRef.Here("storage.ListKeys(SlotPrefix)", "前缀列举"));

            // ── 防损坏演示 ──
            host.AddSectionTitle("防损坏：故意写坏主文件，看备份回退");
            var corruptLabel = host.AddValueDisplay("步骤：保存两次（产生上一版备份）→ 损坏主文件 → 读取 → 观察回退。");
            corruptLabel.style.whiteSpace = WhiteSpace.Normal;
            host.AddActionRow("① 连存两次（Level+1 再存，产生 .bak）", async () =>
            {
                await storage.Save(ProfileKey, current);
                current.Level++;
                RefreshState();
                await storage.Save(ProfileKey, current);
                corruptLabel.text = $"已连存两次 ✓ 磁盘上现在有主文件（Level={current.Level}）和上一版备份（Level={current.Level - 1}）。";
            });
            host.AddActionRow("② 模拟损坏主文件（直接写坏 profile.sav）", () =>
            {
                string main = Path.Combine(DemoRootPath, ProfileKey + ".sav");
                if (!File.Exists(main)) { corruptLabel.text = "主文件不存在——先点 ①。"; return; }
                File.WriteAllBytes(main, Encoding.UTF8.GetBytes("### corrupted by demo ###"));
                corruptLabel.text = "主文件已被写坏 ✗（模拟磁盘错误 / 写一半断电）。现在点 ③ 读取。";
            }, CodeRef.Here("File.WriteAllBytes(main", "演示用的搞破坏代码"));
            host.AddActionRow("③ 读取（应回退到上一版备份 + Console 有 warning）", async () =>
            {
                var loaded = await storage.Load<DemoSaveData>(ProfileKey);
                corruptLabel.text = loaded == null
                    ? "返回 null——主备都不可用（是不是没先点 ①？首写没有备份）。"
                    : $"回退成功 ✓ 读到的是上一版备份：{loaded}。看 Console：主文件反序列化失败 + 回退 warning。下次 Save 会重建主文件。";
            }, CodeRef.Here("storage.Load<DemoSaveData>(ProfileKey)", "读取（自动回退）"));
#if UNITY_EDITOR
            host.AddActionRow("打开存储目录（看 .sav / .bak 文件，内容是明文 JSON）", () =>
            {
                Directory.CreateDirectory(DemoRootPath);
                UnityEditor.EditorUtility.RevealInFinder(DemoRootPath);
            }, new CodeRef("Assets/Game/Framework/Core/Storage/FileStorageProvider.cs", "ReplaceAtomic", "原子写实现"));
#endif
            host.AddNote("每个 key 至多三个文件：`<key>.sav`（主）/ `.sav.bak`（上一版备份）/ `.sav.tmp`（写入途中）。写路径「临时文件 → 原子替换 → 旧版变备份」保证任何时刻磁盘上都有一份完整可读的数据。默认序列化是带缩进的明文 JSON——`.sav` 可直接用文本编辑器打开调试；体积敏感 / 要混淆就换 serializer（见下方扩展点）。");

            // ── 版本迁移 ──
            host.AddSectionTitle("版本迁移的姿势：Version 字段 + Load 后 switch（框架不做迁移管线）");
            var migrateLabel = host.AddValueDisplay("默认 JSON 对字段增删天然宽容（新增字段旧档取默认值）——绝大多数演进免迁移；下面演示结构性改动的姿势。");
            migrateLabel.style.whiteSpace = WhiteSpace.Normal;
            host.AddActionRow("① 写入一份 v1 旧档（没有 PlayerName 语义）", async () =>
            {
                var legacy = new DemoSaveData { Version = 1, Level = 99, PlayerName = "" };
                await storage.Save(LegacyKey, legacy);
                migrateLabel.text = $"已写入 v1 旧档 ✓：{legacy}";
            });
            host.AddActionRow("② 读取 → 迁移 → 回写（MigrateIfNeeded）", async () =>
            {
                var data = await storage.Load<DemoSaveData>(LegacyKey);
                if (data == null) { migrateLabel.text = "没有旧档——先点 ①。"; return; }
                bool migrated = MigrateIfNeeded(data);
                if (migrated) await storage.Save(LegacyKey, data); // 迁移完回写，下次启动不再迁
                migrateLabel.text = migrated
                    ? $"已迁移 ✓ v1 → v{CurrentSaveVersion} 并回写：{data}"
                    : $"无需迁移（已是 v{data.Version}）：{data}";
            }, CodeRef.Here("private static bool MigrateIfNeeded", "迁移样板（switch 链）"));
            host.AddNote("姿势固定三步：数据类型里放 `int Version` 字段 → `Load` 后 `MigrateIfNeeded` 按版本**链式** switch（v1→v2→v3 自然逐级经过）→ 迁移完 `Save` 回写。框架**刻意不提供**迁移注册表 / 管线——迁移逻辑本质是业务代码，一个 switch 最直白（ADR-0021 §4）。");

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
