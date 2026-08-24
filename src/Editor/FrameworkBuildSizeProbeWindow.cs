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
        private const float CompactWidth = 620f;

        private readonly Dictionary<string, Toggle> _profileToggles = new(StringComparer.Ordinal);
        private readonly List<VisualElement> _profileCards = new();
        private VisualElement _actions;
        private VisualElement _profileGrid;
        private VisualElement _advancedProfileGrid;
        private VisualElement _results;
        private HelpBox _status;
        private Button _startButton;
        private Button _stopButton;

        /// <summary>打开或聚焦真实构建体积证据窗口。</summary>
        [MenuItem("SSFramework/诊断/真实构建体积证据", priority = 21)]
        public static void Open() => GetWindow<FrameworkBuildSizeProbeWindow>("真实构建体积证据").Show();

        private void OnEnable() => FrameworkBuildSizeProbe.Changed += RefreshState;
        private void OnDisable() => FrameworkBuildSizeProbe.Changed -= RefreshState;

        /// <summary>创建支持窄窗纵排的 UI Toolkit 构建控制台。</summary>
        public void CreateGUI()
        {
            minSize = new Vector2(360f, 420f);
            _profileToggles.Clear();
            _profileCards.Clear();

            VisualElement root = rootVisualElement;
            root.Clear();
            root.style.backgroundColor = WindowBackground;
            root.style.flexDirection = FlexDirection.Column;

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

            var title = Wrap(new Label("真实构建体积证据"));
            title.style.fontSize = 19;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            scroll.Add(title);
            var subtitle = Wrap(new Label(
                "在 Library 下的隔离空工程里真正删除未选 Module，再用当前平台构建；主工程场景、Build Settings 与 HybridCLR 配置都不会改变。"));
            subtitle.style.color = MutedTextColor;
            subtitle.style.marginTop = 4;
            subtitle.style.marginBottom = 8;
            scroll.Add(subtitle);

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
            scroll.Add(_profileGrid);

            var advancedProfiles = new Foldout
            {
                name = "build-size-probe-advanced-profiles",
                text = "任意 Module 入口（按需选择，默认不构建）",
                value = false,
                style = { marginTop = 5, marginBottom = 4 },
            };
            advancedProfiles.Add(Wrap(new Label(
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
            advancedProfiles.Add(_advancedProfileGrid);
            scroll.Add(advancedProfiles);

            try
            {
                foreach (var plan in FrameworkBuildSizeProbe.CreatePlans())
                    AddProfile(plan);
            }
            catch (Exception ex)
            {
                var failure = new HelpBox("无法读取模块组合：" + ex.Message, HelpBoxMessageType.Error);
                _profileGrid.Add(failure);
            }

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
            _startButton = CreateActionButton("构建所选组合", StartSelected,
                "顺序启动隔离 Unity 子进程；IL2CPP 首次构建可能需要较长时间。", "build-size-probe-start");
            _stopButton = CreateActionButton("当前完成后停止", FrameworkBuildSizeProbe.RequestStopAfterCurrent,
                "不强杀正在写产物的 Unity；当前组合结束后不再启动后续组合。", "build-size-probe-stop");
            _actions.Add(_startButton);
            _actions.Add(_stopButton);
            _actions.Add(CreateActionButton("打开最近结果", FrameworkBuildSizeProbe.RevealLatestRun,
                "打开 report.md、report.json、构建日志与玩家产物所在目录。", "build-size-probe-reveal"));
            _actions.Add(CreateActionButton("返回模块审计", FrameworkModuleAuditWindow.Open,
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

        private static VisualElement CreateEnvironmentCard()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            var named = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
            var card = CreateCard("build-size-probe-environment");
            card.Add(CreateCardTitle("本轮构建环境"));
            card.Add(Wrap(new Label($"{Application.unityVersion} · {target} · " +
                                    $"{PlayerSettings.GetScriptingBackend(named)} · " +
                                    $"Stripping {PlayerSettings.GetManagedStrippingLevel(named)}")));
            var hint = Wrap(new Label("探针不自动切换平台；想测 WebGL，请先正常切到 WebGL，再从这里构建。"));
            hint.style.color = MutedTextColor;
            hint.style.marginTop = 3;
            card.Add(hint);
            return card;
        }

        private void AddProfile(FrameworkBuildSizeProbe.ProfilePlan plan)
        {
            var card = CreateCard("build-size-probe-profile-" + plan.Key);
            card.style.flexBasis = 280;
            card.style.flexGrow = 1;
            card.style.minWidth = 0;
            card.style.marginLeft = 3;
            card.style.marginRight = 3;
            card.style.marginTop = 3;
            card.style.marginBottom = 3;

            var toggle = new Toggle(plan.Title)
            {
                value = !plan.IsAdvanced && plan.Key != "full",
                name = "build-size-probe-toggle-" + plan.Key,
                tooltip = plan.Description,
            };
            toggle.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(toggle);
            var description = Wrap(new Label(plan.Description));
            description.style.color = MutedTextColor;
            description.style.marginTop = 3;
            card.Add(description);
            card.Add(Wrap(new Label($"{plan.Assemblies.Length} 个 Framework Module")));

            _profileToggles.Add(plan.Key, toggle);
            _profileCards.Add(card);
            (plan.IsAdvanced ? _advancedProfileGrid : _profileGrid).Add(card);
        }

        private void StartSelected()
        {
            try
            {
                FrameworkBuildSizeProbe.Start(_profileToggles
                    .Where(pair => pair.Value.value)
                    .Select(pair => pair.Key));
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
            bool running = FrameworkBuildSizeProbe.IsRunning;
            foreach (var toggle in _profileToggles.Values) toggle.SetEnabled(!running);
            _startButton?.SetEnabled(!running && _profileToggles.Count > 0);
            _stopButton?.SetEnabled(running && !FrameworkBuildSizeProbe.StopAfterCurrentRequested);

            FrameworkBuildSizeProbe.RunReport report =
                FrameworkBuildSizeProbe.CurrentReport ?? FrameworkBuildSizeProbe.LoadLatestReport();
            if (running)
            {
                _status.text = FrameworkBuildSizeProbe.StopAfterCurrentRequested
                    ? "正在等待当前组合安全结束，之后停止。你可以继续使用主工程。"
                    : "隔离 Unity 子进程正在工作。切换主 Unity 到后台不会影响它；详情见各组合日志。";
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

        private void BuildResults(FrameworkBuildSizeProbe.RunReport report)
        {
            _results.Clear();
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
                    string deltaText = core == null
                        ? "需要同时构建 Core 才能计算差值"
                        : (delta > 0 ? "+" : string.Empty) + FrameworkBuildSizeProbe.FormatBytes(delta) + " 相对 Core";
                    card.Add(Wrap(new Label(
                        $"可发布输出 {FrameworkBuildSizeProbe.FormatBytes(record.OutputBytes)} · " +
                        $"BuildReport 总量 {FrameworkBuildSizeProbe.FormatBytes(record.BuildReportBytes)} · {deltaText} · " +
                        $"{record.DurationSeconds:F1}s")));
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
            bool compact = width < CompactWidth;
            if (_actions != null)
            {
                _actions.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
                foreach (var child in _actions.Children())
                {
                    child.style.flexBasis = compact ? StyleKeyword.Auto : 0;
                    child.style.flexGrow = compact ? 0 : 1;
                }
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
        }

        private static Button CreateActionButton(string text, Action action, string tooltip, string name)
        {
            return new Button(action)
            {
                text = text,
                tooltip = tooltip,
                name = name,
                style =
                {
                    flexBasis = 0,
                    flexGrow = 1,
                    minHeight = 28,
                    marginLeft = 2,
                    marginRight = 2,
                    marginTop = 2,
                    marginBottom = 2,
                },
            };
        }

        private static Label CreateSectionTitle(string text)
        {
            var label = Wrap(new Label(text));
            label.style.fontSize = 14;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 8;
            label.style.marginBottom = 4;
            return label;
        }

        private static Label CreateCardTitle(string text)
        {
            var label = Wrap(new Label(text));
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginBottom = 3;
            return label;
        }

        private static Label CreateBullet(string text)
        {
            var label = Wrap(new Label("• " + text));
            label.style.marginTop = 2;
            label.style.marginBottom = 2;
            return label;
        }

        private static VisualElement CreateCard(string name)
        {
            return new VisualElement
            {
                name = name,
                style =
                {
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 8,
                    paddingBottom = 8,
                    backgroundColor = CardBackground,
                    borderLeftWidth = 1,
                    borderRightWidth = 1,
                    borderTopWidth = 1,
                    borderBottomWidth = 1,
                    borderLeftColor = BorderColor,
                    borderRightColor = BorderColor,
                    borderTopColor = BorderColor,
                    borderBottomColor = BorderColor,
                    borderTopLeftRadius = 6,
                    borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6,
                    borderBottomRightRadius = 6,
                },
            };
        }

        private static Label Wrap(Label label)
        {
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1;
            return label;
        }

        private static Color StatusColor(string status) => status switch
        {
            "成功" => HealthyColor,
            "失败" => WarningColor,
            "构建中" => ActiveColor,
            _ => MutedTextColor,
        };

        private static Color WindowBackground => EditorGUIUtility.isProSkin
            ? new Color(0.115f, 0.115f, 0.115f, 1f)
            : new Color(0.82f, 0.82f, 0.82f, 1f);

        private static Color CardBackground => EditorGUIUtility.isProSkin
            ? new Color(0.16f, 0.16f, 0.16f, 1f)
            : new Color(0.94f, 0.94f, 0.94f, 1f);

        private static Color BorderColor => EditorGUIUtility.isProSkin
            ? new Color(0.28f, 0.28f, 0.28f, 1f)
            : new Color(0.68f, 0.68f, 0.68f, 1f);

        private static Color MutedTextColor => EditorGUIUtility.isProSkin
            ? new Color(0.68f, 0.68f, 0.68f, 1f)
            : new Color(0.32f, 0.32f, 0.32f, 1f);

        private static Color HealthyColor => EditorGUIUtility.isProSkin
            ? new Color(0.42f, 0.88f, 0.58f, 1f)
            : new Color(0.05f, 0.38f, 0.16f, 1f);

        private static Color WarningColor => EditorGUIUtility.isProSkin
            ? new Color(1f, 0.58f, 0.30f, 1f)
            : new Color(0.66f, 0.20f, 0.05f, 1f);

        private static Color ActiveColor => EditorGUIUtility.isProSkin
            ? new Color(0.35f, 0.72f, 1f, 1f)
            : new Color(0.05f, 0.36f, 0.70f, 1f);
    }
}
