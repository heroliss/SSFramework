using System.Threading;
using Cysharp.Threading.Tasks;
using R3;

namespace Game.Framework
{
    /// <summary>
    /// 下载任务抽象。
    /// 业务只关心总量、进度流和启动下载，不直接依赖底层资源库的下载操作。
    /// 进度用 <see cref="ReadOnlyReactiveProperty{T}"/> 暴露，订阅时立即拿到当前快照（R3 内置行为）。
    /// </summary>
    public interface IAssetDownloader
    {
        /// <summary>本次下载需要处理的资源文件数量。为 0 表示没有缺失内容，可直接进入业务流程。</summary>
        int TotalCount { get; }

        /// <summary>本次下载的总字节数，用于展示容量提示或 Wi-Fi 确认。</summary>
        long TotalBytes { get; }

        /// <summary>下载是否已经完成。没有可下载资源时也视为完成。</summary>
        bool IsDone { get; }

        /// <summary>下载进度状态流。订阅即得当前快照，UI 不需要轮询。</summary>
        ReadOnlyReactiveProperty<DownloadProgressReport> Progress { get; }

        /// <summary>
        /// 启动下载。
        /// 同一个 downloader 反复调用只启动一次底层 operation；后续调用等待同一个 operation 的终态。
        /// <para><b>失败语义</b>：单文件失败会按下载配置（<c>FailedTryAgain</c>）自动重试若干次；最终仍失败（重试耗尽 / 远端不可达）时
        /// <b>抛异常</b>，调用方需 <c>try/catch</c>。downloader 是一次性的——失败后再调本方法会立即重抛、不会重试，
        /// 重试须<b>重建 downloader 再下</b>（已成功的分片已进缓存会被跳过，即断点续传）。
        /// 取消 token 触发时取消底层下载并抛 <see cref="System.OperationCanceledException"/>。</para>
        /// </summary>
        UniTask Download(CancellationToken ct = default);
    }

    /// <summary>
    /// 下载进度快照。
    /// 用不可变值对象承载一次进度变化，避免 View 订阅时直接碰底层资源库的回调数据结构。
    /// </summary>
    public readonly struct DownloadProgressReport
    {
        public readonly float Progress;
        public readonly int TotalCount;
        public readonly int CurrentCount;
        public readonly long TotalBytes;
        public readonly long CurrentBytes;

        public DownloadProgressReport(
            float progress,
            int totalCount,
            int currentCount,
            long totalBytes,
            long currentBytes)
        {
            Progress = progress;
            TotalCount = totalCount;
            CurrentCount = currentCount;
            TotalBytes = totalBytes;
            CurrentBytes = currentBytes;
        }

        /// <summary>总大小（MB），保留两位小数。</summary>
        public string TotalSizeMB => (TotalBytes / 1048576f).ToString("F2");

        /// <summary>已下载大小（MB），保留两位小数。</summary>
        public string CurrentSizeMB => (CurrentBytes / 1048576f).ToString("F2");

        /// <summary>是否已下载完成。总数为 0 时返回 false，避免空任务被误判为完成。</summary>
        public bool IsDone => TotalCount > 0 && CurrentCount >= TotalCount;

        public override string ToString() => $"{CurrentCount}/{TotalCount} ({CurrentSizeMB}/{TotalSizeMB}MB) {Progress:P0}";
    }
}
