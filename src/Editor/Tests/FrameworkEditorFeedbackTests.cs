using System.IO;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.TestTools;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    public sealed class FrameworkEditorFeedbackTests
    {
        [TestCase(FrameworkEditorFeedback.Level.Info, LogType.Log, "INFO", "ℹ")]
        [TestCase(FrameworkEditorFeedback.Level.Success, LogType.Log, "SUCCESS", "✓")]
        [TestCase(FrameworkEditorFeedback.Level.Warning, LogType.Warning, "WARNING", "⚠")]
        [TestCase(FrameworkEditorFeedback.Level.Failure, LogType.Error, "FAILURE", "✗")]
        public void Presentation_MapsSeverityToStableConsoleMarker(
            FrameworkEditorFeedback.Level level,
            LogType expectedLogType,
            string token,
            string icon)
        {
            var presentation = FrameworkEditorFeedback.CreatePresentation(
                "热更 Generate", level, "第一行摘要\n下一步：检查配置。");

            Assert.That(presentation.LogType, Is.EqualTo(expectedLogType));
            Assert.That(presentation.ConsoleMessage,
                Does.StartWith($"[SSFramework.Tool][{token}] 热更 Generate\n"));
            Assert.That(presentation.ConsoleMessage, Does.Contain("下一步：检查配置。"));
            Assert.That(presentation.NotificationMessage, Does.StartWith(icon + " 热更 Generate：第一行摘要"));
        }

        [Test]
        public void Presentation_TruncatesOnlyNotificationAndKeepsCopyableDetails()
        {
            string details = new string('长', 160) + "\n恢复入口：SSFramework/诊断。";

            var presentation = FrameworkEditorFeedback.CreatePresentation(
                "资源构建", FrameworkEditorFeedback.Level.Failure, details);

            Assert.That(presentation.NotificationMessage.Length, Is.LessThan(150));
            Assert.That(presentation.NotificationMessage, Does.EndWith("…"));
            Assert.That(presentation.ConsoleMessage, Does.Contain(details));
        }

        [TestCase("✓ 完成", FrameworkEditorFeedback.Level.Success)]
        [TestCase("✓ 完成\n⚠ 有一项沿用默认值", FrameworkEditorFeedback.Level.Warning)]
        [TestCase("⚠ 前置提醒\n✗ 生成失败", FrameworkEditorFeedback.Level.Failure)]
        public void SummaryMarkers_ChooseMostSevereLevel(
            string details,
            FrameworkEditorFeedback.Level expected)
        {
            FrameworkEditorFeedback.Level actual = FrameworkEditorFeedback.ResolveSummaryLevel(details);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase(FrameworkEditorFeedback.Level.Info, LogType.Log, "INFO")]
        [TestCase(FrameworkEditorFeedback.Level.Success, LogType.Log, "SUCCESS")]
        [TestCase(FrameworkEditorFeedback.Level.Warning, LogType.Warning, "WARNING")]
        [TestCase(FrameworkEditorFeedback.Level.Failure, LogType.Error, "FAILURE")]
        public void Report_EmitsOneRecordWithExpectedSeverity(
            FrameworkEditorFeedback.Level level,
            LogType expectedLogType,
            string token)
        {
            string expected = $"[SSFramework.Tool][{token}] 热更 Generate\n影响：测试反馈出口。";
            LogAssert.Expect(expectedLogType, expected);

            FrameworkEditorFeedback.Report("热更 Generate", level, "影响：测试反馈出口。");

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void FrameworkSource_HasOnlyReviewedModalInteractions()
        {
            FrameworkModuleSourceCatalog.SourceLocation sceneShortcut =
                FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                    "SceneShortcutMenu.cs", FrameworkModuleAudit.CoreAssemblyName + ".Editor");
            var confirmationWhitelist = new Dictionary<string, string>
            {
                [sceneShortcut.AssetPath] =
                    "\"正在运行 Play\",",
            };
            if (FrameworkModuleSourceCatalog.TryFindUniqueFileInAssemblySource(
                    "AssetBuildMenu.cs", "Game.Framework.Build.Editor", out var assetBuildMenu))
                confirmationWhitelist.Add(assetBuildMenu.AssetPath, "\"全量重建\",");

            foreach (string assetPath in AssetDatabase.GetAllAssetPaths()
                         .Where(path => path.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase))
                         .Where(path =>
                         {
                             string assemblyName = CompilationPipeline.GetAssemblyNameFromScriptPath(path);
                             return assemblyName != null &&
                                    (assemblyName.Equals(FrameworkModuleAudit.CoreAssemblyName,
                                         System.StringComparison.Ordinal) ||
                                     assemblyName.StartsWith(FrameworkModuleAudit.CoreAssemblyName + ".",
                                         System.StringComparison.Ordinal));
                         }))
            {
                FrameworkModuleSourceCatalog.SourceLocation sourceLocation =
                    FrameworkModuleSourceCatalog.Resolve(assetPath);
                string relative = sourceLocation.AssetPath;
                if (relative.EndsWith("FrameworkEditorFeedbackTests.cs"))
                    continue;

                string source = File.ReadAllText(sourceLocation.PhysicalPath);
                int count = CountOccurrences(source, "EditorUtility.DisplayDialog");
                Assert.That(CountOccurrences(source, "EditorUtility.DisplayDialogComplex"), Is.Zero,
                    relative + " 含有未登记的复杂模态弹窗。 ");
                Assert.That(CountOccurrences(source, ".ShowModal("), Is.Zero,
                    relative + " 含有未登记的模态窗口。 ");
                Assert.That(CountOccurrences(source, ".ShowModalUtility("), Is.Zero,
                    relative + " 含有未登记的模态工具窗口。 ");
                if (!confirmationWhitelist.TryGetValue(relative, out string requiredCall))
                {
                    Assert.That(count, Is.Zero,
                        relative + " 含有未登记的模态弹窗；普通结果会阻塞 Unity / MCP 队列。 ");
                    continue;
                }

                Assert.That(count, Is.EqualTo(1), relative + " 只允许一个已登记的真实确认框。 ");
                Assert.That(source, Does.Contain(requiredCall), relative + " 的确认框语义已改变，请重新审查白名单。 ");
            }
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }
    }
}
