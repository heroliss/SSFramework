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
    /// </summary>
    internal sealed class SimulatedAssetDownloader : IAssetDownloader
    {
        private readonly float _duration;
        private readonly ReactiveProperty<DownloadProgressReport> _progress;
        private UniTaskCompletionSource _tcs;

        internal SimulatedAssetDownloader(float duration)
        {
            _duration = duration;
            _progress = new ReactiveProperty<DownloadProgressReport>(
                new DownloadProgressReport(0f, 0, 0, 0, 0));
        }

        public int TotalCount => 0;
        public long TotalBytes => 0;
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
                const int steps = 20;
                int delayMs = Mathf.Max(16, (int)(_duration * 1000f / steps));
                for (int i = 1; i <= steps; i++)
                {
                    await UniTask.Delay(delayMs, cancellationToken: ct);
                    _progress.Value = new DownloadProgressReport(i / (float)steps, 0, 0, 0, 0);
                }

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
