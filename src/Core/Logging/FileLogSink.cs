using System;
using System.IO;
using UnityEngine;

namespace Game.Framework.Logging
{
    /// <summary>
    /// 零依赖文件 sink：把日志按行追加到文件，超过阈值自动按大小滚动、保留最近若干份。
    /// 覆盖「玩家包 / QA 捞日志」这个最常用的落盘需求，无需引入 ZLogger 一串依赖。
    /// </summary>
    /// <remarks>
    /// 用法：<c>Log.AddSink(new FileLogSink(Path.Combine(Application.persistentDataPath, "logs", "framework.log")))</c>；
    /// 不再需要时 <see cref="Dispose"/>（或 <c>Log.RemoveSink</c> 后 Dispose）关闭句柄。<br/>
    /// 配合 <c>Log.CaptureUnityLogs()</c> 一起开，引擎报错 / 第三方日志 / 未捕获异常也会落进本文件（最该捞到的正是它们）。<br/>
    /// <b>线程安全</b>：<see cref="Log"/> 全程持锁，可从任意线程调用（如网络后台线程）。<br/>
    /// <b>可靠性</b>：<c>AutoFlush</c> 开，崩溃前的日志不滞留缓冲；写入 / 滚动异常被吞掉并只告警一次，绝不打断业务。
    /// 定位是「够用的落盘」——不做异步批处理 / 按时间滚动 / 结构化，要那些上 ZLogger 模块（ADR-0034）。
    /// </remarks>
    public sealed class FileLogSink : ILogSink, IDisposable
    {
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly int _maxFiles;
        private readonly object _gate = new();
        private StreamWriter _writer;
        private bool _faulted;
        private bool _disposed;

        /// <param name="filePath">日志文件完整路径（父目录自动创建）。</param>
        /// <param name="minLevel">最低级别，默认 <see cref="LogLevel.Info"/>（Trace 噪音默认不落盘）。</param>
        /// <param name="maxBytes">单文件字节上限，超过即滚动，默认 5 MB。</param>
        /// <param name="maxFiles">保留的归档份数（不含当前文件），默认 3。</param>
        public FileLogSink(string filePath, LogLevel minLevel = LogLevel.Info, long maxBytes = 5 * 1024 * 1024, int maxFiles = 3)
        {
            _path = filePath;
            MinLevel = minLevel;
            _maxBytes = Math.Max(1024, maxBytes);
            _maxFiles = Mathf.Clamp(maxFiles, 0, 99);
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(_path, append: true) { AutoFlush = true };
                WriteSessionHeader();
            }
            catch (Exception e)
            {
                _faulted = true;
                Debug.LogWarning($"[FileLogSink] 打开日志文件失败，文件日志停用：{_path}\n{e}");
            }
        }

        /// <summary>
        /// 每次开档写一段会话头（设备 / 系统 / 版本 / 时间）。
        /// 日志文件是追加的，多次启动会叠在一起——没有这段分隔，拿到玩家的 log 根本分不清哪段是哪次运行，
        /// 也无从知道他用的什么机器、什么版本，而这恰恰是排查的第一步。
        /// </summary>
        /// <remarks>⚠ <c>Application</c> / <c>SystemInfo</c> 只能主线程访问；本 sink 约定在启动时（主线程）构造。</remarks>
        private void WriteSessionHeader()
        {
            _writer.WriteLine();
            _writer.WriteLine("==================== session " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====================");
            _writer.WriteLine($"app      : {Application.productName} {Application.version}  ({Application.platform})");
            _writer.WriteLine($"unity    : {Application.unityVersion}");
            _writer.WriteLine($"device   : {SystemInfo.deviceModel}");
            _writer.WriteLine($"os       : {SystemInfo.operatingSystem}");
            _writer.WriteLine($"graphics : {SystemInfo.graphicsDeviceName}");
            _writer.WriteLine($"memory   : {SystemInfo.systemMemorySize} MB");
            _writer.WriteLine("=================================================================================");
        }

        public LogLevel MinLevel { get; set; }

        public void Log(in LogEntry entry)
        {
            if (_faulted) return;

            // 在锁外格式化（纯字符串，无共享状态），锁内只做写 + 滚动，缩短临界区。
            string line = Format(in entry);
            lock (_gate)
            {
                if (_disposed || _writer == null) return;
                try
                {
                    _writer.WriteLine(line);
                    RollIfNeeded();
                }
                catch (Exception e)
                {
                    _faulted = true;
                    Debug.LogWarning($"[FileLogSink] 写日志失败，文件日志停用：{_path}\n{e}");
                }
            }
        }

        private static string Format(in LogEntry entry)
        {
            string time = entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
            string cat = entry.Category != null ? $"[{entry.Category}] " : string.Empty;
            string line = $"{time} [{entry.Level}] {cat}{entry.Message}";

            // 异常自带堆栈；没有异常时门面会给 Error 补抓一份栈（LogEntry.StackTrace）。
            // 落盘的 error 若两者皆无，事后只剩一句话、无从定位——所以这里一定要把栈带上。
            if (entry.Exception != null) return line + Environment.NewLine + entry.Exception;
            if (!string.IsNullOrEmpty(entry.StackTrace)) return line + Environment.NewLine + entry.StackTrace.TrimEnd();
            return line;
        }

        // 当前文件超阈值时滚动：删最老、其余后移一位、当前改成 .1、重开新文件。持锁调用。
        private void RollIfNeeded()
        {
            if (_writer.BaseStream.Length < _maxBytes) return;

            _writer.Flush();
            _writer.Dispose();
            _writer = null;

            try
            {
                if (_maxFiles == 0)
                {
                    // 不保留归档：直接截断重开。
                    _writer = new StreamWriter(_path, append: false) { AutoFlush = true };
                    return;
                }

                SafeDelete(Suffixed(_maxFiles));                       // 删最老
                for (int i = _maxFiles - 1; i >= 1; i--)
                    SafeMove(Suffixed(i), Suffixed(i + 1));            // .i → .(i+1)
                SafeMove(_path, Suffixed(1));                         // 当前 → .1
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FileLogSink] 日志滚动失败：{_path}\n{e}");
            }
            finally
            {
                // 无论滚动是否顺利，都要有一个可写的当前文件，避免后续全部丢日志。
                if (_writer == null)
                    _writer = new StreamWriter(_path, append: true) { AutoFlush = true };
            }
        }

        // framework.log → framework.{i}.log
        private string Suffixed(int i)
        {
            string dir = Path.GetDirectoryName(_path);
            string name = Path.GetFileNameWithoutExtension(_path);
            string ext = Path.GetExtension(_path);
            string file = $"{name}.{i}{ext}";
            return string.IsNullOrEmpty(dir) ? file : Path.Combine(dir, file);
        }

        private static void SafeDelete(string p)
        {
            if (File.Exists(p)) File.Delete(p);
        }

        private static void SafeMove(string src, string dst)
        {
            if (!File.Exists(src)) return;
            SafeDelete(dst);
            File.Move(src, dst);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _writer?.Dispose(); }
                catch { /* 关闭失败无所谓，已在退出路径 */ }
                _writer = null;
            }
        }
    }
}
