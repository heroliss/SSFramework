using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;

namespace Game.Framework.Storage
{
    /// <summary>
    /// 默认存储 provider：根目录下的文件读写（每个 key 一个 <c>.sav</c> 文件，key 的 <c>/</c> 分段映射子目录），
    /// 写路径走「临时文件 → 原子替换 → 上一版自动变 <c>.bak</c>」——任何时刻磁盘上都有一份完整可读的数据，
    /// 写一半崩溃 / 断电不丢档。这是存储模块替业务兜住的核心价值（ADR-0021 §3）。
    /// </summary>
    /// <remarks>
    /// 每个 key 至多三个文件：<c>&lt;key&gt;.sav</c>（主）/ <c>&lt;key&gt;.sav.bak</c>（上一版备份）/ <c>&lt;key&gt;.sav.tmp</c>（写入途中，残留会被下次写覆盖）。
    /// 读损坏的回退决策在 <see cref="StorageUtility"/>（它才知道字节能否反序列化），本类只提供主 / 备两个读取通道。
    /// 文件 IO 一律切线程池执行（大存档写盘不卡帧），完成后回主线程返回（UniTask 默认行为）。
    /// key 已由 utility 层按 <see cref="StorageKey"/> 校验；直接使用本类型（不经 utility）时需自行保证 key 合法。
    /// 本类无长期持有的句柄，<see cref="Dispose"/> 为 no-op（接口上的 IDisposable 是给 SQLite 这类有连接的后端留的）。
    /// </remarks>
    public sealed class FileStorageProvider : IStorageProvider
    {
        private const string FileExtension = ".sav";
        private const string BackupSuffix = ".bak";
        private const string TempSuffix = ".tmp";

        private readonly string _root;

        /// <summary>实际读写的根目录（绝对路径），诊断显示用。</summary>
        public string RootPath => _root;

        /// <param name="rootPath">存储根目录的<b>绝对路径</b>。常规用法传 <c>Path.Combine(Application.persistentDataPath, "storage")</c>
        /// （<see cref="StorageUtility"/> 无参构造即此默认）；测试传临时目录实现隔离。</param>
        public FileStorageProvider(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("存储根目录不能为空。", nameof(rootPath));
            _root = rootPath;
        }

        public async UniTask WriteAsync(string key, byte[] bytes, CancellationToken ct)
        {
            string main = MainPath(key);
            await UniTask.RunOnThreadPool(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(main));
                string tmp = main + TempSuffix;
                File.WriteAllBytes(tmp, bytes);
                ReplaceAtomic(tmp, main, main + BackupSuffix);
            }, cancellationToken: ct);
        }

        public UniTask<byte[]> ReadAsync(string key, CancellationToken ct)
            => ReadFileAsync(MainPath(key), ct);

        public UniTask<byte[]> ReadBackupAsync(string key, CancellationToken ct)
            => ReadFileAsync(MainPath(key) + BackupSuffix, ct);

        public bool Exists(string key)
        {
            string main = MainPath(key);
            return File.Exists(main) || File.Exists(main + BackupSuffix);
        }

        public async UniTask DeleteAsync(string key, CancellationToken ct)
        {
            string main = MainPath(key);
            await UniTask.RunOnThreadPool(() =>
            {
                // 主 + 备份 + 残留临时文件一并删；File.Delete 对不存在的路径本就是 no-op。
                File.Delete(main);
                File.Delete(main + BackupSuffix);
                File.Delete(main + TempSuffix);
            }, cancellationToken: ct);
        }

        public async UniTask<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct)
        {
            return await UniTask.RunOnThreadPool<IReadOnlyList<string>>(() =>
            {
                if (!Directory.Exists(_root)) return Array.Empty<string>();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                // 手动替换在「旧主文件移到 .bak、临时文件尚未转正」之间崩溃时，可能只剩可恢复的备份。
                // 因此主文件与备份都证明 key 存在；.tmp 只是未提交写入，不能单独暴露成存档位。
                foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!TryGetKey(file, out string key)) continue;
                    if (prefix == null || key.StartsWith(prefix, StringComparison.Ordinal))
                        keys.Add(key);
                }

                var sortedKeys = new List<string>(keys);
                sortedKeys.Sort(StringComparer.Ordinal); // 稳定顺序，槽位列表 UI 不抖动
                return sortedKeys;
            }, cancellationToken: ct);
        }

        public void Dispose() { /* 无长期句柄可释放（见类型 remarks） */ }

        private string MainPath(string key) => Path.Combine(_root, key + FileExtension);

        private bool TryGetKey(string file, out string key)
        {
            string relative = file.Substring(_root.Length).TrimStart('/', '\\');
            int suffixLength;
            if (relative.EndsWith(FileExtension + BackupSuffix, StringComparison.Ordinal))
                suffixLength = FileExtension.Length + BackupSuffix.Length;
            else if (relative.EndsWith(FileExtension, StringComparison.Ordinal))
                suffixLength = FileExtension.Length;
            else
            {
                key = null;
                return false;
            }

            key = relative.Substring(0, relative.Length - suffixLength).Replace('\\', '/');
            return key.Length > 0;
        }

        // 原子替换：File.Replace 一步完成「tmp 转正 + 旧主文件变 bak」；个别平台不支持时退化为手动三步——
        // 顺序保证中途崩溃最多丢一层备份，主数据链（tmp 或 main 至少一个完整）不断。
        private static void ReplaceAtomic(string tmp, string main, string bak)
        {
            if (!File.Exists(main))
            {
                File.Move(tmp, main); // 首写：没有旧版可备份
                return;
            }
            try
            {
                File.Replace(tmp, main, bak);
            }
            catch (Exception e) when (e is PlatformNotSupportedException || e is IOException)
            {
                Log.Warning($"File.Replace 不可用（{e.GetType().Name}），退化为手动替换：{main}", "FileStorageProvider");
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(main, bak);
                File.Move(tmp, main);
            }
        }

        private static async UniTask<byte[]> ReadFileAsync(string path, CancellationToken ct)
        {
            return await UniTask.RunOnThreadPool(() =>
            {
                try
                {
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // 读失败按「不可用」处理（返回 null 由上层走备份回退），但打日志留痕——IO 错误不该无声。
                    Log.Warning($"读文件失败 {path}：{e.Message}", "FileStorageProvider");
                    return null;
                }
            }, cancellationToken: ct);
        }
    }
}
