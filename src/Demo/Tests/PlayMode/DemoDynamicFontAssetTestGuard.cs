#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using NUnit.Framework.Interfaces;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestRunner;

[assembly: TestRunCallback(typeof(Game.Framework.Demo.PlayMode.Tests.DemoDynamicFontAssetTestGuard))]

namespace Game.Framework.Demo.PlayMode.Tests
{
    /// <summary>
    /// 把 Demo 动态字体在 Editor 测试中产生的 glyph / atlas 持久化限制在一次测试事务内。
    /// </summary>
    /// <remarks>
    /// TextCore 的资源更新可能晚于单个 fixture TearDown，甚至在后续用例才落盘，所以恢复边界必须是
    /// 整轮 TestRun 回到稳定 EditMode 之后。字节快照能保留测试前未提交的资产调整；
    /// <c>ClearFontAssetData</c> 会误删源资产原有的 feature / atlas 基线，不能替代本守卫。
    /// </remarks>
    internal sealed class DemoDynamicFontAssetTestGuard : ITestRunCallback
    {
        private const string SnapshotFolder = "Library/SSFramework/TestSnapshots/DemoDynamicFonts";
        private const string ReadyMarker = "snapshot.ready";
        private const double EditorSettleSeconds = 2d;
        private const double RestoredStableSeconds = 2d;

        private static readonly string[] AssetPaths =
        {
            "Assets/Game/Framework/Demo/Res/Fonts/DemoLatin SDF.asset",
            "Assets/Game/Framework/Demo/Res/Fonts/DemoNotoSansSC SDF.asset",
        };

        private static readonly string[] GuardedTestPrefixes =
        {
            "Game.Framework.Demo.Tests.",
            "Game.Framework.Demo.PlayMode.Tests.",
        };

        private static double _editorReadySince = -1d;
        private static bool _verifyingRestoredFiles;
        private static FileStamp[] _restoredStamps = Array.Empty<FileStamp>();

        private readonly struct FileStamp
        {
            internal FileStamp(long length, DateTime lastWriteUtc)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
            }

            internal long Length { get; }
            internal DateTime LastWriteUtc { get; }
        }

        [InitializeOnLoadMethod]
        private static void RecoverSnapshotAfterDomainReload() => ScheduleRestoreIfNeeded();

        public void RunStarted(ITest testsToRun)
        {
            if (!ContainsGuardedDemoTest(testsToRun)) return;
            CaptureSnapshot();
        }

        public void RunFinished(ITestResult testResults) => ScheduleRestoreIfNeeded();

        public void TestStarted(ITest test) { }

        public void TestFinished(ITestResult result) { }

        private static bool ContainsGuardedDemoTest(ITest test)
        {
            if (test == null) return false;
            if (GuardedTestPrefixes.Any(prefix =>
                    test.FullName?.StartsWith(prefix, StringComparison.Ordinal) == true))
                return true;
            return test.Tests != null && test.Tests.Any(ContainsGuardedDemoTest);
        }

        private static void CaptureSnapshot()
        {
            string snapshotDirectory = FullPath(SnapshotFolder);
            if (Directory.Exists(snapshotDirectory))
                throw new InvalidOperationException(
                    "检测到上一轮 Demo 动态字体快照尚未恢复；请回到 EditMode 等待自动恢复后再重跑测试：" +
                    snapshotDirectory);

            Directory.CreateDirectory(snapshotDirectory);
            try
            {
                for (int i = 0; i < AssetPaths.Length; i++)
                {
                    string source = FullPath(AssetPaths[i]);
                    if (!File.Exists(source))
                        throw new FileNotFoundException("Demo 动态字体资产不存在。", source);
                    File.WriteAllBytes(SnapshotPath(snapshotDirectory, i), File.ReadAllBytes(source));
                }
                File.WriteAllText(Path.Combine(snapshotDirectory, ReadyMarker), DateTime.UtcNow.ToString("O"));
            }
            catch
            {
                // RunStarted 尚未放行测试，失败时源字体还没被本轮修改；清掉不完整快照，允许修复后重试。
                if (Directory.Exists(snapshotDirectory)) Directory.Delete(snapshotDirectory, true);
                throw;
            }
        }

        private static void ScheduleRestoreIfNeeded()
        {
            string snapshotDirectory = FullPath(SnapshotFolder);
            if (!File.Exists(Path.Combine(snapshotDirectory, ReadyMarker))) return;
            _editorReadySince = -1d;
            _verifyingRestoredFiles = false;
            _restoredStamps = Array.Empty<FileStamp>();
            EditorApplication.update -= RestoreWhenEditorIsStable;
            EditorApplication.update += RestoreWhenEditorIsStable;
        }

        private static void RestoreWhenEditorIsStable()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
            {
                if (!_verifyingRestoredFiles) _editorReadySince = -1d;
                return;
            }

            if (_verifyingRestoredFiles)
            {
                if (!RestoredFileStampsMatch())
                {
                    if (!RestoreSnapshotFiles()) return;
                    CaptureRestoredFileStamps();
                    _editorReadySince = EditorApplication.timeSinceStartup;
                    return;
                }
                if (EditorApplication.timeSinceStartup - _editorReadySince < RestoredStableSeconds) return;
                if (!SnapshotMatchesAssets())
                {
                    if (!RestoreSnapshotFiles()) return;
                    CaptureRestoredFileStamps();
                    _editorReadySince = EditorApplication.timeSinceStartup;
                    return;
                }

                CompleteRestore();
                return;
            }

            if (_editorReadySince < 0d)
            {
                _editorReadySince = EditorApplication.timeSinceStartup;
                return;
            }
            if (EditorApplication.timeSinceStartup - _editorReadySince < EditorSettleSeconds) return;

            if (!RestoreSnapshotFiles()) return;
            _verifyingRestoredFiles = true;
            CaptureRestoredFileStamps();
            _editorReadySince = EditorApplication.timeSinceStartup;
        }

        private static bool RestoreSnapshotFiles()
        {
            string snapshotDirectory = FullPath(SnapshotFolder);
            try
            {
                for (int i = 0; i < AssetPaths.Length; i++)
                {
                    string snapshot = SnapshotPath(snapshotDirectory, i);
                    string destination = FullPath(AssetPaths[i]);
                    if (!File.Exists(snapshot))
                        throw new FileNotFoundException("Demo 动态字体测试快照不完整。", snapshot);

                    byte[] expected = File.ReadAllBytes(snapshot);
                    if (File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(expected))
                        continue;
                    File.WriteAllBytes(destination, expected);
                    AssetDatabase.ImportAsset(AssetPaths[i], ImportAssetOptions.ForceUpdate);
                }
                return true;
            }
            catch (Exception exception)
            {
                // 保留快照供下次 Domain Reload / Editor 重启继续恢复；不能清掉唯一的原始字节证据。
                Debug.LogException(exception);
                ScheduleRestoreIfNeeded();
                return false;
            }
        }

        private static bool SnapshotMatchesAssets()
        {
            string snapshotDirectory = FullPath(SnapshotFolder);
            try
            {
                for (int i = 0; i < AssetPaths.Length; i++)
                {
                    string snapshot = SnapshotPath(snapshotDirectory, i);
                    string destination = FullPath(AssetPaths[i]);
                    if (!File.Exists(snapshot) || !File.Exists(destination)) return false;
                    if (!File.ReadAllBytes(destination).SequenceEqual(File.ReadAllBytes(snapshot))) return false;
                }
                return true;
            }
            catch (IOException)
            {
                // TextCore 仍在写文件时先判为不稳定，下一次 update 会走可重试的恢复路径。
                return false;
            }
        }

        private static void CaptureRestoredFileStamps()
        {
            _restoredStamps = AssetPaths.Select(path =>
            {
                var file = new FileInfo(FullPath(path));
                return new FileStamp(file.Length, file.LastWriteTimeUtc);
            }).ToArray();
        }

        private static bool RestoredFileStampsMatch()
        {
            if (_restoredStamps.Length != AssetPaths.Length) return false;
            for (int i = 0; i < AssetPaths.Length; i++)
            {
                var file = new FileInfo(FullPath(AssetPaths[i]));
                if (!file.Exists || file.Length != _restoredStamps[i].Length ||
                    file.LastWriteTimeUtc != _restoredStamps[i].LastWriteUtc)
                    return false;
            }
            return true;
        }

        private static void CompleteRestore()
        {
            string snapshotDirectory = FullPath(SnapshotFolder);
            EditorApplication.update -= RestoreWhenEditorIsStable;
            _editorReadySince = -1d;
            _verifyingRestoredFiles = false;
            _restoredStamps = Array.Empty<FileStamp>();
            try
            {
                Directory.Delete(snapshotDirectory, true);
                Debug.Log("[DemoFontTestGuard] 动态字体已恢复并通过延迟写回稳定检查，本轮测试未污染工作树。");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                ScheduleRestoreIfNeeded();
            }
        }

        private static string SnapshotPath(string snapshotDirectory, int index) =>
            Path.Combine(snapshotDirectory, index + ".asset.bytes");

        private static string FullPath(string projectRelativePath) =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath) ??
                                          Directory.GetCurrentDirectory(), projectRelativePath));
    }
}
#endif
