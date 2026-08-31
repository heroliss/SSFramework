using System;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 当前 Editor 会话中的 Framework Module 审计证据缓存。重型采集只在使用者或机器入口明确请求时执行，
    /// 工程资产、Package、构建场景、目标平台或编译图变化后立即失效；窗口打开只读取现有快照，
    /// 不隐式触发扫描。
    /// </summary>
    [InitializeOnLoad]
    internal static class FrameworkModuleAuditCache
    {
        internal sealed class Entry
        {
            internal readonly FrameworkModuleAudit.Snapshot Snapshot;
            internal readonly FrameworkModuleAudit.AuditResult Result;
            internal readonly string Report;
            internal readonly DateTime CapturedUtc;
            internal readonly double DurationSeconds;
            internal readonly FrameworkModuleAudit.CaptureTimings CaptureTimings;
            internal readonly double AnalysisSeconds;
            internal readonly double ReportSeconds;

            internal Entry(
                FrameworkModuleAudit.Snapshot snapshot,
                FrameworkModuleAudit.AuditResult result,
                string report,
                DateTime capturedUtc,
                double durationSeconds,
                FrameworkModuleAudit.CaptureTimings captureTimings,
                double analysisSeconds,
                double reportSeconds)
            {
                Snapshot = snapshot;
                Result = result;
                Report = report ?? string.Empty;
                CapturedUtc = capturedUtc;
                DurationSeconds = durationSeconds;
                CaptureTimings = captureTimings;
                AnalysisSeconds = analysisSeconds;
                ReportSeconds = reportSeconds;
            }
        }

        private static Entry _current;

        internal static event Action Invalidated;
        internal static event Action<Entry> Refreshed;

        static FrameworkModuleAuditCache()
        {
            EditorApplication.projectChanged += Invalidate;
            CompilationPipeline.compilationStarted += _ => Invalidate();
            EditorBuildSettings.sceneListChanged += Invalidate;
            UnityEditor.PackageManager.Events.registeredPackages += _ => Invalidate();
        }

        internal static bool TryGet(out Entry entry)
        {
            entry = _current;
            return entry != null;
        }

        internal static Entry GetOrRefresh() => _current ?? Refresh();

        internal static Entry Refresh(Action<string, float> progress = null)
        {
            // 显式刷新失败后不能继续把旧证据当作当前结果。调用方仍可持有此前 Entry 供只读展示，
            // 但缓存入口必须回到未采集状态，让下一次请求真正重试。
            _current = null;
            NotifyInvalidated();
            var stopwatch = Stopwatch.StartNew();
            FrameworkModuleAudit.Snapshot snapshot = FrameworkModuleAudit.Capture(
                out FrameworkModuleAudit.CaptureTimings captureTimings,
                (phase, value) => progress?.Invoke("采集 · " + phase, value * 0.86f));

            progress?.Invoke("分析依赖与删除边界", 0.89f);
            var analysisStopwatch = Stopwatch.StartNew();
            FrameworkModuleAudit.AuditResult result = FrameworkModuleAudit.Analyze(snapshot);
            analysisStopwatch.Stop();

            progress?.Invoke("生成可复制报告", 0.96f);
            var reportStopwatch = Stopwatch.StartNew();
            string report = FrameworkModuleAudit.CreateReport(result);
            reportStopwatch.Stop();
            stopwatch.Stop();
            progress?.Invoke("审计完成", 1f);
            var refreshed = new Entry(
                snapshot,
                result,
                report,
                DateTime.UtcNow,
                stopwatch.Elapsed.TotalSeconds,
                captureTimings,
                analysisStopwatch.Elapsed.TotalSeconds,
                reportStopwatch.Elapsed.TotalSeconds);
            _current = refreshed;
            NotifyRefreshed(refreshed);
            return refreshed;
        }

        internal static void Invalidate()
        {
            _current = null;
            NotifyInvalidated();
        }

        private static void NotifyInvalidated()
        {
            Delegate[] handlers = Invalidated?.GetInvocationList();
            if (handlers == null) return;
            foreach (Action handler in handlers)
            {
                try
                {
                    handler();
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogError(
                        $"[FrameworkModuleAuditCache] 证据失效观察者异常：{exception}");
                }
            }
        }

        private static void NotifyRefreshed(Entry entry)
        {
            Delegate[] handlers = Refreshed?.GetInvocationList();
            if (handlers == null) return;
            foreach (Action<Entry> handler in handlers)
            {
                try
                {
                    handler(entry);
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogError(
                        $"[FrameworkModuleAuditCache] 证据刷新观察者异常：{exception}");
                }
            }
        }
    }

    /// <summary>使用 Unity 6 当前回调接收构建目标切换，避免依赖已废弃的静态事件。</summary>
    internal sealed class FrameworkModuleAuditBuildTargetWatcher : IActiveBuildTargetChanged
    {
        public int callbackOrder => 0;

        public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget) =>
            FrameworkModuleAuditCache.Invalidate();
    }
}
