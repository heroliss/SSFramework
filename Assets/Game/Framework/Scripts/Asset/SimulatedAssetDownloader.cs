#if UNITY_EDITOR
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using R3;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// Editor-only downloader used by EditorSimulate mode when all assets are already local.
    /// It lets UI flows observe a real progress stream without adding demo-only delays.
    ///
    /// 由「模拟大小 + 速度」驱动：时长 = 大小 / 速度。把模拟大小作为 <see cref="DownloadProgressReport.TotalBytes"/>
    /// 暴露、并按进度推进 <c>CurrentBytes</c>，让 UI 能显示总大小 / 已下载 / 实时速度，而不只是一个 0→1 的空进度。
    /// </summary>
    internal sealed class SimulatedAssetDownloader : IAssetDownloader
    {
        private readonly long _totalBytes;
        private readonly long _bytesPerSecond;
        private readonly ReactiveProperty<DownloadProgressReport> _progress;
        private UniTaskCompletionSource _tcs;

        /// <param name="totalBytes">模拟总大小（字节）。</param>
        /// <param name="bytesPerSecond">模拟下载速度（字节/秒）。时长 = 大小 / 速度。</param>
        internal SimulatedAssetDownloader(long totalBytes, long bytesPerSecond)
        {
            _totalBytes = Math.Max(0, totalBytes);
            _bytesPerSecond = Math.Max(1, bytesPerSecond);
            _progress = new ReactiveProperty<DownloadProgressReport>(
                new DownloadProgressReport(0f, 1, 0, _totalBytes, 0));
        }

        public int TotalCount => 1;
        public long TotalBytes => _totalBytes;
        public bool IsDone => _progress.Value.Progress >= 1f;
        public bool IsSimulated => true;
        public ReadOnlyReactiveProperty<DownloadProgressReport> Progress => _progress;

        public UniTask Download(CancellationToken ct = default)
        {
            if (_tcs != null) return _tcs.Task;

            _tcs = new UniTaskCompletionSource();
            RunAsync(ct).Forget();
            return _tcs.Task;
        }

        private async UniTaskVoid RunAsync(CancellationToken ct)
        {
            try
            {
                // 固定时间间隔推进（50ms ≈ 20 次/秒）：刷新节奏恒定、平滑度与总时长无关；
                // 不像「固定步数」那样——时长短时步进太碎、时长长时每步等很久显得卡。步数自动 = 时长 ÷ 间隔。
                const float tickSec = 0.05f;
                int tickMs = (int)(tickSec * 1000f);
                float durationSec = _totalBytes / (float)_bytesPerSecond;
                float elapsed = 0f;
                while (elapsed < durationSec)
                {
                    await UniTask.Delay(tickMs, cancellationToken: ct);
                    elapsed += tickSec;
                    float p = Mathf.Clamp01(elapsed / durationSec);
                    _progress.Value = new DownloadProgressReport(p, 1, p >= 1f ? 1 : 0, _totalBytes, (long)(_totalBytes * p));
                }
                // 收尾精确到 100% / 全字节（浮点累加可能差一点点）。
                _progress.Value = new DownloadProgressReport(1f, 1, 1, _totalBytes, _totalBytes);

                _tcs.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                _tcs.TrySetException(ex);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }
}
#endif
