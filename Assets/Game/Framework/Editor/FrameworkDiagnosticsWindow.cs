using System;
using System.Collections.Generic;
using System.Linq;
using Game.Framework.Context;
using Game.Framework.Diagnostics;
using Game.Framework.Internal;
using Game.Framework.Pool;
using Game.Framework.Systems;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 「框架诊断面板」（菜单 <c>SSFramework/诊断/框架诊断面板</c>）：把散在各组件 Inspector「运行时诊断」
    /// 折叠组里的信息聚合成一屏——存活 Context 作用域树（含各容器本地注册表、事件订阅计数、池占用）、
    /// DisposableBag 存活计数、<see cref="LoggingCommandSystem"/> 命令流水。定位是调试与泄漏排查入口：
    /// 订阅数 / Bag 数只增不减、Context 切走后仍在树上，都是泄漏嫌疑（ADR-0026）。
    /// </summary>
    /// <remarks>
    /// 数据全部来自内核诊断数据面（<see cref="FrameworkDiagnostics"/> / <c>Container.LocalRegistrationDetails</c>，
    /// 经 InternalsVisibleTo 白盒读取），窗口只读不写——尤其<b>不触发工厂绑定</b>（未实例化的工厂显示为待解析，
    /// 诊断不得改变被观察系统）。约 10Hz 自动重绘（<see cref="OnInspectorUpdate"/>）。
    /// </remarks>
    public sealed class FrameworkDiagnosticsWindow : EditorWindow
    {
        [MenuItem("SSFramework/诊断/框架诊断面板", priority = 0)]
        public static void Open() => GetWindow<FrameworkDiagnosticsWindow>("框架诊断").Show();

        private const int MaxCommandRows = 64;

        private Vector2 _scroll;
        private readonly Dictionary<GameContext, bool> _foldouts = new(); // 树节点展开状态（会话级，死 Context 的条目无害）
        private readonly List<LoggingCommandSystem.Entry> _commandBuffer = new();
        private bool _showCommands = true;
        private bool _showContexts = true;

        private void OnEnable() => EditorApplication.playModeStateChanged += OnPlayModeChanged;
        private void OnDisable() => EditorApplication.playModeStateChanged -= OnPlayModeChanged;

        // 新 Play 会话开始时清掉上一局的展开状态（键是 Context 强引用，不清会让死 Context 无法 GC）。
        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode) _foldouts.Clear();
        }

        private void OnInspectorUpdate()
        {
            // ~10Hz 重绘：Play 中数据实时变化；非 Play 下登记表静止，重绘只是空转，跳过省电。
            if (EditorApplication.isPlaying) Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("框架运行时诊断 · 一屏看穿", EditorStyles.boldLabel);

            var contexts = FrameworkDiagnostics.LiveContexts;
            if (contexts.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    EditorApplication.isPlaying
                        ? "当前没有存活的 GameContext。"
                        : "进入 Play 模式后，这里展示存活 Context 作用域树、事件订阅 / Bag 计数与命令流水。",
                    MessageType.Info);
                return;
            }

            DrawGlobalCounters(contexts.Count);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            _showContexts = EditorGUILayout.Foldout(_showContexts, $"Context 作用域树（{contexts.Count} 个存活）", toggleOnLabelClick: true);
            if (_showContexts) DrawContextTree(contexts);

            EditorGUILayout.Space(6);
            _showCommands = EditorGUILayout.Foldout(_showCommands, "Command 流水（LoggingCommandSystem）", toggleOnLabelClick: true);
            if (_showCommands) DrawCommandLog();
            EditorGUILayout.EndScrollView();
        }

        // ── 全局计数 ────────────────────────────────────────────────────────

        private static void DrawGlobalCounters(int contextCount)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"存活 Context：{contextCount}", GUILayout.MinWidth(110));
                EditorGUILayout.LabelField(
                    $"DisposableBag 存活：{FrameworkDiagnostics.BagsAlive}（累计创建 {FrameworkDiagnostics.BagsCreated}）",
                    GUILayout.MinWidth(220));
                EditorGUILayout.LabelField($"命令累计：{LoggingCommandSystem.TotalRecorded}", GUILayout.MinWidth(100));
            }
        }

        // ── Context 树 ──────────────────────────────────────────────────────

        private void DrawContextTree(IReadOnlyList<GameContext> contexts)
        {
            // 容器 → Context 反查表；父级 = 沿 Container.Parent 链找到的第一个有主容器。
            // （中间可能隔着无 Context 的裸容器——测试等场景直接 new Container，跳过即可。）
            var byContainer = new Dictionary<Container, GameContext>();
            foreach (var ctx in contexts)
                byContainer[ctx.Container] = ctx;

            var children = new Dictionary<GameContext, List<GameContext>>();
            var roots = new List<GameContext>();
            foreach (var ctx in contexts)
            {
                GameContext parent = null;
                for (var p = ctx.Container.Parent; p != null; p = p.Parent)
                    if (byContainer.TryGetValue(p, out parent))
                        break;
                if (parent == null)
                {
                    roots.Add(ctx);
                }
                else
                {
                    if (!children.TryGetValue(parent, out var list))
                        children[parent] = list = new List<GameContext>();
                    list.Add(ctx);
                }
            }

            foreach (var root in roots)
                DrawContextNode(root, children, depth: 0);
        }

        private void DrawContextNode(GameContext ctx, Dictionary<GameContext, List<GameContext>> children, int depth)
        {
            _foldouts.TryGetValue(ctx, out bool expanded);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 16f);
                double alive = Time.realtimeSinceStartupAsDouble - ctx.CreatedRealtime;
                string label = $"{DisplayName(ctx)}{(ReferenceEquals(ctx, GameContext.Main) ? "  [Main]" : "")}  ·  存活 {FormatDuration(alive)}";
                _foldouts[ctx] = expanded = EditorGUILayout.Foldout(expanded, label, toggleOnLabelClick: true);
            }

            if (expanded)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUILayout.Space(2);
                    DrawRegistrations(ctx, depth);
                    DrawEventCounts(ctx, depth);
                    DrawPoolDiagnostics(ctx, depth);
                }
            }

            if (children.TryGetValue(ctx, out var kids))
                foreach (var kid in kids)
                    DrawContextNode(kid, children, depth + 1);
        }

        private static void DrawRegistrations(GameContext ctx, int depth)
        {
            var rows = ctx.Container.LocalRegistrationDetails
                .Select(d => (Text: FormatRegistration(d), d.IsOverride))
                .OrderBy(r => r.Text, StringComparer.Ordinal)
                .ToList();

            DrawIndentedHeader(depth, $"本地注册（{rows.Count}）——不含父级回退");
            if (rows.Count == 0) DrawIndentedRow(depth, "（无）");
            foreach (var row in rows)
                DrawIndentedRow(depth, row.Text);
        }

        private static string FormatRegistration((Type Contract, object Instance, bool IsOverride, bool IsPendingFactory) d)
        {
            string target = d.IsPendingFactory ? "工厂（未首次解析）"
                : d.Instance == null ? "null"
                : d.Instance.GetType().Name;
            string source = d.IsOverride ? "运行时" : "构建时";
            return $"{d.Contract.Name} → {target} · {source}";
        }

        private static void DrawEventCounts(GameContext ctx, int depth)
        {
            var counts = ctx.EventSubscriptionCounts;
            var rows = counts?.Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value)
                .ToList();

            DrawIndentedHeader(depth, $"事件订阅（{rows?.Sum(kv => kv.Value) ?? 0}）——只增不减 = 泄漏嫌疑");
            if (rows == null || rows.Count == 0) { DrawIndentedRow(depth, "（无存活订阅）"); return; }
            foreach (var kv in rows)
                DrawIndentedRow(depth, $"{kv.Key.Name} × {kv.Value}");
        }

        private static void DrawPoolDiagnostics(GameContext ctx, int depth)
        {
            // 只看本地注册的池（父级的池在父节点展示，避免整棵树重复）。不经 Resolve——不触发工厂。
            foreach (var d in ctx.Container.LocalRegistrationDetails)
            {
                if (d.Contract != typeof(IPoolUtility)) continue;
                var impl = d.Instance switch
                {
                    PoolUtility p => p,
                    MonoPoolUtility mono => mono.Impl,
                    _ => null,
                };
                if (impl == null) return;

                var pools = impl.GetPoolDiagnostics();
                DrawIndentedHeader(depth, $"对象池（{pools.Count}）");
                if (pools.Count == 0) DrawIndentedRow(depth, "（无池）");
                foreach (string line in pools)
                    DrawIndentedRow(depth, line);
                return;
            }
        }

        // ── Command 流水 ────────────────────────────────────────────────────

        private void DrawCommandLog()
        {
            if (LoggingCommandSystem.TotalRecorded == 0)
            {
                EditorGUILayout.HelpBox(
                    "未记录到命令。接入：根 Context 的 InstallBindings 里注册\n" +
                    "builder.RegisterValue(new LoggingCommandSystem(), typeof(ICommandSystem));\n" +
                    "替换默认 CommandSystem 即得全局命令流水（opt-in，不影响执行语义）。",
                    MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    $"最近 {Math.Min(MaxCommandRows, LoggingCommandSystem.Capacity)} 条（新 → 旧）· 完成时落账，在途异步不显示",
                    EditorStyles.miniLabel);
                if (GUILayout.Button("清空", GUILayout.Width(50)))
                    LoggingCommandSystem.ClearLog();
            }

            LoggingCommandSystem.CopyRecent(_commandBuffer, MaxCommandRows);
            for (int i = _commandBuffer.Count - 1; i >= 0; i--) // 新的在上，便于盯屏
            {
                var e = _commandBuffer[i];
                string line = $"帧 {e.Frame,-6} {e.CommandType}{(e.IsAsync ? " (async)" : "")}  @{e.ContextName}  {e.DurationMs:F2}ms";
                if (e.Error == null)
                    EditorGUILayout.LabelField(line, EditorStyles.label);
                else
                    EditorGUILayout.LabelField($"{line}  ✗ {e.Error}", ErrorLabelStyle);
            }
        }

        // ── 小工具 ──────────────────────────────────────────────────────────

        private static string DisplayName(GameContext ctx)
            => string.IsNullOrEmpty(ctx.DebugName) ? $"GameContext#{ctx.GetHashCode():X}" : ctx.DebugName;

        private static string FormatDuration(double seconds)
            => seconds < 60 ? $"{seconds:F0}s" : $"{(int)(seconds / 60)}m{(int)(seconds % 60)}s";

        private static void DrawIndentedHeader(int depth, string text)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 16f + 4f);
                EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel);
            }
        }

        private static void DrawIndentedRow(int depth, string text)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 16f + 16f);
                EditorGUILayout.LabelField(text, EditorStyles.miniLabel);
            }
        }

        private static GUIStyle _errorLabel;
        private static GUIStyle ErrorLabelStyle => _errorLabel ??= new GUIStyle(EditorStyles.label)
        {
            normal = { textColor = new Color(0.95f, 0.4f, 0.35f) },
            hover = { textColor = new Color(0.95f, 0.4f, 0.35f) },
        };
    }
}
