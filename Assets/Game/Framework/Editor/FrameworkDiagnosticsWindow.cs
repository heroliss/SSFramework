using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Game.Framework.Context;
using Game.Framework.Diagnostics;
using Game.Framework.Logging;
using Game.Framework.Pool;
using Game.Framework.Systems;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 「运行时诊断」（菜单 <c>SSFramework/诊断与分析/运行时诊断</c>）：调试器风格的运行时总览——
    /// 左侧存活 Context 作用域树（搜索过滤、双击定位场景对象），右侧选中 Context 的明细
    /// （本地注册表 / 事件订阅计数 / 池借出），底部 <see cref="LoggingCommandSystem"/> 命令流水表格
    /// （过滤 / 仅错误 / 复制导出），顶栏全局计数带趋势 sparkline。定位是调试与泄漏排查入口：
    /// 订阅数 / Bag 数只增不减、Context 切走后仍在树上，都是泄漏嫌疑（ADR-0026）。
    /// </summary>
    /// <remarks>
    /// 数据全部来自内核诊断数据面（<see cref="FrameworkDiagnostics"/> / <c>Container.LocalRegistrationDetails</c>，
    /// 经 InternalsVisibleTo 白盒读取），窗口只读不写——尤其<b>不触发工厂绑定</b>（未实例化的工厂显示为待解析，
    /// 诊断不得改变被观察系统）。UI Toolkit 实现（TreeView / MultiColumnListView），每 500ms 增量刷新：
    /// 结构没变只重绑可见行，树的展开状态与选中不丢。
    /// </remarks>
    public sealed class FrameworkDiagnosticsWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.RuntimeDiagnostics, priority = 80)]
        public static void Open() => GetWindow<FrameworkDiagnosticsWindow>("框架诊断").Show();

        private const int RefreshMs = 500;
        private const string AutoRefreshKey = "SSFramework.Diag.AutoRefresh";
        private const string OnlyErrorsKey = "SSFramework.Diag.OnlyErrors";

        /// <summary>
        /// 编辑器窗口不保证被停靠在宽区域：窄浮窗仍应保留完整操作路径，而不是依赖水平滚动去找关键状态。
        /// 三档只控制信息密度与分栏方向，诊断数据本身始终不丢失。
        /// </summary>
        internal enum LayoutMode
        {
            Compact,
            Medium,
            Wide,
        }

        internal enum CommandColumnId
        {
            Time,
            Frame,
            Mode,
            Command,
            Context,
            Duration,
            Status,
        }

        // ── 主题色（深浅 skin 通用的中饱和度底 + 白字）────────────────────────
        private static readonly Color ColMain = new(0.23f, 0.51f, 0.29f);     // [Main]
        private static readonly Color ColFallback = new(0.35f, 0.42f, 0.55f); // →Main 回退
        private static readonly Color ColMono = new(0.28f, 0.45f, 0.60f);     // 场景 Mono Context
        private static readonly Color ColPure = new(0.45f, 0.38f, 0.58f);     // 纯 C# Context
        private static readonly Color ColRuntime = new(0.62f, 0.45f, 0.18f);  // 运行时注册
        private static readonly Color ColBuild = new(0.36f, 0.36f, 0.40f);    // 构建时绑定
        private static readonly Color ColFactory = new(0.48f, 0.35f, 0.62f);  // 工厂未解析
        private static readonly Color ColAsync = new(0.25f, 0.45f, 0.65f);
        private static readonly Color ColError = new(0.78f, 0.30f, 0.26f);
        private static readonly Color ColOk = new(0.30f, 0.55f, 0.32f);
        private static readonly Color ColWarnDur = new(0.82f, 0.60f, 0.18f);  // ≥ 1 帧（16.7ms）
        private static readonly Color ColBadDur = new(0.85f, 0.33f, 0.28f);   // ≥ 100ms
        private static readonly Color ColMuted = new(0.55f, 0.55f, 0.55f);

        // ── UI 引用 ─────────────────────────────────────────────────────────
        private TreeView _tree;
        private HelpBox _treeHint;
        private ScrollView _monoIssueScroll;
        private VisualElement _monoIssuePanel;
        private VisualElement _treePane;
        private ScrollView _detail;
        private Label _detailAliveLabel;
        private VisualElement _toolbarActions, _toolbarSearchRow;
        private ToolbarSearchField _treeSearchField;
        private VisualElement _counterStrip;
        private readonly List<Label> _counterSeparators = new();
        private VisualElement _loggingStrip, _loggingGlobalRow, _loggingSinkRow;
        private Label _loggingSeparator;
        private TwoPaneSplitView _mainSplit, _contextSplit;
        private VisualElement _commandPane;
        private Toolbar _commandToolbarPrimary, _commandToolbarSearchRow;
        private ToolbarSearchField _commandSearchField;
        private MultiColumnListView _commandTable;
        private Column _timeColumn, _frameColumn, _modeColumn, _commandColumn, _contextColumn, _durationColumn, _statusColumn;
        private HelpBox _commandHint;
        private TextField _commandDetail;
        private Label _ctxCountLabel, _bagCountLabel, _cmdCountLabel;
        private Sparkline _ctxSpark, _bagSpark;
        private EnumField _minLevelField;
        private Toggle _captureToggle;
        private VisualElement _sinkContainer;
        private string _sinkSignature;
        private readonly List<(ILogSink Sink, EnumField Field)> _sinkRows = new(); // Field 为 null = 该 sink 级别固定、只读显示
        private IVisualElementScheduledItem _ticker;

        // ── 状态 ────────────────────────────────────────────────────────────
        private string _treeFilter = "";
        private string _cmdFilter = "";
        private bool _onlyErrors;
        private readonly Dictionary<GameContext, int> _idByCtx = new(); // TreeView 稳定 id（展开状态按 id 记忆）
        private int _nextId = 1;
        private readonly HashSet<int> _knownIds = new();               // 已见过的 id：新节点默认展开、老节点尊重用户折叠
        private string _treeSignature = "";
        private string _monoIssueSignature = "";
        private GameContext _selected;
        private string _detailSignature;
        private readonly Dictionary<GameContext, MonoGameContextBase> _monoByCtx = new();
        private readonly List<LoggingCommandSystem.Entry> _cmdRing = new();
        private readonly List<LoggingCommandSystem.Entry> _cmdRows = new(); // 过滤后（新 → 旧），表格数据源
        private long _lastTotalRecorded = -1;
        private bool _cmdFilterDirty = true;
        private bool _secRegOpen = true, _secEvtOpen = true, _secPoolOpen = true;
        private LayoutMode? _layoutMode;

        /// <summary>树节点数据：TreeView item 里只挂 Context 引用，其余现算（每次重绑都拿最新值）。</summary>
        private sealed class CtxItem
        {
            public GameContext Ctx;
        }

        private void OnEnable() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            _ticker?.Pause();
        }

        // 新 Play 会话开始：id 映射 / 选中 / 签名全部作废（键是 Context 强引用，不清会让死 Context 无法 GC）。
        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            _idByCtx.Clear();
            _knownIds.Clear();
            _treeSignature = "";
            _monoIssueSignature = "";
            _detailSignature = null;
            _selected = null;
            _lastTotalRecorded = -1;
        }

        public void CreateGUI()
        {
            // ⚠ 这几个签名是「当前可视树已经按这份数据建好了」的缓存，可视树一重建就必须作废。
            // 域重载后 Unity 会用反序列化的窗口实例重新调 CreateGUI：VisualElement 引用是全新的空容器，
            // 而这些字段可能带着上一轮的值活过来——不清就会「签名没变 → 跳过重建 → 容器永远空着」。
            _sinkSignature = null;
            _treeSignature = "";
            _monoIssueSignature = "";
            _detailSignature = null;
            _layoutMode = null;
            _counterSeparators.Clear();

            var root = rootVisualElement;
            _ticker?.Pause();
            root.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            root.Clear();
            minSize = new Vector2(280, 420);
            root.Add(BuildToolbar());
            root.Add(BuildCountersStrip());
            root.Add(BuildLoggingStrip());

            // 上下分割：上 = Context 树 + 明细（左右分割），下 = 命令流水。
            _mainSplit = new TwoPaneSplitView(1, 220, TwoPaneSplitViewOrientation.Vertical)
            {
                name = "diagnostics-main-split",
                style = { flexGrow = 1, minHeight = 180 },
            };
            _contextSplit = new TwoPaneSplitView(0, 340, TwoPaneSplitViewOrientation.Horizontal)
            {
                name = "diagnostics-context-split",
                style = { flexGrow = 1, minHeight = 120 },
            };
            _treePane = BuildTreePane();
            _detail = (ScrollView)BuildDetailPane();
            _commandPane = BuildCommandPane();
            _contextSplit.Add(_treePane);
            _contextSplit.Add(_detail);
            _mainSplit.Add(_contextSplit);
            _mainSplit.Add(_commandPane);
            root.Add(_mainSplit);

            root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            ApplyResponsiveLayout(position.width, position.height, force: true);

            _ticker = root.schedule.Execute(Tick).Every(RefreshMs);
            if (!SessionState.GetBool(AutoRefreshKey, true)) _ticker.Pause();
            Tick();
        }

        // ── 顶栏 ────────────────────────────────────────────────────────────

        private VisualElement BuildToolbar()
        {
            var wrapper = new VisualElement { name = "diagnostics-toolbar" };
            _toolbarActions = new Toolbar { name = "diagnostics-toolbar-actions" };
            _toolbarSearchRow = new Toolbar
            {
                name = "diagnostics-toolbar-search-row",
                style = { display = DisplayStyle.None },
            };

            var auto = new ToolbarToggle
            {
                text = "自动刷新",
                value = SessionState.GetBool(AutoRefreshKey, true),
                tooltip = "每 500ms 刷新一次；关闭后可用「刷新」手动取快照（冻结画面方便细看）。",
            };
            auto.RegisterValueChangedCallback(e =>
            {
                SessionState.SetBool(AutoRefreshKey, e.newValue);
                if (e.newValue) { _ticker.Resume(); Tick(); }
                else _ticker.Pause();
            });
            _toolbarActions.Add(auto);
            _toolbarActions.Add(new ToolbarButton(Tick) { text = "刷新", tooltip = "手动刷新一次（自动刷新关闭时用）。" });

            _treeSearchField = new ToolbarSearchField
            {
                name = "diagnostics-tree-search",
                tooltip = "过滤 Context 树：匹配 Context 名 / 注册契约名 / 事件类型名（保留命中节点的祖先）。",
                style = { flexGrow = 1, flexShrink = 1, marginLeft = 6, marginRight = 6 },
            };
            _treeSearchField.RegisterValueChangedCallback(e =>
            {
                _treeFilter = e.newValue?.Trim() ?? "";
                _treeSignature = ""; // 强制重建树
                Tick();
            });
            _toolbarActions.Add(_treeSearchField);

            _toolbarActions.Add(new ToolbarButton(() => _tree?.ExpandAll()) { text = "展开" });
            _toolbarActions.Add(new ToolbarButton(() => _tree?.CollapseAll()) { text = "折叠" });
            wrapper.Add(_toolbarActions);
            wrapper.Add(_toolbarSearchRow);
            return wrapper;
        }

        private VisualElement BuildCountersStrip()
        {
            _counterStrip = new VisualElement
            {
                name = "diagnostics-counters",
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    flexWrap = Wrap.Wrap,
                    paddingLeft = 8, paddingRight = 8, paddingTop = 3, paddingBottom = 3,
                    borderBottomWidth = 1, borderBottomColor = new Color(0, 0, 0, 0.3f),
                },
            };

            _ctxCountLabel = MutedLabel("存活 Context —");
            _ctxSpark = new Sparkline(ColMono) { tooltip = "存活 Context 数趋势（最近约 30 秒）" };
            _bagCountLabel = MutedLabel("Bag 存活 —");
            _bagSpark = new Sparkline(ColRuntime) { tooltip = "DisposableBag 存活数趋势（最近约 30 秒）——持续上升 = 泄漏嫌疑" };
            _cmdCountLabel = MutedLabel("命令累计 —");

            _ctxCountLabel.style.flexShrink = 0;
            _bagCountLabel.style.flexShrink = 0;
            _cmdCountLabel.style.flexShrink = 0;

            _counterStrip.Add(_ctxCountLabel);
            _counterStrip.Add(_ctxSpark);
            _counterStrip.Add(Dot());
            _counterStrip.Add(_bagCountLabel);
            _counterStrip.Add(_bagSpark);
            _counterStrip.Add(Dot());
            _counterStrip.Add(_cmdCountLabel);
            return _counterStrip;

            Label Dot()
            {
                var dot = new Label("·") { style = { color = ColMuted, marginLeft = 8, marginRight = 8 } };
                _counterSeparators.Add(dot);
                return dot;
            }
        }

        // ── 日志状态条（全局） ───────────────────────────────────────────────

        /// <summary>
        /// 日志系统的全局状态**且可就地改**：全局级别（总闸门）+ 接管 Unity 日志流 + 各 sink 的 MinLevel 下拉。
        /// </summary>
        /// <remarks>
        /// 为什么值得占一行：这些在编辑器里原本**完全看不见、也只能改代码**。sink 与 <c>CaptureUnityLogs</c>
        /// 都是业务在启动期用代码装配的（ADR-0034 §3：显式注册、不走配置资产），代价就是
        /// 「日志怎么没落盘 / 引擎报错怎么没进文件」时无从查证，而想临时调一下还得改代码 + 重进 Play。
        /// 本栏把这三样做成可读可改，改动**立即生效但不持久**（下次运行仍由业务的启动代码决定，
        /// 面板不悄悄改变正式行为）。日志是全局静态的，故放顶部全局区而不是 per-Context 明细里。
        /// <br/>
        /// ⚠ UI 细节：Unity 的 <c>Toggle(string)</c> 构造器设的是 <b>BaseField 的 label</b>——它渲染在勾选框
        /// **左侧且带固定宽度**，会把文字推远、让勾选框贴到后一个控件上，看不出勾选框属于谁。
        /// 故一律用 <c>text</c>（渲染在勾选框**右侧紧贴着**）。
        /// </remarks>
        private VisualElement BuildLoggingStrip()
        {
            _loggingStrip = new VisualElement
            {
                name = "diagnostics-logging",
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    paddingLeft = 8, paddingRight = 8, paddingTop = 3, paddingBottom = 3,
                    borderBottomWidth = 1, borderBottomColor = new Color(0, 0, 0, 0.3f),
                },
            };

            _loggingGlobalRow = new VisualElement
            {
                name = "diagnostics-logging-global",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    flexWrap = Wrap.Wrap,
                    flexShrink = 0,
                },
            };

            _loggingSinkRow = new VisualElement
            {
                name = "diagnostics-logging-sinks",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    flexWrap = Wrap.Wrap,
                    flexGrow = 1,
                    flexShrink = 1,
                },
            };

            _loggingGlobalRow.Add(new Label("日志")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, marginRight = 10, color = ColMuted },
            });

            // 全局 MinLevel（总闸门）。会话写入统一经 FrameworkLogMenu，域重载后恢复到运行时字段。
            // 摆在各 sink 的分闸门左边，「总闸门 → 分闸门」的串联关系一眼可见——日志要同时过这两道。
            _loggingGlobalRow.Add(new Label("全局 ≥")
            {
                tooltip = "Log.MinLevel：日志的【总闸门】。低于它的日志一律不投递、连 LogEntry 都不构造。\n" +
                          "右边每个 sink 还各有一道【分闸门】(MinLevel)——一条日志要【同时】过这两道才到得了那个 sink。\n\n" +
                          "设成 Trace = 俗称的「开 Verbose」（看容器注册 / 解析、资源重试等框架诊断噪音）；\n" +
                          "设成 Warning = 全局压掉 Info 噪音，不必逐个改 sink。\n" +
                          "只影响本次 Editor 会话；重启 Unity 后恢复 Info。",
                style = { color = ColMuted, fontSize = 11, marginRight = 3 },
            });
            _minLevelField = new EnumField(Log.MinLevel)
            {
                style = { minWidth = 76, marginRight = 12, marginTop = 0, marginBottom = 0 },
            };
            _minLevelField.RegisterValueChangedCallback(e => FrameworkLogMenu.SetMinLevel((LogLevel)e.newValue));
            _loggingGlobalRow.Add(_minLevelField);

            // 接管 Unity 日志流：CaptureUnityLogs 幂等、可随时开关，故直接做成勾选框。
            _captureToggle = new Toggle
            {
                text = "接管 Unity 日志流",
                value = Log.IsCapturingUnityLogs,
                tooltip = "Log.CaptureUnityLogs()：接管 Application.logMessageReceivedThreaded，把引擎报错 /\n" +
                          "第三方包日志 / 裸 Debug.Log / 未捕获异常也灌进 sink。\n" +
                          "不开的话，玩家崩溃的那个 NullReferenceException 根本不在你的日志文件里。\n\n" +
                          "⚠ 这里改只对当前运行有效、不持久——下次运行仍由业务启动代码里的调用决定。",
                style = { marginRight = 12, fontSize = 11 },
            };
            _captureToggle.RegisterValueChangedCallback(e => Log.CaptureUnityLogs(e.newValue));
            _loggingGlobalRow.Add(_captureToggle);

            _loggingSeparator = new Label("·") { style = { color = ColMuted, marginRight = 8 } };
            _loggingSinkRow.Add(_loggingSeparator);

            _loggingSinkRow.Add(new Label("输出端（Sink）")
            {
                tooltip = "当前装配的日志去向（Log.Sinks）。右侧下拉 = 该 sink 的 MinLevel，低于它的日志不投递给它。\n" +
                          "改了立即生效（不持久）——想临时把细粒度日志抓进文件，把文件 sink 调到 Trace 再把总闸门放行到 Trace 即可，\n" +
                          "不必改代码重进 Play。",
                style = { color = ColMuted, fontSize = 11, marginRight = 4 },
            });

            _sinkContainer = new VisualElement
            {
                name = "diagnostics-sink-container",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    flexWrap = Wrap.Wrap,
                    flexGrow = 1,
                    flexShrink = 1,
                    overflow = Overflow.Visible,
                },
            };
            _loggingSinkRow.Add(_sinkContainer);

            _loggingStrip.Add(_loggingGlobalRow);
            _loggingStrip.Add(_loggingSinkRow);
            return _loggingStrip;
        }

        private void RefreshLogging()
        {
            if (_minLevelField == null) return;

            // 菜单 / 代码都可能在面板之外改过状态（或刚域重载），每次 tick 对齐一次显示。
            if (!Equals(_minLevelField.value, Log.MinLevel)) _minLevelField.SetValueWithoutNotify(Log.MinLevel);

            bool capturing = Log.IsCapturingUnityLogs;
            if (_captureToggle.value != capturing) _captureToggle.SetValueWithoutNotify(capturing);

            var sinks = Log.Sinks;

            // sink 组成变了才重建行（否则每 500ms 重建会打断正在操作的下拉）；MinLevel 变化只同步下拉的值。
            var sig = new StringBuilder();
            foreach (var s in sinks) sig.Append(s.GetType().FullName).Append(';');
            string signature = sig.ToString();
            if (signature != _sinkSignature)
            {
                _sinkSignature = signature;
                RebuildSinkRows(sinks);
            }

            foreach (var (sink, field) in _sinkRows)
                if (field != null && !Equals(field.value, sink.MinLevel))
                    field.SetValueWithoutNotify(sink.MinLevel); // 代码侧改过 MinLevel：面板跟上
        }

        private void RebuildSinkRows(IReadOnlyList<ILogSink> sinks)
        {
            _sinkContainer.Clear();
            _sinkRows.Clear();

            if (sinks.Count == 0)
            {
                // ClearSinks 之后没再装——日志此刻无处可去（测试里正常，正式运行时是事故）。
                _sinkContainer.Add(new Label("无（日志无处可去！）") { style = { color = ColError, fontSize = 11 } });
                return;
            }

            foreach (var sink in sinks)
            {
                var box = new VisualElement
                {
                    name = "diagnostics-sink",
                    style =
                    {
                        flexDirection = FlexDirection.Row,
                        alignItems = Align.Center,
                        marginRight = 10,
                        flexShrink = 0,
                    },
                };
                box.Add(new Label(sink.GetType().Name) { style = { fontSize = 11, color = ColMuted, marginRight = 2 } });

                // MinLevel 在 ILogSink 上是只读的（不强迫所有 sink 可变——测试里的固定级别 sink 就只有 getter）；
                // 具体实现（UnityDebugLogSink / FileLogSink）才有 setter。故在具体类型上找可写的 MinLevel：
                // 找得到就给下拉，找不到就只读显示。反射只发生在重建时、且按类型缓存。
                var setter = FindMinLevelSetter(sink.GetType());
                if (setter != null)
                {
                    var field = new EnumField(sink.MinLevel) { style = { minWidth = 76, marginTop = 0, marginBottom = 0 } };
                    var captured = sink;
                    field.RegisterValueChangedCallback(e => setter.SetValue(captured, e.newValue));
                    box.Add(field);
                    _sinkRows.Add((sink, field));
                }
                else
                {
                    box.Add(new Label($"(≥{sink.MinLevel})") { style = { fontSize = 11, color = ColMuted } });
                    _sinkRows.Add((sink, null));
                }

                _sinkContainer.Add(box);
            }
        }

        // 具体 sink 类型上「可写的 MinLevel 属性」缓存（没有则为 null，代表该 sink 的级别固定、面板只读显示）。
        private static readonly Dictionary<Type, PropertyInfo> MinLevelSetters = new();

        private static PropertyInfo FindMinLevelSetter(Type sinkType)
        {
            if (MinLevelSetters.TryGetValue(sinkType, out var cached)) return cached;
            var p = sinkType.GetProperty(nameof(ILogSink.MinLevel), BindingFlags.Public | BindingFlags.Instance);
            if (p != null && (!p.CanWrite || p.PropertyType != typeof(LogLevel))) p = null;
            return MinLevelSetters[sinkType] = p;
        }

        // ── Context 树（左） ─────────────────────────────────────────────────

        private VisualElement BuildTreePane()
        {
            var pane = new VisualElement
            {
                name = "diagnostics-tree-pane",
                style = { flexGrow = 1, minWidth = 0, minHeight = 100 },
            };

            _treeHint = new HelpBox(
                "进入 Play 模式后，这里展示存活 Context 作用域树。\n" +
                "退出 Play 后仍留在树上的 Context = 上一局没 Dispose 的泄漏嫌疑（下次进 Play 时清空）。",
                HelpBoxMessageType.Info);
            pane.Add(_treeHint);

            // Failed Mono Context 没有可放进 LiveContexts 的 GameContext。单独展示宿主真实状态，
            // 保持下方作用域树“每个节点都一定有 Container”的深层不变量。
            _monoIssueScroll = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "diagnostics-mono-issues-scroll",
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto,
                style =
                {
                    display = DisplayStyle.None,
                    maxHeight = 240,
                    flexShrink = 1,
                    marginBottom = 4,
                },
            };
            _monoIssuePanel = new VisualElement
            {
                name = "diagnostics-mono-issues",
                style =
                {
                    paddingLeft = 4,
                    paddingRight = 4,
                },
            };
            _monoIssueScroll.Add(_monoIssuePanel);
            pane.Add(_monoIssueScroll);

            _tree = new TreeView
            {
                fixedItemHeight = 20,
                selectionType = SelectionType.Single,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                style = { flexGrow = 1, minHeight = 60 },
            };
            _tree.makeItem = MakeTreeRow;
            _tree.bindItem = (ve, i) => BindTreeRow(ve, _tree.GetItemDataForIndex<CtxItem>(i));
            _tree.selectionChanged += items =>
            {
                _selected = (items.FirstOrDefault() as CtxItem)?.Ctx;
                _detailSignature = null; // 强制重建明细
                RefreshDetail();
            };
            // 双击 / 回车：定位场景对象（仅场景 Mono Context 有对应物）。
            _tree.itemsChosen += items =>
            {
                if ((items.FirstOrDefault() as CtxItem)?.Ctx is { } ctx) PingSceneObject(ctx);
            };
            pane.Add(_tree);
            return pane;
        }

        private static VisualElement MakeTreeRow()
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexGrow = 1 } };
            row.Add(new Label
            {
                name = "name",
                style = { flexShrink = 1, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis, unityTextAlign = TextAnchor.MiddleLeft },
            });
            row.Add(new VisualElement { name = "badges", style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 0 } });
            row.Add(new Label { name = "meta", style = { color = ColMuted, fontSize = 10, flexShrink = 0, marginLeft = 6 } });
            return row;
        }

        private void BindTreeRow(VisualElement row, CtxItem item)
        {
            var ctx = item.Ctx;
            row.Q<Label>("name").text = DisplayName(ctx);

            var badges = row.Q<VisualElement>("badges");
            badges.Clear();
            if (ReferenceEquals(ctx, GameContext.Main))
                badges.Add(Badge("Main", ColMain, "全局主上下文（GameContext.Main）"));
            badges.Add(_monoByCtx.ContainsKey(ctx)
                ? Badge("Mono", ColMono, "场景 MonoGameContextBase 的内部 Context（双击定位场景对象）")
                : Badge("C#", ColPure, "纯 C# GameContext（GameFlow 状态 / 手工 new）"));
            if (ctx.InheritsFromGlobal && !ReferenceEquals(ctx, GameContext.Main))
                badges.Add(Badge("→Main", ColFallback, "本地与父链未命中时回退 GameContext.Main"));

            int regs = ctx.Container.LocalRegistrationDetails.Count();
            int subs = ctx.EventSubscriptionCounts?.Sum(kv => kv.Value) ?? 0;
            var meta = row.Q<Label>("meta");
            meta.text = $"注册 {regs} · 订阅 {subs} · {FormatDuration(Time.realtimeSinceStartupAsDouble - ctx.CreatedRealtime)}";
            meta.style.display = _layoutMode == LayoutMode.Compact ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ── 明细（右） ──────────────────────────────────────────────────────

        private VisualElement BuildDetailPane()
        {
            _detail = new ScrollView
            {
                name = "diagnostics-detail-pane",
                style = { flexGrow = 1, minWidth = 0, minHeight = 100, paddingLeft = 8, paddingRight = 8, paddingTop = 4 },
            };
            return _detail;
        }

        private void RefreshDetail()
        {
            var ctx = _selected;
            if (ctx == null || ctx.IsDisposed || !FrameworkDiagnostics.LiveContexts.Contains(ctx))
            {
                if (_detailSignature != "empty")
                {
                    _detailSignature = "empty";
                    _detail.Clear();
                    _detail.Add(MutedLabel(ctx == null ? "← 在左侧选择一个 Context 查看明细" : "选中的 Context 已释放"));
                }
                return;
            }

            string sig = ComputeDetailSignature(ctx);
            if (sig == _detailSignature)
            {
                // 结构没变只更新存活时长，不整体重建（保滚动位置 / 折叠状态）。
                if (_detailAliveLabel != null)
                    _detailAliveLabel.text = $"存活 {FormatDuration(Time.realtimeSinceStartupAsDouble - ctx.CreatedRealtime)}";
                return;
            }
            _detailSignature = sig;
            _detail.Clear();

            // 头部：名称 + 徽标 + 存活时长 + 定位按钮。
            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    flexWrap = Wrap.Wrap,
                    marginBottom = 2,
                },
            };
            header.Add(new Label(DisplayName(ctx)) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 13, flexShrink = 1 } });
            if (ReferenceEquals(ctx, GameContext.Main)) header.Add(Badge("Main", ColMain, null));
            bool isMono = _monoByCtx.TryGetValue(ctx, out var mono) && mono != null;
            header.Add(isMono ? Badge("Mono", ColMono, null) : Badge("C#", ColPure, null));
            if (ctx.InheritsFromGlobal && !ReferenceEquals(ctx, GameContext.Main)) header.Add(Badge("→Main", ColFallback, null));
            _detail.Add(header);

            var subHeader = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    flexWrap = Wrap.Wrap,
                    marginBottom = 6,
                },
            };
            _detailAliveLabel = MutedLabel($"存活 {FormatDuration(Time.realtimeSinceStartupAsDouble - ctx.CreatedRealtime)}");
            subHeader.Add(_detailAliveLabel);
            if (isMono)
            {
                subHeader.Add(new Button(() => PingSceneObject(ctx))
                {
                    text = "定位场景对象",
                    tooltip = "在 Hierarchy 中 ping 并选中承载此 Context 的 GameObject",
                    style = { marginLeft = 8, fontSize = 10 },
                });
            }
            _detail.Add(subHeader);

            // 本地注册表。
            var regs = ctx.Container.LocalRegistrationDetails.OrderBy(d => d.Contract.Name, StringComparer.Ordinal).ToList();
            var regFold = Section($"本地注册（{regs.Count}）——不含父级回退", _secRegOpen, v => _secRegOpen = v);
            if (regs.Count == 0) regFold.Add(MutedLabel("（无）"));
            foreach (var d in regs)
            {
                var line = new VisualElement
                {
                    style =
                    {
                        flexDirection = FlexDirection.Row,
                        alignItems = Align.Center,
                        flexWrap = Wrap.Wrap,
                    },
                };
                string target = d.IsPendingFactory ? "（未首次解析）" : d.Instance?.GetType().Name ?? "null";
                line.Add(new Label($"{d.Contract.Name} → {target}")
                {
                    style = { fontSize = 11, flexShrink = 1, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis },
                });
                line.Add(d.IsPendingFactory
                    ? Badge("工厂", ColFactory, "工厂绑定，尚未首次 Resolve——面板不会触发它（观察不改变系统）")
                    : d.IsOverride
                        ? Badge("运行时", ColRuntime, "运行时覆盖（MonoXxxBase 自动注册 / RegisterXxx）")
                        : Badge("构建时", ColBuild, "InstallBindings 里的构建期绑定"));
                if (d.Instance is UnityEngine.Object uo)
                {
                    line.Add(new Button(() => { EditorGUIUtility.PingObject(uo); Selection.activeObject = uo; })
                    {
                        text = "定位",
                        style = { fontSize = 9, marginLeft = 4, paddingLeft = 4, paddingRight = 4 },
                    });
                }
                regFold.Add(line);
            }
            _detail.Add(regFold);

            // 事件订阅计数。
            var events = ctx.EventSubscriptionCounts?.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
            int totalSubs = events?.Sum(kv => kv.Value) ?? 0;
            var evtFold = Section($"事件订阅（{totalSubs}）——只增不减 = 泄漏嫌疑", _secEvtOpen, v => _secEvtOpen = v);
            if (events == null || events.Count == 0) evtFold.Add(MutedLabel("（无存活订阅）"));
            else
                foreach (var kv in events)
                    evtFold.Add(new Label($"{kv.Key.Name} × {kv.Value}") { style = { fontSize = 11 } });
            _detail.Add(evtFold);

            // 池借出 / 空闲（只看本地注册的池；父级的池在父节点看，避免整棵树重复）。
            var poolImpl = ResolveLocalPool(ctx);
            if (poolImpl != null)
            {
                var pools = poolImpl.GetPoolDiagnostics();
                var poolFold = Section($"对象池（{pools.Count}）", _secPoolOpen, v => _secPoolOpen = v);
                if (pools.Count == 0) poolFold.Add(MutedLabel("（无池）"));
                else
                    foreach (string lineText in pools)
                        poolFold.Add(new Label(lineText) { style = { fontSize = 11 } });
                _detail.Add(poolFold);
            }
        }

        private static string ComputeDetailSignature(GameContext ctx)
        {
            var sb = new StringBuilder(256);
            sb.Append(ctx.GetHashCode()).Append('|').Append(ctx.DebugName);
            foreach (var d in ctx.Container.LocalRegistrationDetails)
                sb.Append(d.Contract.Name).Append(d.IsPendingFactory ? 'F' : d.IsOverride ? 'O' : 'B')
                  .Append(d.Instance?.GetType().Name).Append(';');
            var counts = ctx.EventSubscriptionCounts;
            if (counts != null)
                foreach (var kv in counts)
                    sb.Append(kv.Key.Name).Append(':').Append(kv.Value).Append(';');
            var pool = ResolveLocalPool(ctx);
            if (pool != null)
                foreach (string s in pool.GetPoolDiagnostics())
                    sb.Append(s).Append(';');
            return sb.ToString();
        }

        // 取本 Context 本地注册的池实现（不经 Resolve——不触发工厂、不吃父级回退）。
        private static PoolUtility ResolveLocalPool(GameContext ctx)
        {
            foreach (var d in ctx.Container.LocalRegistrationDetails)
            {
                if (d.Contract != typeof(IPoolUtility)) continue;
                return d.Instance switch
                {
                    PoolUtility p => p,
                    MonoPoolUtility mono => mono.Impl,
                    _ => null,
                };
            }
            return null;
        }

        // ── 命令流水（下） ──────────────────────────────────────────────────

        private VisualElement BuildCommandPane()
        {
            var pane = new VisualElement
            {
                name = "diagnostics-command-pane",
                style = { flexGrow = 1, minWidth = 0, minHeight = 90 },
            };

            _commandToolbarPrimary = new Toolbar { name = "diagnostics-command-toolbar" };
            _commandToolbarSearchRow = new Toolbar
            {
                name = "diagnostics-command-search-row",
                style = { display = DisplayStyle.None },
            };
            _commandToolbarPrimary.Add(new Label("命令（Command）流水")
            {
                style = { alignSelf = Align.Center, unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, marginLeft = 4, marginRight = 8 },
            });

            _commandSearchField = new ToolbarSearchField
            {
                name = "diagnostics-command-search",
                tooltip = "过滤命令流水：匹配命令类型名 / Context 名。",
                style = { flexGrow = 1, flexShrink = 1 },
            };
            _commandSearchField.RegisterValueChangedCallback(e => { _cmdFilter = e.newValue?.Trim() ?? ""; _cmdFilterDirty = true; Tick(); });
            _commandToolbarPrimary.Add(_commandSearchField);

            _onlyErrors = SessionState.GetBool(OnlyErrorsKey, false);
            var errToggle = new ToolbarToggle { text = "仅错误", value = _onlyErrors, tooltip = "只看失败 / 取消的命令。" };
            errToggle.RegisterValueChangedCallback(e =>
            {
                _onlyErrors = e.newValue;
                SessionState.SetBool(OnlyErrorsKey, e.newValue);
                _cmdFilterDirty = true;
                Tick();
            });
            _commandToolbarPrimary.Add(errToggle);

            _commandToolbarPrimary.Add(new ToolbarButton(CopyCommandsTsv) { text = "复制", tooltip = "把当前过滤结果以 TSV 复制到剪贴板（可直接粘进表格软件）。" });
            _commandToolbarPrimary.Add(new ToolbarButton(() =>
            {
                LoggingCommandSystem.ClearLog();
                _cmdFilterDirty = true;
                Tick();
            }) { text = "清空" });
            pane.Add(_commandToolbarPrimary);
            pane.Add(_commandToolbarSearchRow);

            _commandHint = new HelpBox(
                "未记录到命令。接入（opt-in、不改变执行语义）：根 Context 的 InstallBindings 里注册\n" +
                "builder.RegisterValue(new LoggingCommandSystem(), typeof(ICommandSystem));\n" +
                "替换默认 CommandSystem 即得全局命令流水；是否启用由各项目的 Composition Root 明确决定。",
                HelpBoxMessageType.Info);
            pane.Add(_commandHint);

            _commandTable = new MultiColumnListView
            {
                name = "diagnostics-command-table",
                fixedItemHeight = 20,
                selectionType = SelectionType.Single,
                showAlternatingRowBackgrounds = AlternatingRowBackground.All,
                style = { flexGrow = 1 },
            };
            _timeColumn = MakeColumn("时间", 70, false, (l, e) => l.text = FormatClock(e.StartTime));
            _frameColumn = MakeColumn("帧", 56, false, (l, e) => l.text = e.Frame.ToString());
            _modeColumn = MakeColumn("模式", 48, false, (l, e) =>
            {
                l.text = e.IsAsync ? "异步" : "同步";
                l.style.color = e.IsAsync ? ColAsync : ColMuted;
            });
            _commandColumn = MakeColumn("命令", 200, true, (l, e) => l.text = e.CommandType);
            _contextColumn = MakeColumn("上下文（Context）", 130, false, (l, e) => l.text = e.ContextName);
            _durationColumn = MakeColumn("耗时", 78, false, (l, e) =>
            {
                l.text = $"{e.DurationMs:F2}ms";
                l.style.unityTextAlign = TextAnchor.MiddleRight;
                l.style.color = e.DurationMs >= 100f ? ColBadDur : e.DurationMs >= 16.7f ? ColWarnDur : ColMuted;
            });
            _statusColumn = MakeColumn("状态", 140, true, (l, e) =>
            {
                l.text = e.Error == null ? "✓" : $"✗ {e.Error}";
                l.style.color = e.Error == null ? ColOk : ColError;
            });
            _commandTable.columns.Add(_timeColumn);
            _commandTable.columns.Add(_frameColumn);
            _commandTable.columns.Add(_modeColumn);
            _commandTable.columns.Add(_commandColumn);
            _commandTable.columns.Add(_contextColumn);
            _commandTable.columns.Add(_durationColumn);
            _commandTable.columns.Add(_statusColumn);
            _commandTable.itemsSource = _cmdRows;
            // 选中一行 → 底部给出完整可复制文本（长错误信息在单元格里放不下）。
            _commandTable.selectionChanged += items =>
            {
                if (items.FirstOrDefault() is LoggingCommandSystem.Entry e)
                {
                    _commandDetail.value =
                        $"{FormatClock(e.StartTime)} 帧{e.Frame} {(e.IsAsync ? "异步" : "同步")} {e.CommandType} @{e.ContextName} {e.DurationMs:F2}ms" +
                        (e.Error != null ? $"\n{e.Error}" : "");
                    _commandDetail.style.display = DisplayStyle.Flex;
                }
                else
                {
                    _commandDetail.style.display = DisplayStyle.None;
                }
            };
            pane.Add(_commandTable);

            _commandDetail = new TextField { multiline = true, isReadOnly = true, style = { display = DisplayStyle.None, maxHeight = 52 } };
            pane.Add(_commandDetail);
            return pane;

            Column MakeColumn(string title, float width, bool stretch, Action<Label, LoggingCommandSystem.Entry> bind) => new()
            {
                title = title,
                width = width,
                minWidth = Mathf.Min(width, 40),
                stretchable = stretch,
                makeCell = () => new Label { style = { fontSize = 11, unityTextAlign = TextAnchor.MiddleLeft, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } },
                bindCell = (ve, i) =>
                {
                    var label = (Label)ve;
                    label.style.color = StyleKeyword.Null; // 重置复用 cell 的颜色
                    label.style.unityTextAlign = TextAnchor.MiddleLeft;
                    if (i >= 0 && i < _cmdRows.Count) bind(label, _cmdRows[i]);
                },
            };
        }

        private void CopyCommandsTsv()
        {
            var sb = new StringBuilder("时间\t帧\t模式\t命令\t上下文\t耗时ms\t状态\n");
            foreach (var e in _cmdRows)
                sb.Append(FormatClock(e.StartTime)).Append('\t').Append(e.Frame).Append('\t')
                  .Append(e.IsAsync ? "异步" : "同步").Append('\t').Append(e.CommandType).Append('\t')
                  .Append(e.ContextName).Append('\t').Append(e.DurationMs.ToString("F2")).Append('\t')
                  .Append(e.Error ?? "✓").Append('\n');
            EditorGUIUtility.systemCopyBuffer = sb.ToString();
            ShowNotification(new GUIContent($"已复制 {_cmdRows.Count} 行"));
        }

        // ── 响应式布局 ──────────────────────────────────────────────────────

        /// <summary>
        /// 根据可用宽度决定信息密度。阈值刻意留出一段 Medium 区间，避免窗口停靠时在横竖分栏之间频繁跳动。
        /// </summary>
        internal static LayoutMode ResolveLayoutMode(float width)
            => width >= 960f ? LayoutMode.Wide : width >= 640f ? LayoutMode.Medium : LayoutMode.Compact;

        /// <summary>
        /// 窄屏优先保留“发生了什么、花了多久、是否成功”；时间、帧、同步模式与 Context 仍可通过选中行后的完整明细查看。
        /// </summary>
        internal static bool IsCommandColumnVisible(LayoutMode mode, CommandColumnId column)
        {
            if (mode == LayoutMode.Wide) return true;
            if (mode == LayoutMode.Medium)
                return column is not CommandColumnId.Frame and not CommandColumnId.Mode;
            return column is CommandColumnId.Command or CommandColumnId.Duration or CommandColumnId.Status;
        }

        /// <summary>窗口变矮时同步压缩命令区，但保留足以显示工具栏和至少两行记录的高度。</summary>
        internal static float ResolveCommandPaneDimension(LayoutMode mode, float height)
        {
            float available = Mathf.Max(180f, height - (mode == LayoutMode.Compact ? 110f : 80f));
            float max = mode == LayoutMode.Compact ? 180f : 220f;
            return Mathf.Clamp(available * 0.30f, 90f, max);
        }

        /// <summary>Context 树的首选尺寸；Compact 下它代表高度，其余模式下代表宽度。</summary>
        internal static float ResolveTreePaneDimension(LayoutMode mode, float width, float height)
            => mode switch
            {
                LayoutMode.Compact => Mathf.Clamp(height * 0.24f, 100f, 220f),
                LayoutMode.Medium => Mathf.Clamp(width * 0.38f, 220f, 340f),
                _ => Mathf.Clamp(width * 0.32f, 300f, 380f),
            };

        /// <summary>Mono 问题区在不同信息密度下的上限；与动态首选高度共用，避免出现 minHeight 大于 maxHeight。</summary>
        internal static float ResolveMonoIssueMaxHeight(LayoutMode mode) => mode switch
        {
            LayoutMode.Compact => 180f,
            LayoutMode.Medium => 210f,
            _ => 260f,
        };

        internal static float ResolveMonoIssuePaneHeight(LayoutMode mode, float windowHeight)
        {
            float maxHeight = ResolveMonoIssueMaxHeight(mode);
            return Mathf.Clamp(windowHeight * 0.24f, 120f, Mathf.Min(220f, maxHeight));
        }

        private void OnRootGeometryChanged(GeometryChangedEvent evt)
        {
            if (evt.target != rootVisualElement || evt.newRect.width <= 0f || evt.newRect.height <= 0f) return;
            ApplyResponsiveLayout(evt.newRect.width, evt.newRect.height);
        }

        private void ApplyResponsiveLayout(float width, float height, bool force = false)
        {
            if (width <= 0f || height <= 0f) return;

            var mode = ResolveLayoutMode(width);
            bool modeChanged = force || _layoutMode != mode;
            _layoutMode = mode;

            if (modeChanged)
            {
                bool wide = mode == LayoutMode.Wide;
                bool compact = mode == LayoutMode.Compact;

                MoveElement(_treeSearchField, wide ? _toolbarActions : _toolbarSearchRow, wide ? 2 : 0);
                _toolbarSearchRow.style.display = wide ? DisplayStyle.None : DisplayStyle.Flex;
                _treeSearchField.style.marginLeft = wide ? 6 : 2;
                _treeSearchField.style.marginRight = wide ? 6 : 2;

                MoveElement(_commandSearchField, wide ? _commandToolbarPrimary : _commandToolbarSearchRow, wide ? 1 : 0);
                _commandToolbarSearchRow.style.display = wide ? DisplayStyle.None : DisplayStyle.Flex;

                _ctxSpark.style.display = compact ? DisplayStyle.None : DisplayStyle.Flex;
                _bagSpark.style.display = compact ? DisplayStyle.None : DisplayStyle.Flex;
                foreach (var separator in _counterSeparators)
                    separator.style.display = compact ? DisplayStyle.None : DisplayStyle.Flex;
                _counterStrip.style.paddingLeft = compact ? 4 : 8;
                _counterStrip.style.paddingRight = compact ? 4 : 8;
                _bagCountLabel.style.marginLeft = compact ? 8 : 0;
                _cmdCountLabel.style.marginLeft = compact ? 8 : 0;

                _loggingStrip.style.flexDirection = wide ? FlexDirection.Row : FlexDirection.Column;
                _loggingStrip.style.alignItems = wide ? Align.Center : Align.Stretch;
                _loggingSinkRow.style.marginTop = wide ? 0 : 2;
                _loggingSeparator.style.display = wide ? DisplayStyle.Flex : DisplayStyle.None;
                _minLevelField.style.minWidth = compact ? 64 : 76;
                _minLevelField.style.marginRight = compact ? 4 : 12;
                _captureToggle.style.marginRight = compact ? 4 : 12;

                _contextSplit.orientation = compact
                    ? TwoPaneSplitViewOrientation.Vertical
                    : TwoPaneSplitViewOrientation.Horizontal;
                _treePane.style.minHeight = compact ? 90 : 100;
                _detail.style.minHeight = compact ? 90 : 100;
                _monoIssueScroll.style.maxHeight = ResolveMonoIssueMaxHeight(mode);

                SetCommandColumnVisibility(mode);
                _tree?.RefreshItems(); // 让已复用的行立刻隐藏 / 恢复低优先级 meta 信息。
            }

            // 窗口尺寸变化时重算首选分栏与可见列宽；用户仅拖动 splitter 时根节点几何不变，不会被这里抢回。
            _contextSplit.fixedPaneInitialDimension = ResolveTreePaneDimension(mode, width, height);
            _mainSplit.fixedPaneInitialDimension = ResolveCommandPaneDimension(mode, height);
            ResizeCommandColumns(mode, width);
        }

        private void SetCommandColumnVisibility(LayoutMode mode)
        {
            _timeColumn.visible = IsCommandColumnVisible(mode, CommandColumnId.Time);
            _frameColumn.visible = IsCommandColumnVisible(mode, CommandColumnId.Frame);
            _modeColumn.visible = IsCommandColumnVisible(mode, CommandColumnId.Mode);
            _commandColumn.visible = IsCommandColumnVisible(mode, CommandColumnId.Command);
            _contextColumn.visible = IsCommandColumnVisible(mode, CommandColumnId.Context);
            _durationColumn.visible = IsCommandColumnVisible(mode, CommandColumnId.Duration);
            _statusColumn.visible = IsCommandColumnVisible(mode, CommandColumnId.Status);
        }

        private void ResizeCommandColumns(LayoutMode mode, float width)
        {
            if (mode == LayoutMode.Compact)
            {
                float available = Mathf.Max(260f, width - 8f);
                _durationColumn.width = 70f;
                _durationColumn.minWidth = 64f;
                _statusColumn.width = 76f;
                _statusColumn.minWidth = 70f;
                _commandColumn.width = Mathf.Max(90f, available - 146f);
                _commandColumn.minWidth = 80f;
                return;
            }

            if (mode == LayoutMode.Medium)
            {
                _commandColumn.minWidth = 40f;
                _durationColumn.minWidth = 40f;
                _statusColumn.minWidth = 40f;
                _timeColumn.width = 70f;
                _contextColumn.width = 120f;
                _durationColumn.width = 76f;
                _statusColumn.width = 110f;
                _commandColumn.width = Mathf.Max(150f, width - 390f);
                return;
            }

            _commandColumn.minWidth = 40f;
            _durationColumn.minWidth = 40f;
            _statusColumn.minWidth = 40f;
            _timeColumn.width = 70f;
            _frameColumn.width = 56f;
            _modeColumn.width = 48f;
            _commandColumn.width = 200f;
            _contextColumn.width = 130f;
            _durationColumn.width = 78f;
            _statusColumn.width = 140f;
        }

        private static void MoveElement(VisualElement element, VisualElement target, int index)
        {
            element.RemoveFromHierarchy();
            target.Insert(Mathf.Clamp(index, 0, target.childCount), element);
        }

        // ── 定时刷新 ────────────────────────────────────────────────────────

        private void Tick()
        {
            if (_tree == null) return; // CreateGUI 之前的保护

            var contexts = FrameworkDiagnostics.LiveContexts;

            // 场景 Mono Context 反查表（定位按钮 / Mono 徽标用）。
            _monoByCtx.Clear();
            var monoContexts = FindObjectsByType<MonoGameContextBase>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var m in monoContexts)
            {
                var snapshot = m.DiagnosticSnapshot;
                if (snapshot.State == MonoContextDiagnosticState.Ready && snapshot.Context != null)
                    _monoByCtx[snapshot.Context] = m;
            }

            var monoIssues = RefreshMonoIssues(monoContexts);
            RefreshCounters(
                contexts.Count,
                monoIssues.RootCauses,
                monoIssues.TimingGroups,
                monoIssues.Affected);
            RefreshLogging();
            RefreshTree(contexts);
            RefreshDetail();
            RefreshCommands();
        }

        private void RefreshCounters(
            int contextCount,
            int monoRootCauseCount,
            int monoTimingGroupCount,
            int monoAffectedCount)
        {
            string monoSummary = BuildMonoIssueSummary(
                monoRootCauseCount,
                monoTimingGroupCount,
                monoAffectedCount);
            _ctxCountLabel.text = $"存活 Context {contextCount}{monoSummary}";
            _bagCountLabel.text = $"Bag 存活 {FrameworkDiagnostics.BagsAlive}（累计 {FrameworkDiagnostics.BagsCreated}）";
            _cmdCountLabel.text = $"命令累计 {LoggingCommandSystem.TotalRecorded}";
            if (EditorApplication.isPlaying)
            {
                _ctxSpark.Push(contextCount);
                _bagSpark.Push(FrameworkDiagnostics.BagsAlive);
            }
        }

        internal static string BuildMonoIssueSummary(
            int rootCauseCount,
            int timingGroupCount,
            int affectedCount)
        {
            if (rootCauseCount == 0 && timingGroupCount == 0) return string.Empty;
            if (rootCauseCount == 0)
                return $" · Mono 时序提醒 {timingGroupCount}（影响 {affectedCount}）";
            if (timingGroupCount == 0)
                return $" · Mono 根因 {rootCauseCount}（影响 {affectedCount}）";
            return $" · Mono 根因 {rootCauseCount} · 时序提醒 {timingGroupCount}（影响 {affectedCount}）";
        }

        /// <summary>
        /// 展示“有宿主、无 GameContext”的初始化异常。这里复用 Tick 已做的场景扫描，不建立第二份静态强引用
        /// 登记表；删除窗口后 Core 的运行时语义完全不受影响。
        /// </summary>
        private (int RootCauses, int TimingGroups, int Affected) RefreshMonoIssues(
            IReadOnlyList<MonoGameContextBase> monoContexts)
        {
            bool editorIsPlaying = EditorApplication.isPlaying;
            IReadOnlyList<MonoContextIssueAnalysis.Group> groups =
                MonoContextIssueAnalysis.Analyze(monoContexts, editorIsPlaying);
            int rootCauseCount = groups.Count(group => !group.IsTimingConcern);
            int timingGroupCount = groups.Count - rootCauseCount;
            int affectedCount = groups.Sum(group => group.Affected.Count);
            string signature = MonoContextIssueAnalysis.BuildSignature(groups, editorIsPlaying);

            _monoIssueScroll.style.display = groups.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            LayoutMode mode = _layoutMode ?? ResolveLayoutMode(position.width);
            _monoIssueScroll.style.minHeight = groups.Count == 0
                ? 0
                : ResolveMonoIssuePaneHeight(mode, position.height);
            if (signature == _monoIssueSignature)
                return (rootCauseCount, timingGroupCount, affectedCount);
            _monoIssueSignature = signature;
            _monoIssuePanel.Clear();

            if (groups.Count == 0) return (0, 0, 0);

            bool hasFailure = rootCauseCount > 0;
            var headerParts = new List<string>(2);
            if (rootCauseCount > 0) headerParts.Add($"{rootCauseCount} 个根因");
            if (timingGroupCount > 0) headerParts.Add($"{timingGroupCount} 个时序提醒");
            _monoIssuePanel.Add(new Label(
                $"Mono 初始化：{string.Join(" · ", headerParts)} · 影响 {affectedCount} 个 Context")
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = editorIsPlaying && hasFailure ? ColError : ColWarnDur,
                    marginBottom = 2,
                    whiteSpace = WhiteSpace.Normal,
                },
            });

            _monoIssuePanel.Add(new HelpBox(
                editorIsPlaying
                    ? hasFailure
                        ? "当前 Play 证据：先处理每组“最先失败”。一个父级失败会让依赖它的子 Context 一起失败，影响数量不等于独立 bug 数量。"
                        : "当前 Play 时序提醒：激活对象仍未完成初始化；若持续超过一帧，再检查最上游对象的激活状态与 Awake 时序。"
                    : "历史证据：当前没有在运行。这些状态来自上次 Play，保留是为了停止后仍能定位和复制；场景重载后会重建，若关闭 Scene Reload 请手动重载场景。",
                editorIsPlaying && hasFailure ? HelpBoxMessageType.Error : HelpBoxMessageType.Warning));

            foreach (MonoContextIssueAnalysis.Group group in groups)
            {
                MonoContextIssueAnalysis.Candidate origin = group.Origin;
                string cause = MonoContextIssueAnalysis.CauseSummary(group.RootCause);
                string originLabel = group.HasParentCycle
                    ? "优先定位（父级链循环）"
                    : group.IsTimingConcern ? "最上游未就绪" : "最先失败";

                _monoIssuePanel.Add(new HelpBox(
                    $"{MonoContextIssueAnalysis.EvidenceLabel(editorIsPlaying)}\n" +
                    $"{(group.IsTimingConcern ? "时序提醒" : "首要根因")}：{cause}\n" +
                    $"{originLabel}：{origin.Path}  [{MonoContextIssueAnalysis.StateLabel(origin.Snapshot.State)}]\n" +
                    $"受影响：{group.Affected.Count} 个 Context",
                    editorIsPlaying && group.RootCause != null
                        ? HelpBoxMessageType.Error
                        : HelpBoxMessageType.Warning));

                var actions = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginBottom = 2 },
                };
                actions.Add(new Button(() => PingSceneObject(origin.Host))
                {
                    text = group.HasParentCycle
                        ? "定位循环链起点"
                        : group.IsTimingConcern ? "定位最上游对象" : "定位最先失败对象",
                });
                string report = MonoContextIssueAnalysis.BuildCopyReport(group, editorIsPlaying);
                actions.Add(new Button(() => EditorGUIUtility.systemCopyBuffer = report)
                {
                    text = "复制整组诊断",
                });
                _monoIssuePanel.Add(actions);

                var affected = new Foldout
                {
                    text = $"受影响 Context（{group.Affected.Count}）",
                    value = group.Affected.Count <= 3,
                    style = { marginBottom = 4 },
                };
                foreach (MonoContextIssueAnalysis.Candidate candidate in group.Affected)
                {
                    affected.Add(new Label(
                        $"• {candidate.Path}  [{MonoContextIssueAnalysis.StateLabel(candidate.Snapshot.State)}]\n" +
                        $"  父级：{MonoContextIssueAnalysis.DescribeParent(candidate.Snapshot.ResolvedParent)}")
                    {
                        style = { whiteSpace = WhiteSpace.Normal, color = ColMuted, marginBottom = 2 },
                    });
                }
                _monoIssuePanel.Add(affected);
            }

            return (rootCauseCount, timingGroupCount, affectedCount);
        }

        /// <summary>
        /// 判断 Mono Context 快照是否代表需要维护者处理的异常。普通 MonoBehaviour 在 Edit Mode 不执行
        /// <c>Awake</c>，所以 Uninitialized 是场景资产的正常静态状态；只有进入 Play 后仍未初始化才可疑。
        /// Failed 保留跨模式可见，便于停止 Play 后继续复制异常和定位宿主。
        /// </summary>
        internal static bool ShouldReportMonoIssue(MonoGameContextBase host, bool editorIsPlaying)
            => MonoContextIssueAnalysis.ShouldReport(host, editorIsPlaying);

        private void RefreshTree(IReadOnlyList<GameContext> contexts)
        {
            // 剔除已死 Context 的 id（保活的复用原 id，展开状态跟着 id 走）。
            if (_idByCtx.Count > 0)
            {
                var live = new HashSet<GameContext>(contexts);
                foreach (var dead in _idByCtx.Keys.Where(c => !live.Contains(c)).ToList())
                    _idByCtx.Remove(dead);
            }

            // 容器 → Context 反查；父级 = 沿 Container.Parent 链找到的第一个有主容器
            // （中间可能隔着无 Context 的裸容器——测试等场景直接 new Container，跳过即可）。
            var byContainer = contexts.ToDictionary(c => c.Container);
            var children = new Dictionary<GameContext, List<GameContext>>();
            var roots = new List<GameContext>();
            foreach (var ctx in contexts)
            {
                GameContext parent = null;
                for (var p = ctx.Container.Parent; p != null; p = p.Parent)
                    if (byContainer.TryGetValue(p, out parent))
                        break;
                if (parent == null) roots.Add(ctx);
                else (children.TryGetValue(parent, out var list) ? list : children[parent] = new List<GameContext>()).Add(ctx);
            }

            // 搜索过滤：命中节点 + 其祖先保留。
            HashSet<GameContext> visible = null;
            if (_treeFilter.Length > 0)
            {
                visible = new HashSet<GameContext>();
                foreach (var ctx in contexts)
                    if (MatchesFilter(ctx))
                        visible.Add(ctx);
                // 命中者的祖先补进来（自底向上一遍即可：树是从 roots 下钻的，直接递归标记）。
                bool MarkAncestors(GameContext node)
                {
                    bool keep = visible.Contains(node);
                    if (children.TryGetValue(node, out var kids))
                        foreach (var kid in kids)
                            keep |= MarkAncestors(kid);
                    if (keep) visible.Add(node);
                    return keep;
                }
                foreach (var root in roots) MarkAncestors(root);
            }

            // 结构签名：没变化只重绑可见行（更新计数 / 时长），变了才重建（展开状态按 id 保留）。
            var sig = new StringBuilder(64).Append(_treeFilter).Append('#');
            foreach (var ctx in contexts)
                if (visible == null || visible.Contains(ctx))
                    sig.Append(IdOf(ctx)).Append(',');
            string signature = sig.ToString();

            // 提示语分三态。此前只在「列表为空」时显示解释，恰恰在最需要解释的时候（编辑模式下列表里
            // 堆着一批上局残留）把它藏了起来——看到一堆 Context 却不知道它们是什么，正是这个面板最容易误导人的地方。
            if (EditorApplication.isPlaying || contexts.Count == 0)
            {
                _treeHint.messageType = HelpBoxMessageType.Info;
                _treeHint.text =
                    "进入 Play 模式后，这里展示存活 Context 作用域树。\n" +
                    "退出 Play 后仍留在树上的 Context = 上一局没 Dispose 的泄漏嫌疑（下次进 Play 时清空）。";
                _treeHint.style.display = contexts.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            else
            {
                // 未运行却有存活 Context：登记表刻意持强引用不判活（ADR-0026），故这些就是「创建了却没 Dispose」的。
                // 最常见的来源是刚跑完 PlayMode 测试——测试里 new 的 Context 不少没在 TearDown 里 Dispose。
                _treeHint.messageType = HelpBoxMessageType.Warning;
                _treeHint.text =
                    $"未运行，但仍有 {contexts.Count} 个 Context 存活 —— 它们是**上一次 Play / PlayMode 测试结束时没有 Dispose** 的（泄漏嫌疑）。\n" +
                    "登记表刻意持强引用、不判活：留在这里的正是「创建了却忘记 Dispose」本身，这就是本面板要暴露的东西。\n" +
                    "下次进入 Play 时会自动清空。若你刚跑过 PlayMode 测试，这里通常是测试残留，不代表游戏代码有泄漏。";
                _treeHint.style.display = DisplayStyle.Flex;
            }
            _tree.style.display = contexts.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;

            if (signature == _treeSignature)
            {
                _tree.RefreshItems();
                return;
            }
            _treeSignature = signature;

            List<TreeViewItemData<CtxItem>> Build(IEnumerable<GameContext> nodes) =>
                nodes.Where(n => visible == null || visible.Contains(n))
                    .Select(n => new TreeViewItemData<CtxItem>(
                        IdOf(n),
                        new CtxItem { Ctx = n },
                        children.TryGetValue(n, out var kids) ? Build(kids) : null))
                    .ToList();

            _tree.SetRootItems(Build(roots));
            _tree.Rebuild();

            // 新出现的节点默认展开（老节点尊重用户手动折叠的状态）。
            foreach (var ctx in contexts)
            {
                int id = IdOf(ctx);
                if (_knownIds.Add(id))
                    _tree.ExpandItem(id);
            }
        }

        private bool MatchesFilter(GameContext ctx)
        {
            if (DisplayName(ctx).IndexOf(_treeFilter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var d in ctx.Container.LocalRegistrationDetails)
                if (d.Contract.Name.IndexOf(_treeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            var counts = ctx.EventSubscriptionCounts;
            if (counts != null)
                foreach (var kv in counts)
                    if (kv.Value > 0 && kv.Key.Name.IndexOf(_treeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            return false;
        }

        private int IdOf(GameContext ctx)
        {
            if (!_idByCtx.TryGetValue(ctx, out int id))
                _idByCtx[ctx] = id = _nextId++;
            return id;
        }

        private void RefreshCommands()
        {
            long total = LoggingCommandSystem.TotalRecorded;
            _commandHint.style.display = total == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _commandTable.style.display = total == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (total == 0) _commandDetail.style.display = DisplayStyle.None;

            if (total == _lastTotalRecorded && !_cmdFilterDirty) return;
            _lastTotalRecorded = total;
            _cmdFilterDirty = false;

            LoggingCommandSystem.CopyRecent(_cmdRing);
            _cmdRows.Clear();
            for (int i = _cmdRing.Count - 1; i >= 0; i--) // 新 → 旧，盯屏不用滚
            {
                var e = _cmdRing[i];
                if (_onlyErrors && e.Error == null) continue;
                if (_cmdFilter.Length > 0 &&
                    e.CommandType.IndexOf(_cmdFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    e.ContextName.IndexOf(_cmdFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _cmdRows.Add(e);
            }
            _commandTable.RefreshItems();
        }

        // ── 小工具 ──────────────────────────────────────────────────────────

        private void PingSceneObject(GameContext ctx)
        {
            if (_monoByCtx.TryGetValue(ctx, out var mono) && mono != null)
            {
                EditorGUIUtility.PingObject(mono.gameObject);
                Selection.activeGameObject = mono.gameObject;
            }
        }

        private static void PingSceneObject(MonoGameContextBase mono)
        {
            if (mono == null) return;
            EditorGUIUtility.PingObject(mono.gameObject);
            Selection.activeGameObject = mono.gameObject;
        }

        private static string DisplayName(GameContext ctx)
            => string.IsNullOrEmpty(ctx.DebugName) ? $"GameContext#{ctx.GetHashCode():X}" : ctx.DebugName;

        private static string FormatDuration(double seconds)
            => seconds < 60 ? $"{seconds:F0}s" : $"{(int)(seconds / 60)}m{(int)(seconds % 60)}s";

        private static string FormatClock(float realtimeSeconds)
        {
            var ts = TimeSpan.FromSeconds(realtimeSeconds);
            return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        }

        private static Label MutedLabel(string text) => new(text) { style = { color = ColMuted, fontSize = 11 } };

        // 明细里的折叠节：标题带计数、折叠状态回写窗口字段（重建明细后不丢用户的开合选择）。
        private static Foldout Section(string title, bool open, Action<bool> setOpen)
        {
            var fold = new Foldout { text = title, value = open, style = { marginBottom = 4 } };
            fold.RegisterValueChangedCallback(e =>
            {
                if (e.target == fold) setOpen(e.newValue);
            });
            return fold;
        }

        private static Label Badge(string text, Color bg, string tooltip)
        {
            var l = new Label(text) { tooltip = tooltip };
            var s = l.style;
            s.backgroundColor = bg;
            s.color = Color.white;
            s.fontSize = 9;
            s.marginLeft = 4;
            s.paddingLeft = 4;
            s.paddingRight = 4;
            s.paddingTop = 0;
            s.paddingBottom = 1;
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 6;
            s.alignSelf = Align.Center;
            s.flexShrink = 0;
            s.unityTextAlign = TextAnchor.MiddleCenter;
            return l;
        }

        /// <summary>极简折线趋势图（Painter2D 画线）：定长滑动窗口，Push 即重绘。计数用，无坐标轴。</summary>
        private sealed class Sparkline : VisualElement
        {
            private const int MaxSamples = 60; // 500ms × 60 ≈ 30 秒窗口
            private readonly List<float> _values = new();
            private readonly Color _color;

            public Sparkline(Color color)
            {
                _color = color;
                style.width = 64;
                style.height = 14;
                style.marginLeft = 4;
                style.alignSelf = Align.Center;
                style.flexShrink = 0;
                generateVisualContent += OnGenerate;
            }

            public void Push(float value)
            {
                if (_values.Count > 0 && Mathf.Approximately(_values[^1], value) &&
                    _values.Count >= 2 && Mathf.Approximately(_values[^2], value))
                {
                    // 值持续不变时仍推进窗口（时间轴等距），但避免每帧无谓重绘。
                    _values.Add(value);
                    if (_values.Count > MaxSamples) _values.RemoveAt(0);
                    return;
                }
                _values.Add(value);
                if (_values.Count > MaxSamples) _values.RemoveAt(0);
                MarkDirtyRepaint();
            }

            private void OnGenerate(MeshGenerationContext ctx)
            {
                if (_values.Count < 2) return;
                var r = contentRect;
                float min = float.MaxValue, max = float.MinValue;
                foreach (float v in _values)
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                if (max - min < 1e-3f) { min -= 0.5f; max += 0.5f; } // 平线：居中显示

                var p = ctx.painter2D;
                p.strokeColor = _color;
                p.lineWidth = 1.5f;
                p.lineJoin = LineJoin.Round;
                p.BeginPath();
                for (int i = 0; i < _values.Count; i++)
                {
                    float x = r.xMin + r.width * i / (MaxSamples - 1); // 固定步长：窗口未满时从左侧生长
                    float y = r.yMax - (_values[i] - min) / (max - min) * (r.height - 2f) - 1f;
                    if (i == 0) p.MoveTo(new Vector2(x, y));
                    else p.LineTo(new Vector2(x, y));
                }
                p.Stroke();
            }
        }
    }
}
