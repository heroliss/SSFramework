using System;
using System.Diagnostics;
using UnityEditor;
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

        static FrameworkModuleAuditCache()
        {
            EditorApplication.projectChanged += Invalidate;
            CompilationPipeline.compilationStarted += _ => Invalidate();
            EditorUserBuildSettings.activeBuildTargetChanged += Invalidate;
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
            _current = new Entry(
                snapshot,
                result,
                report,
                DateTime.UtcNow,
                stopwatch.Elapsed.TotalSeconds,
                captureTimings,
                analysisStopwatch.Elapsed.TotalSeconds,
                reportStopwatch.Elapsed.TotalSeconds);
            return _current;
        }

        internal static void Invalidate()
        {
            _current = null;
            Invalidated?.Invoke();
        }
    }
}
