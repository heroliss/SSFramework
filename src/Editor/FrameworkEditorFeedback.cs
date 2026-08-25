using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 框架编辑器工具的非阻塞结果出口。普通成功、校验失败、缺配置与 PlayMode 拦截只写一条有稳定状态标记的
    /// Console 记录，并在当前窗口短暂提示；不会打开会阻塞 Unity / MCP 队列的模态结果弹窗。
    /// 真正会删除缓存、覆盖文件或需要用户选择的操作仍应直接使用确认对话框。
    /// </summary>
    public static class FrameworkEditorFeedback
    {
        /// <summary>一次 Editor 工具操作对使用者的最终影响；决定 Console 严重级别、稳定状态标记和通知停留时间。</summary>
        public enum Level
        {
            /// <summary>没有修改也没有异常，例如“当前没有可取消的标记”。</summary>
            Info,

            /// <summary>操作按预期完成，产物或配置已处于可继续使用的状态。</summary>
            Success,

            /// <summary>操作未启动、只完成部分非关键步骤，或结果需要使用者留意；不等同于失败。</summary>
            Warning,

            /// <summary>操作失败或产物不可继续使用；Console 以 Error 记录。</summary>
            Failure,
        }

        internal readonly struct Presentation
        {
            internal readonly LogType LogType;
            internal readonly string ConsoleMessage;
            internal readonly string NotificationMessage;
            internal readonly double NotificationSeconds;

            internal Presentation(
                LogType logType,
                string consoleMessage,
                string notificationMessage,
                double notificationSeconds)
            {
                LogType = logType;
                ConsoleMessage = consoleMessage;
                NotificationMessage = notificationMessage;
                NotificationSeconds = notificationSeconds;
            }
        }

        /// <summary>
        /// 报告一次已结束或被前置条件阻止的工具操作。<paramref name="operation"/> 应是稳定、简短的中文动作名，
        /// <paramref name="details"/> 保留完整原因、影响与下一步，便于人和 AI 从 Console 复制排查。
        /// </summary>
        public static void Report(string operation, Level level, string details, UnityEngine.Object context = null)
        {
            Presentation presentation = CreatePresentation(operation, level, details);
            switch (presentation.LogType)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    Debug.LogError(presentation.ConsoleMessage, context);
                    break;
                case LogType.Warning:
                    Debug.LogWarning(presentation.ConsoleMessage, context);
                    break;
                default:
                    Debug.Log(presentation.ConsoleMessage, context);
                    break;
            }

            EditorWindow target = EditorWindow.focusedWindow ?? SceneView.lastActiveSceneView;
            target?.ShowNotification(
                new GUIContent(presentation.NotificationMessage),
                presentation.NotificationSeconds);
        }

        /// <summary>把二态操作结果映射为 <see cref="Level.Success"/> 或 <see cref="Level.Failure"/>。</summary>
        public static void ReportResult(string operation, bool success, string details, UnityEngine.Object context = null) =>
            Report(operation, success ? Level.Success : Level.Failure, details, context);

        /// <summary>
        /// 适配既有生成器的多行摘要契约：包含 <c>✗</c> 视为失败，只有 <c>⚠</c> 视为带提醒完成，
        /// 其余视为成功。新 API 更推荐直接传明确级别。
        /// </summary>
        public static void ReportSummary(string operation, string details, UnityEngine.Object context = null)
            => Report(operation, ResolveSummaryLevel(details), details, context);

        internal static Level ResolveSummaryLevel(string details) => details?.Contains("✗") == true
            ? Level.Failure
            : details?.Contains("⚠") == true
                ? Level.Warning
                : Level.Success;

        /// <summary>报告一次需要处理或留意、但不应制造红色失败噪音的结果。</summary>
        public static void Warn(string operation, string details, UnityEngine.Object context = null) =>
            Report(operation, Level.Warning, details, context);

        /// <summary>报告一次没有副作用、无需修复的说明性结果。</summary>
        public static void Info(string operation, string details, UnityEngine.Object context = null) =>
            Report(operation, Level.Info, details, context);

        internal static Presentation CreatePresentation(string operation, Level level, string details)
        {
            string normalizedOperation = string.IsNullOrWhiteSpace(operation) ? "未命名操作" : operation.Trim();
            string normalizedDetails = string.IsNullOrWhiteSpace(details) ? "（没有附加详情）" : details.Trim();
            string token = level switch
            {
                Level.Success => "SUCCESS",
                Level.Warning => "WARNING",
                Level.Failure => "FAILURE",
                _ => "INFO",
            };
            string icon = level switch
            {
                Level.Success => "✓",
                Level.Warning => "⚠",
                Level.Failure => "✗",
                _ => "ℹ",
            };
            LogType logType = level switch
            {
                Level.Warning => LogType.Warning,
                Level.Failure => LogType.Error,
                _ => LogType.Log,
            };
            double seconds = level switch
            {
                Level.Failure => 6d,
                Level.Warning => 5d,
                _ => 3d,
            };
            string firstLine = normalizedDetails
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0) ?? "查看 Console 详情";
            if (firstLine.Length > 120)
                firstLine = firstLine.Substring(0, 117) + "…";

            return new Presentation(
                logType,
                $"[SSFramework.Tool][{token}] {normalizedOperation}\n{normalizedDetails}",
                $"{icon} {normalizedOperation}：{firstLine}",
                seconds);
        }
    }
}
