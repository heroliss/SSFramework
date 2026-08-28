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
    /// 把当前场景中的 Demo 动态字体在 PlayMode（含 TestRun）产生的 glyph / atlas 持久化限制在一次运行事务内。
    /// </summary>
    /// <remarks>
    /// PlayMode Runner 会在 <see cref="ITestRunCallback.RunStarted"/> 前加载当前场景；若等回调再拍快照，首帧生成的字形
    /// 已可能混入所谓“原始”字节。因此在 <see cref="PlayModeStateChange.ExitingEditMode"/>（场景切换前）捕获，
    /// TestRun 回调只复用该快照。TextCore 的资源更新又可能晚于单个 fixture TearDown，甚至在后续用例才落盘，
    /// 所以恢复边界必须是整轮运行回到稳定 EditMode 之后，不能按测试类名前缀过滤。恢复后仍要同时观察磁盘字节与
    /// Unity Object dirty flag：DemoScene 的文本重绘可能只改了内存对象，稍后的 Refresh / SaveAssets 才落盘。
    /// FontAsset 的材质、atlas 纹理等子资产也能单独标脏；必须检查整条 asset path 的全部对象，而不只是 main asset。
    /// 字节快照能保留测试前已落盘、但尚未提交版本控制的资产调整；捕获前仍在内存中的 dirty 修改会明确拒绝启动，
    /// 因为仅凭磁盘快照无法在恢复时区分它与本轮测试生成的数据。
    /// <c>ClearFontAssetData</c> 会误删源资产原有的 feature / atlas 基线，不能替代本守卫。
    /// </remarks>
    internal sealed class DemoDynamicFontAssetTestGuard : ITestRunCallback
    {
        private const string SnapshotFolder = "Library/SSFramework/TestSnapshots/DemoDynamicFonts";
        private const string ReadyMarker = "snapshot.ready";
        private const string CapturedBeforePlayMarker = "captured-before-play.ready";
        private const double EditorSettleSeconds = 2d;
        private const double RestoredStableSeconds = 2d;

        private static readonly string[] AssetPaths =
        {
            "Assets/Game/Framework/Demo/Res/Fonts/DemoLatin SDF.asset",
            "Assets/Game/Framework/Demo/Res/Fonts/DemoNotoSansSC SDF.asset",
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
        private static void InitializeEditorHooks()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            ScheduleRestoreIfNeeded();
        }

        public void RunStarted(ITest testsToRun)
        {
            // PlayMode 在 ExitingEditMode 已捕获干净字节；RunStarted 此时场景可能已渲染，不能覆盖那份快照。
            if (HasSnapshotCapturedBeforePlay()) return;

            // EditMode TestRun 不经过 PlayMode 状态切换，仍在这里捕获。守卫对整轮测试生效，
            // 不能按测试 FullName 判断“是否属于 Demo”。
            CaptureSnapshot();
        }

        public void RunFinished(ITestResult testResults) => ScheduleRestoreIfNeeded();

        public void TestStarted(ITest test) { }

        public void TestFinished(ITestResult result) { }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                try
                {
                    CaptureSnapshotBeforePlay();
                }
                catch (Exception exception)
                {
                    // 快照失败时不能继续进入 Play，否则 Demo 字体可能在没有原始字节证据的情况下被持久化。
                    Debug.LogException(exception);
                    EditorApplication.isPlaying = false;
                }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                // 人工 Play 没有 TestRunCallback.RunFinished，也必须走同一恢复路径。
                ScheduleRestoreIfNeeded();
            }
        }

        private static void CaptureSnapshotBeforePlay()
        {
            if (HasSnapshotCapturedBeforePlay()) return;
            CaptureSnapshot();
            File.WriteAllText(
                Path.Combine(FullPath(SnapshotFolder), CapturedBeforePlayMarker),
                DateTime.UtcNow.ToString("O"));
        }

        private static bool HasSnapshotCapturedBeforePlay()
        {
            string snapshotDirectory = FullPath(SnapshotFolder);
            return File.Exists(Path.Combine(snapshotDirectory, ReadyMarker)) &&
                   File.Exists(Path.Combine(snapshotDirectory, CapturedBeforePlayMarker));
        }

        private static void CaptureSnapshot()
        {
            string snapshotDirectory = FullPath(SnapshotFolder);
            if (Directory.Exists(snapshotDirectory))
                throw new InvalidOperationException(
                    "检测到上一轮 Demo 动态字体快照尚未恢复；请回到 EditMode 等待自动恢复后再重跑测试：" +
                    snapshotDirectory);

            ThrowIfTrackedAssetsDirtyBeforeCapture();

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
                // TextCore 可能在恢复后的 DemoScene 重绘中再次把字体对象标脏，但尚未写回磁盘。
                // 这正是本事务要丢弃的临时动态数据；清掉 dirty 并重新开始稳定窗口，避免快照删除后
                // 下一次 Assets/Refresh 才把 glyph / atlas 迟到写回源码资产。
                if (ReloadTrackedAssetsIfDirty())
                {
                    _editorReadySince = EditorApplication.timeSinceStartup;
                    return;
                }
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
                    AssetDatabase.ImportAsset(
                        AssetPaths[i],
                        ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
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

        /// <summary>
        /// 快照只能保存磁盘字节，不能保存 Unity Object 尚未落盘的序列化状态。测试启动前若已有 dirty 对象，
        /// 恢复阶段无法区分“用户编辑”与“本轮动态 glyph”，因此必须 fail-fast，不能静默替用户丢弃修改。
        /// </summary>
        internal static void ThrowIfTrackedAssetsDirtyBeforeCapture()
        {
            string[] dirtyObjects = AssetPaths
                .SelectMany(path => AssetDatabase.LoadAllAssetsAtPath(path)
                    .Where(asset => asset != null && EditorUtility.IsDirty(asset))
                    .Select(asset => $"{path} / {asset.name} ({asset.GetType().Name})"))
                .ToArray();
            if (dirtyObjects.Length == 0) return;

            throw new InvalidOperationException(
                "Demo 动态字体在测试开始前存在未保存的内存修改，已拒绝启动，避免恢复事务覆盖用户编辑。" +
                "请先在 Unity 中保存或撤销这些修改后重试：\n- " +
                string.Join("\n- ", dirtyObjects));
        }

        /// <summary>
        /// 丢弃字体主对象及其材质 / atlas 子资产尚未落盘的运行时修改。
        /// 只 ClearDirty(main asset) 不够：任一子资产保持 dirty，后续 Refresh 仍会保存整份 .asset，
        /// 连带把 main asset 内存中的 glyph table 一起写回。清标记后强制同步重导入，让内存对象也回到磁盘快照。
        /// </summary>
        private static bool ReloadTrackedAssetsIfDirty()
        {
            bool reloaded = false;
            for (int i = 0; i < AssetPaths.Length; i++)
            {
                UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(AssetPaths[i]);
                if (!assets.Any(asset => asset != null && EditorUtility.IsDirty(asset))) continue;

                foreach (UnityEngine.Object asset in assets)
                    if (asset != null && EditorUtility.IsDirty(asset))
                        EditorUtility.ClearDirty(asset);

                AssetDatabase.ImportAsset(
                    AssetPaths[i],
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                reloaded = true;
            }
            return reloaded;
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
