using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 保存“哪些 Prefab 根上有 <see cref="UIBindingData"/>”的 Editor 会话索引。第一次需要完整证据时扫描一次；
    /// 后续由 <see cref="AssetPostprocessor"/> 检查变化 Prefab 及其 Variant 依赖闭包，并把快照放进
    /// <see cref="SessionState"/> 跨脚本域重载复用。
    /// </summary>
    /// <remarks>
    /// 索引只记录候选 Prefab 路径，不缓存绑定条目或生成目标。输出 claim 真正采集时仍重新加载这些候选，
    /// 因而 Profile、目录覆盖与 Prefab 内容使用当前值；索引缺失时只有真实 claim 采集才回退到一次完整扫描，
    /// 窗口预览只消费已有快照，不能把不完整缓存当作跨 Module 写盘的安全证据。
    /// </remarks>
    internal static class UIBindingPrefabCatalog
    {
        private const int SnapshotVersion = 2;
        private const string SessionKey =
            "Game.Framework.UI.UGui.Editor.UIBindingPrefabCatalog.v2";

        private static HashSet<string> _paths;
        private static Dictionary<string, string> _basePrefabByVariant;
        private static IReadOnlyList<string> _orderedPaths;
        private static bool _initialized;

        /// <summary>当前 Editor 进程实际执行完整 Prefab 扫描的次数；用于性能契约测试与诊断。</summary>
        internal static int FullScanCount { get; private set; }

        /// <summary>
        /// 返回稳定排序的完整候选快照。内存与 Session 都没有证据时会完整扫描一次；之后同一 Editor 会话
        /// 跨脚本域重载继续复用，工程资产变化则由增量导入回调维护。
        /// </summary>
        internal static IReadOnlyList<string> GetPaths()
        {
            if (!_initialized && !TryRestoreSessionSnapshot()) Refresh();
            return _orderedPaths;
        }

        /// <summary>只读取已有内存或 Session 快照；不会为窗口绘制触发完整工程扫描。</summary>
        internal static bool TryGetPaths(out IReadOnlyList<string> paths)
        {
            if (!_initialized && !TryRestoreSessionSnapshot())
            {
                paths = Array.Empty<string>();
                return false;
            }

            paths = _orderedPaths;
            return true;
        }

        /// <summary>显式重扫所有 Prefab，并在扫描成功后原子替换会话索引。</summary>
        internal static void Refresh()
        {
            string[] prefabPaths = AssetDatabase.FindAssets("t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(IsPrefabPath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var discovered = new HashSet<string>(StringComparer.Ordinal);
            var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in prefabPaths)
            {
                InspectPrefab(path, out bool hasBindingData, out string basePrefabPath);
                if (hasBindingData) discovered.Add(path);
                if (!string.IsNullOrEmpty(basePrefabPath)) dependencies[path] = basePrefabPath;
            }

            Replace(discovered, dependencies, persist: true);
            FullScanCount++;
        }

        /// <summary>
        /// AssetPostprocessor 的增量入口。若本会话尚无完整快照则不在导入回调里偷偷补扫；第一次读取仍由
        /// <see cref="GetPaths"/> 建立完整基线。
        /// </summary>
        internal static void ApplyAssetChanges(
            IEnumerable<string> importedAssets,
            IEnumerable<string> deletedAssets,
            IEnumerable<string> movedAssets,
            IEnumerable<string> movedFromAssetPaths)
        {
            string[] imported = PrefabPaths(importedAssets);
            string[] deleted = PrefabPaths(deletedAssets);
            string[] moved = PrefabPaths(movedAssets);
            string[] movedFrom = PrefabPaths(movedFromAssetPaths);
            if (imported.Length == 0 && deleted.Length == 0 && moved.Length == 0 && movedFrom.Length == 0)
                return;

            if (!_initialized && !TryRestoreSessionSnapshot()) return;

            bool changed = false;
            string[] removed = deleted.Concat(movedFrom).Distinct(StringComparer.Ordinal).ToArray();
            foreach (string path in removed)
            {
                changed |= _paths.Remove(path);
                changed |= _basePrefabByVariant.Remove(path);
            }

            string[] updated = imported.Concat(moved).Distinct(StringComparer.Ordinal).ToArray();
            foreach (string path in updated) changed |= UpdatePrefabRecord(path);

            // Unity 通常会把受影响 Variant 一起报为 imported，但该行为不是索引正确性的契约。
            // 依赖图让“回调里只有基 Prefab”也能重验全部后代；逐层快照避免遍历中修改字典。
            var pending = new Queue<string>(removed.Concat(updated).Distinct(StringComparer.Ordinal));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                string basePath = pending.Dequeue();
                if (!visited.Add(basePath)) continue;
                string[] variants = _basePrefabByVariant
                    .Where(pair => string.Equals(pair.Value, basePath, StringComparison.Ordinal))
                    .Select(pair => pair.Key)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                foreach (string variantPath in variants)
                {
                    changed |= UpdatePrefabRecord(variantPath);
                    pending.Enqueue(variantPath);
                }
            }

            if (changed) PublishSnapshot(persist: true);
        }

        /// <summary>丢弃内存与 Session 快照；用于显式刷新失败后避免展示跨批次的混合证据。</summary>
        internal static void Invalidate()
        {
            _paths = null;
            _basePrefabByVariant = null;
            _orderedPaths = null;
            _initialized = false;
            SessionState.EraseString(SessionKey);
        }

        /// <summary>模拟脚本域重载时只丢弃静态字段，保留 SessionState；仅供白盒测试。</summary>
        internal static void ForgetMemoryForTests()
        {
            _paths = null;
            _basePrefabByVariant = null;
            _orderedPaths = null;
            _initialized = false;
        }

        /// <summary>替换候选集合但保留 Variant 依赖图；用于模拟增量回调到达前的陈旧候选快照。</summary>
        internal static void ReplaceCandidatePathsForTests(IEnumerable<string> paths)
        {
            if (!_initialized && !TryRestoreSessionSnapshot())
                throw new InvalidOperationException("测试替换候选前必须先建立完整 Prefab 索引。");
            _paths = new HashSet<string>(paths ?? Array.Empty<string>(), StringComparer.Ordinal);
            PublishSnapshot(persist: false);
        }

        private static bool TryRestoreSessionSnapshot()
        {
            string json = SessionState.GetString(SessionKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                SessionSnapshot snapshot = JsonUtility.FromJson<SessionSnapshot>(json);
                if (snapshot == null || snapshot.Version != SnapshotVersion || snapshot.Paths == null ||
                    snapshot.VariantPaths == null || snapshot.BasePrefabPaths == null ||
                    snapshot.VariantPaths.Length != snapshot.BasePrefabPaths.Length)
                    return false;
                var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < snapshot.VariantPaths.Length; i++)
                {
                    string variantPath = snapshot.VariantPaths[i];
                    string basePath = snapshot.BasePrefabPaths[i];
                    if (!IsPrefabPath(variantPath) || !IsPrefabPath(basePath)) continue;
                    dependencies[variantPath] = basePath;
                }
                Replace(snapshot.Paths.Where(IsPrefabPath), dependencies, persist: false);
                return true;
            }
            catch (Exception)
            {
                SessionState.EraseString(SessionKey);
                return false;
            }
        }

        private static void Replace(
            IEnumerable<string> paths,
            IReadOnlyDictionary<string, string> dependencies,
            bool persist)
        {
            _paths = new HashSet<string>(paths ?? Array.Empty<string>(), StringComparer.Ordinal);
            _basePrefabByVariant = dependencies == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(dependencies, StringComparer.Ordinal);
            _initialized = true;
            PublishSnapshot(persist);
        }

        private static void PublishSnapshot(bool persist)
        {
            _orderedPaths = Array.AsReadOnly(_paths.OrderBy(path => path, StringComparer.Ordinal).ToArray());
            if (!persist) return;

            var snapshot = new SessionSnapshot
            {
                Version = SnapshotVersion,
                Paths = _orderedPaths.ToArray(),
                VariantPaths = _basePrefabByVariant.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            };
            snapshot.BasePrefabPaths = snapshot.VariantPaths
                .Select(path => _basePrefabByVariant[path])
                .ToArray();
            SessionState.SetString(SessionKey, JsonUtility.ToJson(snapshot));
        }

        private static bool UpdatePrefabRecord(string prefabPath)
        {
            bool changed = false;
            InspectPrefab(prefabPath, out bool hasBindingData, out string basePrefabPath);
            if (hasBindingData) changed |= _paths.Add(prefabPath);
            else changed |= _paths.Remove(prefabPath);

            if (string.IsNullOrEmpty(basePrefabPath))
                changed |= _basePrefabByVariant.Remove(prefabPath);
            else if (!_basePrefabByVariant.TryGetValue(prefabPath, out string existingBasePath) ||
                     !string.Equals(existingBasePath, basePrefabPath, StringComparison.Ordinal))
            {
                _basePrefabByVariant[prefabPath] = basePrefabPath;
                changed = true;
            }

            return changed;
        }

        private static string[] PrefabPaths(IEnumerable<string> paths) => (paths ?? Array.Empty<string>())
            .Where(IsPrefabPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        private static bool IsPrefabPath(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        private static void InspectPrefab(
            string prefabPath,
            out bool hasBindingData,
            out string basePrefabPath)
        {
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            hasBindingData = root != null && root.GetComponent<UIBindingData>() != null;
            basePrefabPath = string.Empty;
            if (root == null || PrefabUtility.GetPrefabAssetType(root) != PrefabAssetType.Variant) return;

            UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(root);
            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (IsPrefabPath(sourcePath) && !string.Equals(sourcePath, prefabPath, StringComparison.Ordinal))
                basePrefabPath = sourcePath;
        }

        [Serializable]
        private sealed class SessionSnapshot
        {
            public int Version;
            public string[] Paths;
            public string[] VariantPaths;
            public string[] BasePrefabPaths;
        }
    }

    /// <summary>只把发生变化的 Prefab 交给 UI Binding 候选索引，不在普通工程变化上全量重扫。</summary>
    internal sealed class UIBindingPrefabAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths) =>
            UIBindingPrefabCatalog.ApplyAssetChanges(
                importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
    }
}
