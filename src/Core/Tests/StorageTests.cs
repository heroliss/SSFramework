using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
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
        private sealed class CapturingLogSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Warning;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

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

        private sealed class CallbackStorageSerializer : IStorageSerializer
        {
            public Action OnSerialize;

            public byte[] Serialize<T>(T data) where T : class
            {
                OnSerialize?.Invoke();
                return Array.Empty<byte>();
            }

            public T Deserialize<T>(byte[] bytes) where T : class => null;
        }

        /// <summary>
        /// 把首个写操作停在半途，用于验证 StorageUtility 的 FIFO 终端释放。
        /// 默认文件 provider 的 Dispose 是 no-op，无法暴露“排队操作访问已释放连接”这一类真实 Adapter 故障。
        /// </summary>
        private sealed class LifecycleProbeProvider : IStorageProvider
        {
            public readonly List<string> Events = new();
            public readonly UniTaskCompletionSource FirstStarted = new();
            public readonly UniTaskCompletionSource FirstRelease = new();
            public readonly UniTaskCompletionSource Disposed = new();

            public Exception FirstFailure;
            public bool ThrowOnDispose;
            public int DisposeCount;
            public int WriteCount => _writeCount;

            private int _writeCount;

            public async UniTask WriteAsync(string key, byte[] bytes, CancellationToken ct)
            {
                int call = ++_writeCount;
                string label = call == 1 ? "A" : "B";
                try
                {
                    ThrowIfPhysicallyDisposed();
                    Events.Add(label + ":start");

                    if (call == 1)
                    {
                        FirstStarted.TrySetResult();
                        await FirstRelease.Task;
                        ct.ThrowIfCancellationRequested();
                        if (FirstFailure != null) throw FirstFailure;
                    }

                    ThrowIfPhysicallyDisposed();
                    Events.Add(label + ":end");
                }
                finally
                {
                    Events.Add(label + ":exit");
                }
            }

            public UniTask<byte[]> ReadAsync(string key, CancellationToken ct) => UniTask.FromResult<byte[]>(null);

            public UniTask<byte[]> ReadBackupAsync(string key, CancellationToken ct) => UniTask.FromResult<byte[]>(null);

            public bool Exists(string key)
            {
                ThrowIfPhysicallyDisposed();
                return false;
            }

            public UniTask DeleteAsync(string key, CancellationToken ct) => UniTask.CompletedTask;

            public UniTask<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct)
                => UniTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            public void Dispose()
            {
                DisposeCount++;
                Events.Add("dispose");
                Disposed.TrySetResult();
                if (ThrowOnDispose) throw new InvalidOperationException("storage-provider-dispose-probe");
            }

            private void ThrowIfPhysicallyDisposed()
            {
                if (DisposeCount != 0)
                    throw new ObjectDisposedException(nameof(LifecycleProbeProvider), "测试 Provider 已被物理释放。");
            }
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

            var sink = new CapturingLogSink();
            Log.AddSink(sink);
            SaveData loaded;
            try
            {
                loaded = await _storage.Load<SaveData>(key);
            }
            finally
            {
                Log.RemoveSink(sink);
            }
            Assert.NotNull(loaded);
            Assert.AreEqual(1, loaded.Level);
            Assert.AreEqual("v1", loaded.Name);
            LogEntry parseFailure = sink.Entries.Find(entry =>
                entry.Category == nameof(StorageUtility) && entry.Message.Contains("主文件反序列化失败"));
            Assert.IsNotNull(parseFailure.Exception,
                "损坏存档的反序列化异常必须保留在 LogEntry.Exception，不能只留易变的文本摘要");

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
            await _storage.Save("save/slot1", new SaveData()); // 主 + 备份仍只代表一个 key
            await _storage.Save("settings", new SaveData());

            var slots = await _storage.ListKeys("save/");
            CollectionAssert.AreEqual(new[] { "save/slot1", "save/slot2" }, slots); // 前缀过滤 + 稳定排序

            var all = await _storage.ListKeys();
            Assert.AreEqual(3, all.Count);
        });

        [UnityTest]
        public IEnumerator ListKeys_BackupOnly_IsDiscoverable_AndTempOnlyIsIgnored() => UniTask.ToCoroutine(async () =>
        {
            const string recoverableKey = "save/recoverable";
            await _storage.Save(recoverableKey, new SaveData { Level = 8, Name = "backup-only" });

            string main = MainPath(recoverableKey);
            File.Move(main, main + ".bak"); // 模拟手动替换在旧主文件移走后中断

            string orphanTemp = MainPath("save/orphan") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(orphanTemp));
            File.WriteAllBytes(orphanTemp, Encoding.UTF8.GetBytes("uncommitted"));

            Assert.IsTrue(_storage.Exists(recoverableKey), "仅备份存在时仍是可恢复存档");
            var keys = await _storage.ListKeys("save/");
            CollectionAssert.AreEqual(new[] { recoverableKey }, keys,
                "槽位列表必须包含仅剩备份的存档，但不能把孤立临时文件当成已提交存档");

            LogAssert.Expect(LogType.Warning, new Regex("主文件不可用，已回退上一版备份"));
            var loaded = await _storage.Load<SaveData>(recoverableKey);
            Assert.NotNull(loaded);
            Assert.AreEqual(8, loaded.Level);
            Assert.AreEqual("backup-only", loaded.Name);
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

        private static readonly string[] ValidStorageRootFolderNames =
        {
            "storage", "Storage_01", "save-data", "a", "COM10", "console", "con-save",
        };

        private static readonly string[] InvalidStorageRootFolderNames =
        {
            null, "", " ", "\t", ".", "..", "../outside", "..\\outside", "/outside", "\\outside",
            @"C:\outside", "C:outside", @"\\server\share", "nested/slot", "nested\\slot", "folder.name",
            "has space", "x:y", "name*", "name?", "名字", "control\u0001", "nul\0name",
            "CON", "con", "PRN", "AUX", "NUL", "COM1", "COM9", "LPT1", "LPT9",
            new string('a', 256),
        };

        [TestCaseSource(nameof(ValidStorageRootFolderNames))]
        public void StorageRootPath_ValidPortableName_ResolvesDirectChild(string folderName)
        {
            string rawBase = Path.Combine(Application.temporaryCachePath, "storage-root", "child", "..");
            string resolved = StorageRootPath.Resolve(rawBase, folderName);
            string normalizedBase = Path.GetFullPath(rawBase);
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            Assert.IsTrue(Path.IsPathRooted(resolved));
            Assert.AreEqual(Path.GetFullPath(Path.Combine(normalizedBase, folderName)), resolved);
            Assert.AreEqual(normalizedBase, Directory.GetParent(resolved)?.FullName,
                "解析结果必须是 persistentDataPath 的直接子目录，不能只是字符串前缀看起来相似");
            Assert.IsTrue(resolved.StartsWith(normalizedBase, comparison));
        }

        [TestCaseSource(nameof(InvalidStorageRootFolderNames))]
        public void StorageRootPath_InvalidOrNonPortableName_Throws(string folderName)
        {
            var error = Assert.Throws<ArgumentException>(() =>
                StorageRootPath.Resolve(Application.temporaryCachePath, folderName));

            Assert.AreEqual("rootFolderName", error.ParamName);
        }

        private static readonly string[] InvalidPersistentDataPaths =
        {
            null, "", "relative/base", @"C:relative", @"\rooted",
        };

        [TestCaseSource(nameof(InvalidPersistentDataPaths))]
        public void StorageRootPath_MissingOrRelativePersistentPath_Throws(string persistentDataPath)
        {
            var error = Assert.Throws<ArgumentException>(() => StorageRootPath.Resolve(persistentDataPath, "storage"));

            Assert.AreEqual("persistentDataPath", error.ParamName);
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
        public IEnumerator Dispose_WithRunningAndQueuedOperations_RejectsNewCallsAndDisposesProviderLast()
            => UniTask.ToCoroutine(async () =>
            {
                var provider = new LifecycleProbeProvider();
                var storage = new StorageUtility(provider);
                var first = storage.Save("first", new SaveData { Level = 1 });
                await provider.FirstStarted.Task;
                var second = storage.Save("second", new SaveData { Level = 2 });

                storage.Dispose();
                storage.Dispose();

                Assert.AreEqual(0, provider.DisposeCount, "已有操作尚未排空时不得提前释放 provider");
                Assert.Throws<ObjectDisposedException>(() => storage.Exists("late"), "逻辑终态必须同步可见");
                try
                {
                    await storage.Save("late", new SaveData());
                    Assert.Fail("Dispose 后的新异步请求应立即失败");
                }
                catch (ObjectDisposedException) { }

                provider.FirstRelease.TrySetResult();
                await first;
                await second;
                await provider.Disposed.Task;

                CollectionAssert.AreEqual(
                    new[] { "A:start", "A:end", "A:exit", "B:start", "B:end", "B:exit", "dispose" },
                    provider.Events);
                Assert.AreEqual(1, provider.DisposeCount, "重复 Dispose 只能安排一次物理释放");
            });

        [UnityTest]
        public IEnumerator QueuedFailure_DoesNotPoisonSuccessorOrDeferredDispose()
            => UniTask.ToCoroutine(async () =>
            {
                var provider = new LifecycleProbeProvider
                {
                    FirstFailure = new InvalidOperationException("first-write-probe"),
                };
                var storage = new StorageUtility(provider);
                var first = storage.Save("first", new SaveData());
                await provider.FirstStarted.Task;
                var second = storage.Save("second", new SaveData());
                storage.Dispose();

                Assert.AreEqual(0, provider.DisposeCount);
                CollectionAssert.AreEqual(new[] { "A:start" }, provider.Events,
                    "首个 Provider 方法尚未物理退出时，后继与 terminal 都不能提前运行");
                provider.FirstRelease.TrySetResult();

                try
                {
                    await first;
                    Assert.Fail("首个操作应保留自己的失败终态");
                }
                catch (InvalidOperationException e)
                {
                    Assert.AreEqual("first-write-probe", e.Message);
                }

                await second;
                await provider.Disposed.Task;
                CollectionAssert.AreEqual(
                    new[] { "A:start", "A:exit", "B:start", "B:end", "B:exit", "dispose" },
                    provider.Events,
                    "前驱失败不能毒化后继，也不能跳过 FIFO terminal 释放");
            });

        [UnityTest]
        public IEnumerator QueuedCancellation_DoesNotPoisonSuccessorOrDeferredDispose()
            => UniTask.ToCoroutine(async () =>
            {
                var provider = new LifecycleProbeProvider();
                var storage = new StorageUtility(provider);
                using var cancellation = new CancellationTokenSource();
                var first = storage.Save("first", new SaveData(), cancellation.Token);
                await provider.FirstStarted.Task;
                var second = storage.Save("second", new SaveData());
                storage.Dispose();
                cancellation.Cancel();

                Assert.AreEqual(0, provider.DisposeCount);
                CollectionAssert.AreEqual(new[] { "A:start" }, provider.Events,
                    "取消请求不能让 FIFO 在 Provider 方法真正退出前提前放行");
                provider.FirstRelease.TrySetResult();

                try
                {
                    await first;
                    Assert.Fail("首个操作应保留自己的取消终态");
                }
                catch (OperationCanceledException) { }

                await second;
                await provider.Disposed.Task;
                CollectionAssert.AreEqual(
                    new[] { "A:start", "A:exit", "B:start", "B:end", "B:exit", "dispose" },
                    provider.Events,
                    "前驱取消不能毒化后继，也不能跳过 FIFO terminal 释放");
            });

        [UnityTest]
        public IEnumerator ProviderDisposeFailure_IsLoggedOnce_AndStorageRemainsLogicallyDisposed()
            => UniTask.ToCoroutine(async () =>
            {
                var provider = new LifecycleProbeProvider { ThrowOnDispose = true };
                var storage = new StorageUtility(provider);
                LogAssert.Expect(LogType.Error, new Regex(@"\[StorageUtility\].*FIFO 队列排空后释放失败"));
                LogAssert.Expect(LogType.Exception, new Regex("storage-provider-dispose-probe"));

                storage.Dispose();
                storage.Dispose();
                await provider.Disposed.Task;

                Assert.AreEqual(1, provider.DisposeCount);
                Assert.Throws<ObjectDisposedException>(() => storage.Exists("late"));
            });

        [UnityTest]
        public IEnumerator Save_WhenSerializerReentersDispose_DoesNotQueueWriteAfterTerminal()
            => UniTask.ToCoroutine(async () =>
            {
                var provider = new LifecycleProbeProvider();
                var serializer = new CallbackStorageSerializer();
                StorageUtility storage = null;
                storage = new StorageUtility(provider, serializer);
                serializer.OnSerialize = storage.Dispose;

                try
                {
                    await storage.Save("reentrant", new SaveData());
                    Assert.Fail("Serializer 重入 Dispose 后，Save 必须在进入 Provider 前失败");
                }
                catch (ObjectDisposedException) { }

                await provider.Disposed.Task;
                Assert.AreEqual(0, provider.WriteCount);
                CollectionAssert.AreEqual(new[] { "dispose" }, provider.Events);
            });

        [UnityTest]
        public IEnumerator Save_NotSerializableType_LogsErrorGuard() => UniTask.ToCoroutine(async () =>
        {
            // JsonUtility 对未标 [Serializable] 的类静默产出 "{}"（数据丢失），序列化器的开发期守卫应报 error。
            LogAssert.Expect(LogType.Error, new Regex(@"\[Serializable\]"));
            await _storage.Save("bad", new NotSerializableData { X = 1 });
        });
    }
}
