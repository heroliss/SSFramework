using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Game.Framework.Storage;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证本地存储（ADR-0021）：Save/Load 往返、缺失 → null、原子写 + 备份回退（防损坏是模块核心价值）、
    /// 删除、槽位列举、key 校验、全局 FIFO 串行、Dispose 后 fail-fast。
    /// </summary>
    public class StorageTests
    {
        [Serializable]
        private class SaveData
        {
            public int Level;
            public string Name;
            public List<int> Items = new List<int>();
        }

        // 未标 [Serializable]：JsonUtility 会静默产出空对象，序列化器在 Editor/Dev 下应 LogError 抓住。
        private class NotSerializableData
        {
            public int X;
        }

        private string _root;
        private StorageUtility _storage;

        [SetUp]
        public void SetUp()
        {
            // 每个用例独立临时根目录：互不污染、TearDown 一把删。
            _root = Path.Combine(Application.temporaryCachePath, "storage-tests", Guid.NewGuid().ToString("N"));
            _storage = new StorageUtility(new FileStorageProvider(_root));
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                _storage?.Dispose();
            }
            finally
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
        }

        private string MainPath(string key) => Path.Combine(_root, key + ".sav");

        [UnityTest]
        public IEnumerator SaveThenLoad_RoundtripsAllFields() => UniTask.ToCoroutine(async () =>
        {
            var data = new SaveData { Level = 7, Name = "hero", Items = new List<int> { 1, 2, 3 } };
            await _storage.Save("player", data);

            var loaded = await _storage.Load<SaveData>("player");
            Assert.NotNull(loaded);
            Assert.AreEqual(7, loaded.Level);
            Assert.AreEqual("hero", loaded.Name);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, loaded.Items);
        });

        [UnityTest]
        public IEnumerator Load_MissingKey_ReturnsNull_NoLogs() => UniTask.ToCoroutine(async () =>
        {
            var loaded = await _storage.Load<SaveData>("never-saved");
            Assert.IsNull(loaded); // 新玩家没有存档是常态：null、无任何日志
        });

        [UnityTest]
        public IEnumerator Exists_ReflectsSaveAndDelete() => UniTask.ToCoroutine(async () =>
        {
            Assert.IsFalse(_storage.Exists("settings"));

            await _storage.Save("settings", new SaveData { Level = 1 });
            Assert.IsTrue(_storage.Exists("settings"));

            await _storage.Delete("settings");
            Assert.IsFalse(_storage.Exists("settings"));

            await _storage.Delete("settings"); // 删不存在的 = no-op，不抛
        });

        [UnityTest]
        public IEnumerator Overwrite_CorruptMain_FallsBackToPreviousBackup() => UniTask.ToCoroutine(async () =>
        {
            const string key = "player";
            await _storage.Save(key, new SaveData { Level = 1, Name = "v1" });
            await _storage.Save(key, new SaveData { Level = 2, Name = "v2" });

            // 第二次写走原子替换：上一版应自动变 .bak。
            Assert.IsTrue(File.Exists(MainPath(key) + ".bak"), "覆盖写后应存在上一版备份");

            // 模拟主文件损坏（写坏字节），Load 应回退到备份（= 上一版 v1）并打 warning。
            File.WriteAllBytes(MainPath(key), Encoding.UTF8.GetBytes("corrupted###"));
            LogAssert.Expect(LogType.Warning, new Regex("主文件反序列化失败"));
            LogAssert.Expect(LogType.Warning, new Regex("回退上一版备份"));

            var loaded = await _storage.Load<SaveData>(key);
            Assert.NotNull(loaded);
            Assert.AreEqual(1, loaded.Level);
            Assert.AreEqual("v1", loaded.Name);

            // 只 Save 一次虽然会重建主文件，却会把原来的坏主文件推进 .bak；连续保存两次，
            // 再次损坏主文件后仍能从备份读回，才证明主文件与备份都已恢复健康。
            await _storage.Save(key, loaded);
            await _storage.Save(key, loaded);
            File.WriteAllBytes(MainPath(key), Encoding.UTF8.GetBytes("corrupted-again###"));
            LogAssert.Expect(LogType.Warning, new Regex("主文件反序列化失败"));
            LogAssert.Expect(LogType.Warning, new Regex("回退上一版备份"));

            var recoveredAgain = await _storage.Load<SaveData>(key);
            Assert.NotNull(recoveredAgain, "连续保存两次后，备份也必须恢复为可读数据");
            Assert.AreEqual(1, recoveredAgain.Level);
            Assert.AreEqual("v1", recoveredAgain.Name);
        });

        [UnityTest]
        public IEnumerator DeleteKnownDemoKeys_IsIdempotent_AndLeavesUnrelatedData() => UniTask.ToCoroutine(async () =>
        {
            string[] demoKeys = { "profile", "save/slot1", "save/slot2", "legacy" };
            foreach (string key in demoKeys)
                await _storage.Save(key, new SaveData { Level = 1 });
            await _storage.Save("unrelated/settings", new SaveData { Level = 9 });

            foreach (string key in demoKeys) await _storage.Delete(key);
            foreach (string key in demoKeys) await _storage.Delete(key); // 重复重置仍应是 no-op

            foreach (string key in demoKeys)
                Assert.IsFalse(_storage.Exists(key), $"已知 demo key 应被重置：{key}");
            Assert.IsTrue(_storage.Exists("unrelated/settings"), "白名单重置不得删除未列出的持久数据");
        });

        [UnityTest]
        public IEnumerator BothCorruptOrMissing_ReturnsNullAndLogsError() => UniTask.ToCoroutine(async () =>
        {
            const string key = "player";
            await _storage.Save(key, new SaveData { Level = 1 }); // 首写：只有主文件，无备份

            File.WriteAllBytes(MainPath(key), Encoding.UTF8.GetBytes("corrupted###"));
            LogAssert.Expect(LogType.Warning, new Regex("主文件反序列化失败"));
            LogAssert.Expect(LogType.Error, new Regex("均无法反序列化"));

            var loaded = await _storage.Load<SaveData>(key);
            Assert.IsNull(loaded); // 曾有内容但全坏：null + error（业务当新档处理）
        });

        [UnityTest]
        public IEnumerator ListKeys_FiltersByPrefix_SortedStable() => UniTask.ToCoroutine(async () =>
        {
            await _storage.Save("save/slot2", new SaveData());
            await _storage.Save("save/slot1", new SaveData());
            await _storage.Save("settings", new SaveData());

            var slots = await _storage.ListKeys("save/");
            CollectionAssert.AreEqual(new[] { "save/slot1", "save/slot2" }, slots); // 前缀过滤 + 稳定排序

            var all = await _storage.ListKeys();
            Assert.AreEqual(3, all.Count);
        });

        [Test]
        public void InvalidKeys_ThrowArgumentException()
        {
            // Exists 与异步 API 共用同一处 StorageKey.Validate，这里用同步入口逐条验证规则。
            Assert.Throws<ArgumentException>(() => _storage.Exists(null));
            Assert.Throws<ArgumentException>(() => _storage.Exists(""));
            Assert.Throws<ArgumentException>(() => _storage.Exists("/lead"));
            Assert.Throws<ArgumentException>(() => _storage.Exists("trail/"));
            Assert.Throws<ArgumentException>(() => _storage.Exists("a//b"));
            Assert.Throws<ArgumentException>(() => _storage.Exists("has space"));
            Assert.Throws<ArgumentException>(() => _storage.Exists("dot.name"));   // 字符集排除 '.'（防 .. 逃逸与扩展名混淆）
            Assert.Throws<ArgumentException>(() => _storage.Exists("back\\slash"));

            Assert.DoesNotThrow(() => _storage.Exists("Save-2/slot_01")); // 合法：字母/数字/-/_ + '/' 分段
        }

        [UnityTest]
        public IEnumerator Save_NullData_Throws() => UniTask.ToCoroutine(async () =>
        {
            try
            {
                await _storage.Save<SaveData>("player", null);
                Assert.Fail("Save(null) 应抛 ArgumentNullException");
            }
            catch (ArgumentNullException) { /* 预期 */ }
        });

        [UnityTest]
        public IEnumerator FifoQueue_UnawaitedSaves_LastWins() => UniTask.ToCoroutine(async () =>
        {
            const string key = "player";
            // 三次未逐个 await 的 Save 进同一 FIFO：串行落盘、后者覆盖前者，不交错写坏文件。
            var s1 = _storage.Save(key, new SaveData { Level = 1 });
            var s2 = _storage.Save(key, new SaveData { Level = 2 });
            var s3 = _storage.Save(key, new SaveData { Level = 3 });
            await UniTask.WhenAll(s1, s2, s3);

            var loaded = await _storage.Load<SaveData>(key);
            Assert.NotNull(loaded);
            Assert.AreEqual(3, loaded.Level);
        });

        [Test]
        public void Dispose_ThenUse_ThrowsObjectDisposed()
        {
            var storage = new StorageUtility(new FileStorageProvider(Path.Combine(_root, "disposed")));
            storage.Dispose();
            Assert.Throws<ObjectDisposedException>(() => storage.Exists("any")); // 写丢失必须 fail-fast，不学池的宽容警告
        }

        [UnityTest]
        public IEnumerator Save_NotSerializableType_LogsErrorGuard() => UniTask.ToCoroutine(async () =>
        {
            // JsonUtility 对未标 [Serializable] 的类静默产出 "{}"（数据丢失），序列化器的开发期守卫应报 error。
            LogAssert.Expect(LogType.Error, new Regex(@"\[Serializable\]"));
            await _storage.Save("bad", new NotSerializableData { X = 1 });
        });
    }
}
