using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game.Framework.Context;
using Game.Framework.Internal;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 把 Mono Context 的逐宿主失败快照还原成“最先失败的 Context → 被它级联影响的 Context”。
    /// 分析只读快照，不触发 Initialize / Resolve，也不把失败宿主伪装成可用 GameContext。
    /// </summary>
    internal static class MonoContextIssueAnalysis
    {
        internal sealed class Candidate
        {
            internal readonly MonoGameContextBase Host;
            internal readonly MonoContextDiagnosticSnapshot Snapshot;
            internal readonly string Path;

            internal Candidate(
                MonoGameContextBase host,
                MonoContextDiagnosticSnapshot snapshot,
                string path = null)
            {
                Host = host;
                Snapshot = snapshot;
                Path = path ?? HierarchyPath(host);
            }
        }

        internal sealed class Group
        {
            internal readonly Candidate Origin;
            internal readonly Exception RootCause;
            internal readonly IReadOnlyList<Candidate> Affected;
            internal readonly bool HasParentCycle;
            internal bool IsTimingConcern => RootCause == null;

            internal Group(
                Candidate origin,
                Exception rootCause,
                IReadOnlyList<Candidate> affected,
                bool hasParentCycle)
            {
                Origin = origin;
                RootCause = rootCause;
                Affected = affected;
                HasParentCycle = hasParentCycle;
            }
        }

        internal static IReadOnlyList<Group> Analyze(
            IReadOnlyList<MonoGameContextBase> hosts,
            bool editorIsPlaying)
        {
            var candidates = hosts
                .Where(host => ShouldReport(host, editorIsPlaying))
                .Select(host => new Candidate(host, host.DiagnosticSnapshot))
                .OrderBy(candidate => candidate.Path, StringComparer.Ordinal)
                .ToList();
            return GroupCandidates(candidates);
        }

        /// <summary>
        /// 有异常时，同一最深异常对象只在彼此确有 Mono 父子链时合并，避免两个独立 Context 恰好抛出
        /// 同文案异常时误判成级联。没有异常的宿主只按实际父子链组成“时序提醒”，不计入异常根因。
        /// </summary>
        internal static IReadOnlyList<Group> GroupCandidates(IReadOnlyList<Candidate> candidates)
        {
            var byHostId = candidates.ToDictionary(candidate => candidate.Host.GetInstanceID());
            var causesByHostId = candidates.ToDictionary(
                candidate => candidate.Host.GetInstanceID(),
                candidate => DeepestCause(candidate.Snapshot.Failure));
            var adjacent = candidates.ToDictionary(
                candidate => candidate.Host.GetInstanceID(),
                _ => new HashSet<int>());
            var parentInSameCauseChain = new Dictionary<int, int>();

            foreach (Candidate candidate in candidates)
            {
                int childId = candidate.Host.GetInstanceID();
                if (candidate.Snapshot.ResolvedParent is not MonoGameContextBase monoParent || monoParent == null)
                    continue;

                int parentId = monoParent.GetInstanceID();
                if (!byHostId.ContainsKey(parentId) ||
                    !BelongsToSameIssueChain(causesByHostId[childId], causesByHostId[parentId]))
                    continue;

                adjacent[childId].Add(parentId);
                adjacent[parentId].Add(childId);
                parentInSameCauseChain[childId] = parentId;
            }

            var groups = new List<Group>();
            var unvisited = new HashSet<int>(byHostId.Keys);
            foreach (Candidate start in candidates.OrderBy(candidate => candidate.Path, StringComparer.Ordinal))
            {
                int startId = start.Host.GetInstanceID();
                if (!unvisited.Remove(startId)) continue;

                var componentIds = new HashSet<int> { startId };
                var queue = new Queue<int>();
                queue.Enqueue(startId);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    foreach (int neighbor in adjacent[current])
                    {
                        if (!unvisited.Remove(neighbor)) continue;
                        componentIds.Add(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }

                List<Candidate> affected = componentIds
                    .Select(id => byHostId[id])
                    .OrderBy(candidate => candidate.Path, StringComparer.Ordinal)
                    .ToList();
                List<Candidate> roots = affected
                    .Where(candidate => !parentInSameCauseChain.ContainsKey(candidate.Host.GetInstanceID()))
                    .ToList();
                bool hasParentCycle = roots.Count == 0;
                Candidate origin = hasParentCycle ? affected[0] : roots[0];
                groups.Add(new Group(
                    origin,
                    causesByHostId[origin.Host.GetInstanceID()],
                    affected,
                    hasParentCycle));
            }

            return groups.OrderBy(group => group.Origin.Path, StringComparer.Ordinal).ToList();
        }

        private static bool BelongsToSameIssueChain(Exception childCause, Exception parentCause)
        {
            if (childCause == null || parentCause == null)
                return childCause == null && parentCause == null;
            return ReferenceEquals(childCause, parentCause);
        }

        internal static bool ShouldReport(MonoGameContextBase host, bool editorIsPlaying)
        {
            if (host == null) return false;

            MonoContextDiagnosticState state = host.DiagnosticSnapshot.State;
            if (state == MonoContextDiagnosticState.Failed) return true;
            if (!editorIsPlaying || !host.gameObject.activeInHierarchy) return false;
            return state is MonoContextDiagnosticState.Uninitialized or MonoContextDiagnosticState.Initializing;
        }

        internal static string EvidenceLabel(bool editorIsPlaying) => editorIsPlaying
            ? "当前 Play：正在影响本次运行"
            : "历史证据：来自上次运行，当前没有在执行";

        /// <summary>面向使用者显示中文状态，同时保留真实枚举成员名，便于检索代码和日志。</summary>
        internal static string StateLabel(MonoContextDiagnosticState state) => state switch
        {
            MonoContextDiagnosticState.Uninitialized => "未初始化（Uninitialized）",
            MonoContextDiagnosticState.Initializing => "初始化中（Initializing）",
            MonoContextDiagnosticState.Ready => "就绪（Ready）",
            MonoContextDiagnosticState.Failed => "失败（Failed）",
            MonoContextDiagnosticState.Disposed => "已释放（Disposed）",
            _ => state.ToString(),
        };

        internal static string BuildCopyReport(Group group, bool editorIsPlaying)
        {
            var report = new StringBuilder(512);
            report.AppendLine("[SSFramework.Diagnostics][MonoContext]");
            report.AppendLine("证据：" + EvidenceLabel(editorIsPlaying));
            string originLabel = group.HasParentCycle
                ? "优先定位（父级链存在循环）"
                : group.IsTimingConcern ? "最上游未就绪" : "最先失败";
            report.AppendLine(originLabel + "：" + group.Origin.Path);
            report.AppendLine((group.IsTimingConcern ? "时序提醒：" : "首要根因：") +
                              CauseSummary(group.RootCause));
            if (group.HasParentCycle)
                report.AppendLine("父级链：检测到循环，无法推断唯一的最先失败宿主；请先修正 Parent Context 配置。");
            report.AppendLine(group.IsTimingConcern
                ? $"影响：{group.Affected.Count} 个 Context（同组是同一条父子未就绪链，不代表已经抛出异常）"
                : $"影响：{group.Affected.Count} 个 Context（同组通常是一次父级失败的级联，不是独立根因）");
            report.AppendLine("受影响链：");
            foreach (Candidate candidate in group.Affected)
            {
                report.Append("- ").Append(candidate.Path)
                    .Append(" [").Append(StateLabel(candidate.Snapshot.State)).Append("]")
                    .Append("；父级：").AppendLine(DescribeParent(candidate.Snapshot.ResolvedParent));
            }

            if (group.RootCause != null)
            {
                report.AppendLine("完整根因异常：");
                report.Append(group.RootCause);
            }
            return report.ToString().TrimEnd();
        }

        /// <summary>窗口增量刷新签名必须包含展示依赖的路径、父级拓扑、分组边界与完整异常，不能只看宿主数量。</summary>
        internal static string BuildSignature(IReadOnlyList<Group> groups, bool editorIsPlaying)
        {
            var signature = new StringBuilder(256).Append(editorIsPlaying ? "play;" : "history;");
            foreach (Group group in groups)
            {
                signature.Append("group:").Append(group.Origin.Host.GetInstanceID()).Append(':')
                    .Append(group.Origin.Path).Append(':')
                    .Append(group.HasParentCycle).Append(':')
                    .Append(group.IsTimingConcern).Append(':')
                    .Append(group.RootCause?.ToString()).Append(';');
                foreach (Candidate candidate in group.Affected)
                {
                    signature.Append("host:").Append(candidate.Host.GetInstanceID()).Append(':')
                        .Append(candidate.Path).Append(':')
                        .Append((int)candidate.Snapshot.State).Append(':')
                        .Append(DescribeParent(candidate.Snapshot.ResolvedParent)).Append(':')
                        .Append(candidate.Snapshot.Failure?.ToString()).Append(';');
                }
            }
            return signature.ToString();
        }

        internal static string CauseSummary(Exception rootCause) => rootCause == null
            ? "尚未完成初始化；若持续超过一帧，请检查对象激活状态与 Awake 时序。"
            : $"{rootCause.GetType().Name}: {rootCause.Message}";

        internal static string DescribeParent(IGameContext parent)
        {
            if (parent == null) return "（无）";
            if (parent is UnityEngine.Object unityParent && unityParent == null)
                return "（对象已销毁）";
            if (parent is MonoGameContextBase monoParent && monoParent != null)
                return HierarchyPath(monoParent);
            if (parent is GameContext gameParent)
                return string.IsNullOrEmpty(gameParent.DebugName)
                    ? $"GameContext#{gameParent.GetHashCode():X}"
                    : gameParent.DebugName;
            return parent.GetType().Name;
        }

        internal static string HierarchyPath(MonoGameContextBase mono)
        {
            if (mono == null) return "（对象已销毁）";
            var names = new List<string>();
            for (Transform current = mono.transform; current != null; current = current.parent)
                names.Add(current.name);
            names.Reverse();
            return string.Join("/", names);
        }

        internal static Exception DeepestCause(Exception exception)
        {
            if (exception == null) return null;
            while (exception.InnerException != null)
                exception = exception.InnerException;
            return exception;
        }
    }
}
