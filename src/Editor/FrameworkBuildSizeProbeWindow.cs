using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 选择常见 Module 组合并观察隔离 Player BuildReport；窗口只负责意图与反馈，构建事务由探针拥有。
    /// </summary>
    public sealed class FrameworkBuildSizeProbeWindow : EditorWindow
    {
        private readonly Dictionary<string, Toggle> _profileToggles = new(StringComparer.Ordinal);
        private readonly List<VisualElement> _profileCards = new();
        private readonly List<VisualElement> _metricRows = new();
        private VisualElement _actions;
        private VisualElement _profileGrid;
        private VisualElement _advancedProfileGrid;
        private Foldout _advancedProfilesFoldout;
        private VisualElement _profileLoader;
        private VisualElement _results;
        private Label _environmentSummary;
        private HelpBox _status;
        private Button _loadProfilesButton;
        private Button _startButton;
        private Button _stopButton;
        private bool _profilesLoaded;
        private bool _profilesLoading;
        private bool _advancedProfilesPopulated;
        // 选择是构建意图，不是当前是否已创建 Toggle 的 UI 状态；进阶区折叠和证据刷新都不能丢失它。
        private HashSet<string> _selectedProfileKeys;
        private FrameworkModuleAudit.AuditProfile[] _availableAdvancedProfiles =
            Array.Empty<FrameworkModuleAudit.AuditProfile>();
        private bool? _lastEditorReady;
        private bool? _lastRunning;

        /// <summary>打开或聚焦真实构建体积证据窗口。</summary>
        [MenuItem(FrameworkMenuPaths.BuildSizeProbe, priority = 82)]
        public static void Open() => GetWindow<FrameworkBuildSizeProbeWindow>("真实构建体积证据").Show();

        private void OnEnable()
        {
            FrameworkBuildSizeProbe.Changed += RefreshState;
            FrameworkModuleAuditCache.Invalidated += OnEvidenceInvalidated;
            FrameworkModuleAuditCache.Refreshed += OnEvidenceRefreshed;
        }

        private void OnDisable()
        {
            FrameworkBuildSizeProbe.Changed -= RefreshState;
            FrameworkModuleAuditCache.Invalidated -= OnEvidenceInvalidated;
            FrameworkModuleAuditCache.Refreshed -= OnEvidenceRefreshed;
        }

        private void OnInspectorUpdate()
        {
            RefreshEnvironmentCard();
            RefreshActionAvailability(updateStatusOnChange: true);
        }

        /// <summary>创建支持窄窗纵排的 UI Toolkit 构建控制台。</summary>
        public void CreateGUI()
        {
            minSize = new Vector2(360f, 420f);
            _profileToggles.Clear();
            _profileCards.Clear();
            _metricRows.Clear();
            _profilesLoaded = false;
            _profilesLoading = false;
            _advancedProfilesPopulated = false;
            _selectedProfileKeys = null;
            _availableAdvancedProfiles = Array.Empty<FrameworkModuleAudit.AuditProfile>();
            _environmentSummary = null;

            VisualElement root = rootVisualElement;
            root.Clear();
            FrameworkEditorVisuals.ApplyWindowSurface(root);

            var scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "build-size-probe-content",
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto,
                style = { flexGrow = 1 },
            };
            scroll.contentContainer.style.paddingLeft = 10;
            scroll.contentContainer.style.paddingRight = 10;
            scroll.contentContainer.style.paddingTop = 10;
            scroll.contentContainer.style.paddingBottom = 12;
            root.Add(scroll);

            scroll.Add(FrameworkEditorVisuals.CreateHero(
                "build-size-probe-header",
                "PLAYER BUILD · 隔离删除测试",
                "真实构建体积证据",
                "在 Library 下的隔离空工程里真正删除未选 Module，再用当前平台构建；打开窗口不会扫描工程或启动构建。"));

            scroll.Add(CreateEnvironmentCard());
            scroll.Add(CreateSectionTitle("选择组合"));
            _profileGrid = new VisualElement
            {
                name = "build-size-probe-profiles",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = UnityEngine.UIElements.Wrap.Wrap,
                },
            };

            _profileLoader = FrameworkEditorVisuals.CreateCard(
                "build-size-probe-profile-loader", FrameworkEditorVisuals.Tone.Active);
            _profileLoader.Add(FrameworkEditorVisuals.CreateCardTitle("先读取可构建组合"));
            _profileLoader.Add(FrameworkEditorVisuals.CreateMutedLabel(
                "读取是显式的只读审计，可能短暂停顿；真正点击构建时会重新采集执行证据，" +
                "并且只为所选档位计算源码与 Package 指纹。"));
            _loadProfilesButton = FrameworkEditorVisuals.CreateActionButton(
                "读取可构建组合", ScheduleLoadProfiles,
                "读取当前 Module / Package 关系；不会启动 Player Build。",
                "build-size-probe-load-profiles", primary: true);
            _profileLoader.Add(_loadProfilesButton);
            scroll.Add(_profileLoader);
            scroll.Add(_profileGrid);

            _advancedProfilesFoldout = new Foldout
            {
                name = "build-size-probe-advanced-profiles",
                text = "任意 Module 入口（按需选择，默认不构建）",
                value = false,
                style = { marginTop = 5, marginBottom = 4 },
            };
            _advancedProfilesFoldout.Add(Wrap(new Label(
                "每项以一个 Runtime Module 为入口并自动带上真实依赖闭包；适合验证 Config、Fonts、Proto、Bridge 等任意 Module，不是全局启用开关。")));
            _advancedProfileGrid = new VisualElement
            {
                name = "build-size-probe-module-profiles",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = UnityEngine.UIElements.Wrap.Wrap,
                },
            };
            _advancedProfilesFoldout.Add(_advancedProfileGrid);
            _advancedProfilesFoldout.RegisterValueChangedCallback(change =>
            {
                if (ReferenceEquals(change.target, _advancedProfilesFoldout) && change.newValue)
                    PopulateAdvancedProfiles();
            });
            Toggle advancedTitle = _advancedProfilesFoldout.Q<Toggle>();
            if (advancedTitle == null)
                throw new InvalidOperationException("进阶组合 Foldout 缺少标题 Toggle，无法建立懒构建边界。");
            advancedTitle.RegisterCallback<NavigationMoveEvent>(_ =>
            {
                if (_advancedProfilesFoldout.value) PopulateAdvancedProfiles();
            });
            advancedTitle.RegisterCallback<KeyDownEvent>(_ =>
            {
                if (_advancedProfilesFoldout.value) PopulateAdvancedProfiles();
            });
            scroll.Add(_advancedProfilesFoldout);

            var scope = CreateCard("build-size-probe-scope");
            scope.Add(CreateCardTitle("如何理解数字"));
            scope.Add(CreateBullet("隔离工程只复制所选 Module，未选目录及其 link.xml 不会悄悄进入结果。"));
            scope.Add(CreateBullet("所选程序集完整保留，所以这是可重复的体积上界；实际游戏只用部分能力时通常更小。"));
            scope.Add(CreateBullet("只在相同 Unity、平台、脚本后端、裁剪级别和依赖版本下比较；不要把 Windows 数字外推成 WebGL。"));
            scope.style.marginTop = 8;
            scroll.Add(scope);

            _actions = new VisualElement
            {
                name = "build-size-probe-actions",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    marginTop = 8,
                    marginBottom = 4,
                },
            };
            _startButton = FrameworkEditorVisuals.CreateActionButton("构建所选组合", StartSelected,
                "重新采集执行证据，只为所选组合冻结输入，再顺序启动隔离 Unity 子进程。",
                "build-size-probe-start", primary: true);
            _stopButton = FrameworkEditorVisuals.CreateActionButton(
                "当前完成后停止", FrameworkBuildSizeProbe.RequestStopAfterCurrent,
                "不强杀正在写产物的 Unity；当前组合结束后不再启动后续组合。", "build-size-probe-stop");
            _actions.Add(_startButton);
            _actions.Add(_stopButton);
            _actions.Add(FrameworkEditorVisuals.CreateActionButton(
                "打开最近结果", FrameworkBuildSizeProbe.RevealLatestRun,
                "打开 report.md、report.json、构建日志与玩家产物所在目录。", "build-size-probe-reveal"));
            _actions.Add(FrameworkEditorVisuals.CreateActionButton(
                "返回模块审计", FrameworkModuleAuditWindow.Open,
                "查看原始 DLL 闭包与删除测试。", "build-size-probe-audit"));
            scroll.Add(_actions);

            _status = new HelpBox(string.Empty, HelpBoxMessageType.Info)
            {
                name = "build-size-probe-status",
                style = { marginTop = 4, marginBottom = 8 },
            };
            scroll.Add(_status);

            scroll.Add(CreateSectionTitle("构建结果"));
            _results = new VisualElement { name = "build-size-probe-results" };
            scroll.Add(_results);

            root.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            root.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RefreshState();
            ApplyResponsiveLayout(position.width);
        }

        internal void ApplyResponsiveLayoutForTests(float width) => ApplyResponsiveLayout(width);

        private VisualElement CreateEnvironmentCard()
        {
            var card = CreateCard("build-size-probe-environment");
            card.Add(CreateCardTitle("本轮构建环境"));
            _environmentSummary = Wrap(new Label { name = "build-size-probe-environment-summary" });
            card.Add(_environmentSummary);
            var hint = Wrap(new Label("探针不自动切换平台；想测 WebGL，请先正常切到 WebGL，再从这里构建。"));
            hint.style.color = MutedTextColor;
            hint.style.marginTop = 3;
            card.Add(hint);
            RefreshEnvironmentCard();
            return card;
        }

        private void RefreshEnvironmentCard()
        {
            if (_environmentSummary == null) return;
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            string summary;
            try
            {
                var named = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
                summary = $"{Application.unityVersion} · {target} · " +
                          $"{PlayerSettings.GetScriptingBackend(named)} · " +
                          $"代码裁剪等级（Stripping）{PlayerSettings.GetManagedStrippingLevel(named)}";
            }
            catch (Exception exception)
            {
                summary = $"{Application.unityVersion} · {target} · 无法读取完整环境：{exception.Message}";
            }

            if (!string.Equals(_environmentSummary.text, summary, StringComparison.Ordinal))
                _environmentSummary.text = summary;
        }

        private void ScheduleLoadProfiles()
        {
            if (_profilesLoading) return;
            _profilesLoading = true;
            _loadProfilesButton?.SetEnabled(false);
            _status.text = "正在读取当前 Module / Package 关系；这是显式的只读扫描，不会启动构建。";
            _status.messageType = HelpBoxMessageType.Info;
            rootVisualElement.schedule.Execute(LoadProfilesNow);
        }

        internal void LoadProfilesForTests() => LoadProfilesNow();

        private void LoadProfilesNow()
        {
            _profilesLoading = true;
            try
            {
                // 第一次读取可以复用同一会话里仍有效的 Module 审计；按钮已经显示“刷新”后，
                // 再次点击必须绕过缓存，不能只重画上一份组合。
                FrameworkModuleAuditCache.Entry evidence = _profilesLoaded
                    ? FrameworkModuleAuditCache.Refresh()
                    : FrameworkModuleAuditCache.GetOrRefresh();
                ApplyProfileEvidence(evidence);
            }
            catch (Exception ex)
            {
                ClearProfiles();
                _profilesLoaded = false;
                if (_loadProfilesButton != null) _loadProfilesButton.text = "重新读取可构建组合";
                _status.text = "无法读取模块组合：" + ex.Message;
                _status.messageType = HelpBoxMessageType.Error;
            }
            finally
            {
                _profilesLoading = false;
                _loadProfilesButton?.SetEnabled(true);
                RefreshActionAvailability(updateStatusOnChange: false);
                ApplyResponsiveLayout(position.width);
            }
        }

        private void ApplyProfileEvidence(FrameworkModuleAuditCache.Entry evidence)
        {
            if (evidence?.Result == null) throw new ArgumentNullException(nameof(evidence));
            FrameworkModuleAudit.AuditProfile[] commonProfiles = evidence.Result.CommonProfiles ??
                                                                  Array.Empty<FrameworkModuleAudit.AuditProfile>();
            FrameworkModuleAudit.AuditProfile[] advancedProfiles = evidence.Result.ModuleProfiles ??
                                                                    Array.Empty<FrameworkModuleAudit.AuditProfile>();
            var availableKeys = commonProfiles
                .Concat(evidence.Result.FullProfile == null
                    ? Enumerable.Empty<FrameworkModuleAudit.AuditProfile>()
                    : new[] { evidence.Result.FullProfile })
                .Concat(advancedProfiles)
                .Where(profile => profile != null)
                .Select(profile => profile.Key)
                .ToHashSet(StringComparer.Ordinal);
            _selectedProfileKeys = _selectedProfileKeys == null
                ? commonProfiles.Where(profile => profile != null && profile.Key != "full")
                    .Select(profile => profile.Key)
                    .ToHashSet(StringComparer.Ordinal)
                : _selectedProfileKeys.Where(availableKeys.Contains).ToHashSet(StringComparer.Ordinal);

            ClearProfiles();
            foreach (FrameworkModuleAudit.AuditProfile profile in commonProfiles)
                AddProfile(profile, advanced: false);
            AddProfile(evidence.Result.FullProfile, advanced: false);
            _availableAdvancedProfiles = advancedProfiles;
            if (_advancedProfilesFoldout?.value == true) PopulateAdvancedProfiles();
            _profilesLoaded = true;
            if (_loadProfilesButton != null) _loadProfilesButton.text = "刷新可构建组合";
            int totalProfiles = commonProfiles.Length +
                                (evidence.Result.FullProfile == null ? 0 : 1) +
                                _availableAdvancedProfiles.Length;
            _status.text = $"已读取 {totalProfiles} 个组合（审计耗时 {evidence.DurationSeconds:F1}s）。" +
                           "点击构建时会重新采集执行证据，并只冻结所选档位。";
            _status.messageType = HelpBoxMessageType.Info;
            RefreshEnvironmentCard();
        }

        private void ClearProfiles()
        {
            _profileToggles.Clear();
            _profileCards.Clear();
            _advancedProfilesPopulated = false;
            _availableAdvancedProfiles = Array.Empty<FrameworkModuleAudit.AuditProfile>();
            _profileGrid?.Clear();
            _advancedProfileGrid?.Clear();
        }

        private void PopulateAdvancedProfiles()
        {
            if (_advancedProfilesPopulated || _advancedProfileGrid == null) return;
            foreach (FrameworkModuleAudit.AuditProfile profile in _availableAdvancedProfiles)
                AddProfile(profile, advanced: true);
            _advancedProfilesPopulated = true;
            RefreshActionAvailability(updateStatusOnChange: false);
            ApplyResponsiveLayout(position.width);
        }

        private void OnEvidenceInvalidated()
        {
            RefreshEnvironmentCard();
            if (_profileGrid == null || _profilesLoading) return;
            ClearProfiles();
            _profilesLoaded = false;
            if (_loadProfilesButton != null)
            {
                _loadProfilesButton.text = "重新读取可构建组合";
            }
            _status.text = "工程、Package、构建目标或编译图已经变化，组合预览已失效；最近构建结果仍可查看。";
            _status.messageType = HelpBoxMessageType.Warning;
            RefreshActionAvailability(updateStatusOnChange: false);
        }

        private void OnEvidenceRefreshed(FrameworkModuleAuditCache.Entry evidence)
        {
            RefreshEnvironmentCard();
            if (_profileGrid == null || _profilesLoading) return;
            ApplyProfileEvidence(evidence);
            RefreshActionAvailability(updateStatusOnChange: false);
            ApplyResponsiveLayout(position.width);
        }

        private void AddProfile(FrameworkModuleAudit.AuditProfile profile, bool advanced)
        {
            if (profile == null) return;
            var card = CreateCard("build-size-probe-profile-" + profile.Key);
            card.style.flexBasis = 280;
            card.style.flexGrow = 1;
            card.style.minWidth = 0;
            card.style.marginLeft = 3;
            card.style.marginRight = 3;
            card.style.marginTop = 3;
            card.style.marginBottom = 3;

            var toggle = new Toggle(profile.Title)
            {
                value = _selectedProfileKeys?.Contains(profile.Key) == true,
                name = "build-size-probe-toggle-" + profile.Key,
                tooltip = profile.Description,
            };
            toggle.RegisterValueChangedCallback(change =>
            {
                if (change.newValue) _selectedProfileKeys?.Add(profile.Key);
                else _selectedProfileKeys?.Remove(profile.Key);
                RefreshActionAvailability(updateStatusOnChange: false);
            });
            toggle.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(toggle);
            var description = Wrap(new Label(profile.Description));
            description.style.color = MutedTextColor;
            description.style.marginTop = 3;
            card.Add(description);
            card.Add(Wrap(new Label(
                $"{profile.Footprint.FrameworkAssemblies.Count} 个框架模块（Framework Module）")));

            _profileToggles.Add(profile.Key, toggle);
            _profileCards.Add(card);
            (advanced ? _advancedProfileGrid : _profileGrid).Add(card);
        }

        private void StartSelected()
        {
            try
            {
                FrameworkBuildSizeProbe.Start(_selectedProfileKeys ?? Enumerable.Empty<string>());
            }
            catch (Exception ex)
            {
                _status.text = "无法启动：" + ex.Message;
                _status.messageType = HelpBoxMessageType.Error;
            }
        }

        private void RefreshState()
        {
            if (_status == null || _results == null) return;
            RefreshActionAvailability(updateStatusOnChange: false);
            bool running = FrameworkBuildSizeProbe.IsRunning;
            bool editorReady = FrameworkEditorOperationGate.CanStart(
                requireEditMode: true,
                out string blockedReason);

            FrameworkBuildSizeProbe.RunReport report =
                FrameworkBuildSizeProbe.CurrentReport ?? FrameworkBuildSizeProbe.LoadLatestReport();
            if (running)
            {
                _status.text = FrameworkBuildSizeProbe.StopAfterCurrentRequested
                    ? "正在等待当前组合安全结束，之后停止。你可以继续使用主工程。"
                    : "隔离 Unity 子进程正在工作。切换主 Unity 到后台不会影响它；详情见各组合日志。";
                _status.messageType = HelpBoxMessageType.Info;
            }
            else if (!editorReady)
            {
                _status.text = "当前不能启动构建探针：" + blockedReason + " 等待 Unity 空闲并保持 Edit Mode 后重试。";
                _status.messageType = HelpBoxMessageType.Warning;
            }
            else if (!_profilesLoaded)
            {
                _status.text = report == null
                    ? "窗口已就绪，尚未扫描工程。先读取可构建组合；此操作只读且不会启动 Player Build。"
                    : "组合尚未读取；下面仍显示最近一轮结果。需要新建任务时，先显式读取当前组合。";
                _status.messageType = HelpBoxMessageType.Info;
            }
            else if (report == null)
            {
                _status.text = "尚无构建记录。默认选中 Core、UGUI 与 Toolkit，全部模块作为可选上界。";
                _status.messageType = HelpBoxMessageType.Info;
            }
            else
            {
                int failures = report.Profiles.Count(record => record.Status == "失败");
                _status.text = failures == 0
                    ? "最近一轮已结束。优先比较相对 Core 的差值，不要只看玩家空壳总大小。"
                    : $"最近一轮有 {failures} 个组合失败；打开结果目录查看对应日志，成功组合仍可保留参考。";
                _status.messageType = failures == 0 ? HelpBoxMessageType.Info : HelpBoxMessageType.Warning;
            }
            BuildResults(report);
        }

        private void RefreshActionAvailability(bool updateStatusOnChange)
        {
            if (_status == null) return;
            bool running = FrameworkBuildSizeProbe.IsRunning;
            bool editorReady = FrameworkEditorOperationGate.CanStart(
                requireEditMode: true,
                out string blockedReason);
            foreach (Toggle toggle in _profileToggles.Values) toggle.SetEnabled(!running);
            _loadProfilesButton?.SetEnabled(!_profilesLoading && !running);
            _startButton?.SetEnabled(
                !running && editorReady && _profilesLoaded && _selectedProfileKeys?.Count > 0);
            _stopButton?.SetEnabled(running && !FrameworkBuildSizeProbe.StopAfterCurrentRequested);

            bool stateChanged = _lastRunning != running || _lastEditorReady != editorReady;
            _lastRunning = running;
            _lastEditorReady = editorReady;
            if (!updateStatusOnChange || !stateChanged) return;

            if (running)
            {
                _status.text = FrameworkBuildSizeProbe.StopAfterCurrentRequested
                    ? "正在等待当前组合安全结束，之后停止。你可以继续使用主工程。"
                    : "隔离 Unity 子进程正在工作。切换主 Unity 到后台不会影响它。";
                _status.messageType = HelpBoxMessageType.Info;
            }
            else if (!editorReady)
            {
                _status.text = "当前不能启动构建探针：" + blockedReason + " 等待 Unity 空闲并保持 Edit Mode 后重试。";
                _status.messageType = HelpBoxMessageType.Warning;
            }
            else
            {
                _status.text = _profilesLoaded
                    ? "Unity 已空闲；可以构建所选组合。启动时会重新采集执行证据。"
                    : "Unity 已空闲；先读取当前可构建组合。";
                _status.messageType = HelpBoxMessageType.Info;
            }
        }

        private void BuildResults(FrameworkBuildSizeProbe.RunReport report)
        {
            _results.Clear();
            _metricRows.Clear();
            if (report?.Profiles == null || report.Profiles.Length == 0)
            {
                var empty = Wrap(new Label("运行后会在这里显示状态、最终输出大小、相对 Core 差值和耗时。"));
                empty.style.color = MutedTextColor;
                _results.Add(empty);
                return;
            }

            var core = report.Profiles.FirstOrDefault(record => record.Key == "core" && record.Status == "成功");
            foreach (var record in report.Profiles)
            {
                var card = CreateCard("build-size-probe-result-" + record.Key);
                card.style.borderLeftWidth = 4;
                card.style.borderLeftColor = StatusColor(record.Status);
                var heading = new VisualElement { style = { flexDirection = FlexDirection.Row } };
                var title = Wrap(new Label(record.Title));
                title.style.flexGrow = 1;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                heading.Add(title);
                var status = new Label(record.Status ?? "等待");
                status.style.flexShrink = 0;
                status.style.color = StatusColor(record.Status);
                heading.Add(status);
                card.Add(heading);

                if (record.Status == "成功")
                {
                    long delta = core == null ? 0L : record.OutputBytes - core.OutputBytes;
                    string deltaValue = core == null
                        ? "—"
                        : (delta > 0 ? "+" : string.Empty) + FrameworkBuildSizeProbe.FormatBytes(delta);
                    var metrics = new VisualElement
                    {
                        name = "build-size-probe-result-metrics-" + record.Key,
                        style =
                        {
                            flexDirection = FlexDirection.Row,
                            flexWrap = UnityEngine.UIElements.Wrap.NoWrap,
                            marginTop = 6,
                        },
                    };
                    metrics.Add(FrameworkEditorVisuals.CreateMetric(
                        "build-size-probe-output-" + record.Key,
                        "可发布输出", FrameworkBuildSizeProbe.FormatBytes(record.OutputBytes), "默认比较口径"));
                    metrics.Add(FrameworkEditorVisuals.CreateMetric(
                        "build-size-probe-report-" + record.Key,
                        "BuildReport 总量", FrameworkBuildSizeProbe.FormatBytes(record.BuildReportBytes), "含构建中间证据"));
                    metrics.Add(FrameworkEditorVisuals.CreateMetric(
                        "build-size-probe-delta-" + record.Key,
                        "相对 Core", deltaValue, core == null ? "需同轮构建 Core" : "相同环境下的差值"));
                    metrics.Add(FrameworkEditorVisuals.CreateMetric(
                        "build-size-probe-duration-" + record.Key,
                        "耗时", record.DurationSeconds.ToString("F1") + "s", "Unity 子进程"));
                    _metricRows.Add(metrics);
                    card.Add(metrics);
                }
                var message = Wrap(new Label(record.Message ?? string.Empty));
                message.style.color = MutedTextColor;
                message.style.marginTop = 3;
                card.Add(message);
                card.style.marginBottom = 6;
                _results.Add(card);
            }
        }

        private void OnGeometryChanged(GeometryChangedEvent evt) => ApplyResponsiveLayout(evt.newRect.width);

        private void ApplyResponsiveLayout(float width)
        {
            bool compact = width < FrameworkEditorVisuals.CompactWidth;
            if (_actions != null)
            {
                _actions.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
                FrameworkEditorVisuals.ApplyResponsiveChildren(_actions, compact);
            }
            if (_profileGrid != null)
                _profileGrid.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
            if (_advancedProfileGrid != null)
                _advancedProfileGrid.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
            foreach (var card in _profileCards)
            {
                card.style.flexBasis = compact ? StyleKeyword.Auto : 280;
                card.style.flexGrow = compact ? 0 : 1;
            }
            foreach (VisualElement row in _metricRows)
            {
                row.style.flexDirection = FlexDirection.Row;
                row.style.flexWrap = compact
                    ? UnityEngine.UIElements.Wrap.Wrap
                    : UnityEngine.UIElements.Wrap.NoWrap;
                foreach (VisualElement metric in row.Children())
                {
                    metric.style.flexBasis = compact ? new Length(46, LengthUnit.Percent) : 0;
                    metric.style.flexGrow = 1;
                }
            }
        }

        private static Label CreateSectionTitle(string text)
            => FrameworkEditorVisuals.CreateSectionTitle(text);

        private static Label CreateCardTitle(string text)
            => FrameworkEditorVisuals.CreateCardTitle(text);

        private static Label CreateBullet(string text)
            => FrameworkEditorVisuals.CreateBullet(text);

        private static VisualElement CreateCard(string name)
            => FrameworkEditorVisuals.CreateCard(name);

        private static Label Wrap(Label label)
            => FrameworkEditorVisuals.Wrap(label);

        private static Color StatusColor(string status) => status switch
        {
            "成功" => HealthyColor,
            "失败" => WarningColor,
            "构建中" => ActiveColor,
            _ => MutedTextColor,
        };

        private static Color MutedTextColor => FrameworkEditorVisuals.MutedTextColor;
        private static Color HealthyColor => FrameworkEditorVisuals.HealthyTextColor;
        private static Color WarningColor => FrameworkEditorVisuals.ErrorTextColor;
        private static Color ActiveColor => FrameworkEditorVisuals.ActiveTextColor;
    }
}
