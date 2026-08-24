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
        /// <summary>
        /// 创建 downloader 时统计的资源文件数量快照。为 0 表示该创建时点没有缺失内容；
        /// 若之后发生清缓存，仍以 <see cref="Download"/> 的缓存世代校验为准并重建 downloader。
        /// </summary>
        int TotalCount { get; }

        /// <summary>本次下载的总字节数，用于展示容量提示或 Wi-Fi 确认。</summary>
        long TotalBytes { get; }

        /// <summary>
        /// 下载是否已<b>成功</b>完成（没有可下载资源时也视为完成）。
        /// 失败<b>不</b>反映在此（失败仍为 false）——失败经 <see cref="Download"/> 抛异常暴露，且 downloader 一次性、重试须重建。
        /// 因此判完成优先 <c>await Download()</c> + try/catch，<b>不要</b>用 <c>while(!IsDone)</c> 轮询（失败会永远停在 false）。
        /// </summary>
        bool IsDone { get; }

        /// <summary>下载进度状态流。订阅即得当前快照，UI 不需要轮询。</summary>
        ReadOnlyReactiveProperty<DownloadProgressReport> Progress { get; }

        /// <summary>
        /// 启动下载。
        /// 同一个 downloader 反复调用只启动一次底层 operation；后续调用等待同一个 operation 的终态。
        /// <para><b>失败语义</b>：单文件失败会按下载配置（<c>FailedTryAgain</c>）自动重试若干次；最终仍失败（重试耗尽 / 远端不可达）时
        /// <b>抛异常</b>，调用方需 <c>try/catch</c>。downloader 是一次性的——失败后再调本方法会立即重抛、不会重试，
        /// 重试须<b>重建 downloader 再下</b>（已成功的分片已进缓存会被跳过，即断点续传）。
        /// <b>取消语义</b>：token 取消当前调用者的等待并抛 <see cref="System.OperationCanceledException"/>，不承诺强停共享的底层下载。
        /// 若下载还在同包维护操作后排队且已无人等待，实现可直接跳过；一旦物理下载开始，它会继续到真实终态，其他等待者不受影响。
        /// 任一次清缓存到达终态后，基于旧缓存快照创建的 downloader 会失效，必须重建再下。</para>
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
