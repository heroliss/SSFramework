using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 先给出模块边界结论与行动建议，再按需展开程序集闭包和原始报告。
    /// </summary>
    public sealed class FrameworkModuleAuditWindow : EditorWindow
    {
        private const float CompactWidth = 620f;

        private VisualElement _actions;
        private ScrollView _content;
        private HelpBox _status;
        private List<VisualElement> _responsiveRows;
        private string _rawReport = string.Empty;

        /// <summary>打开或聚焦 Module 裁剪审计窗口。</summary>
        [MenuItem("SSFramework/诊断/模块裁剪审计", priority = 20)]
        public static void Open() => GetWindow<FrameworkModuleAuditWindow>("模块裁剪审计").Show();

        /// <summary>构建可响应窗口宽度的 UI Toolkit 诊断界面。</summary>
        public void CreateGUI()
        {
            minSize = new Vector2(340f, 360f);
            _responsiveRows = new List<VisualElement>();

            var root = rootVisualElement;
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;
            root.style.backgroundColor = WindowBackground;

            root.Add(CreateHeader());

            _actions = new VisualElement
            {
                name = "module-audit-actions",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexShrink = 0,
                    paddingLeft = 8,
                    paddingRight = 8,
                    paddingTop = 4,
                    paddingBottom = 4,
                },
            };
            _actions.Add(CreateActionButton("重新检测", Refresh, "重新读取当前 Player 编译图和 DLL 引用。"));
            _actions.Add(CreateActionButton("复制完整报告", CopyReport, "复制可粘贴到 issue 或评审中的纯文本报告。"));
            _actions.Add(CreateActionButton("打开模块地图", () => OpenAsset("docs/framework-module-map.md"),
                "查看各程序集的职责、依赖方向与删除标准。"));
            root.Add(_actions);

            _status = new HelpBox("正在读取当前目标平台的模块关系……", HelpBoxMessageType.Info)
            {
                name = "module-audit-status",
                style =
                {
                    flexShrink = 0,
                    marginLeft = 8,
                    marginRight = 8,
                    marginTop = 2,
                    marginBottom = 4,
                },
            };
            root.Add(_status);

            _content = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "module-audit-content",
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto,
                style = { flexGrow = 1 },
            };
            _content.contentContainer.style.paddingLeft = 10;
            _content.contentContainer.style.paddingRight = 10;
            _content.contentContainer.style.paddingTop = 4;
            _content.contentContainer.style.paddingBottom = 12;
            root.Add(_content);

            root.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            Refresh();
        }

        internal void ApplyResponsiveLayoutForTests(float width) => ApplyResponsiveLayout(width);

        private VisualElement CreateHeader()
        {
            var header = new VisualElement
            {
                name = "module-audit-header",
                style =
                {
                    flexShrink = 0,
                    paddingLeft = 12,
                    paddingRight = 12,
                    paddingTop = 10,
                    paddingBottom = 6,
                },
            };
            var title = Wrap(new Label("模块裁剪审计"));
            title.style.fontSize = 19;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(title);

            var subtitle = Wrap(new Label("先回答“能不能按需选、哪里最值得关注、下一步做什么”；技术明细需要时再展开。"));
            subtitle.style.marginTop = 3;
            subtitle.style.color = MutedTextColor;
            header.Add(subtitle);
            return header;
        }

        private static Button CreateActionButton(string text, Action action, string tooltip)
        {
            return new Button(action)
            {
                text = text,
                tooltip = tooltip,
                style =
                {
                    flexGrow = 1,
                    flexBasis = 0,
                    minHeight = 26,
                    marginLeft = 2,
                    marginRight = 2,
                    marginTop = 2,
                    marginBottom = 2,
                },
            };
        }

        private void Refresh()
        {
            if (_content == null) return;
            try
            {
                var result = FrameworkModuleAudit.Analyze(FrameworkModuleAudit.Capture());
                _rawReport = FrameworkModuleAudit.CreateReport(result);
                BuildResult(result);
                _status.text = result.IsHealthy
                    ? "检测完成：当前模块边界健康。大小数字用于寻找候选，不代表最终包体。"
                    : "检测完成：发现需要确认的问题。请先看顶部结论和检查结果。";
                _status.messageType = result.IsHealthy ? HelpBoxMessageType.Info : HelpBoxMessageType.Warning;
            }
            catch (Exception ex)
            {
                _rawReport = ex.ToString();
                BuildFailure(ex);
                _status.text = "检测失败：没有用空结果冒充通过。请展开异常信息定位编译图或 DLL 读取问题。";
                _status.messageType = HelpBoxMessageType.Error;
            }

            float width = rootVisualElement.resolvedStyle.width;
            ApplyResponsiveLayout(float.IsNaN(width) || width <= 0f ? position.width : width);
        }

        private void BuildResult(FrameworkModuleAudit.AuditResult result)
        {
            _content.Clear();
            _responsiveRows.Clear();

            AddOverview(result);
            AddSectionTitle("值得关注");
            var recommendations = CreateCard("module-audit-recommendations");
            foreach (string recommendation in result.Recommendations)
                recommendations.Add(CreateBullet(recommendation));
            _content.Add(recommendations);

            AddSectionTitle("常用组合");
            foreach (var profile in result.CommonProfiles)
                _content.Add(CreateProfileCard(profile));

            var advanced = new Foldout
            {
                name = "module-audit-advanced-profiles",
                text = "完整模块与当前热更配置（进阶）",
                value = false,
                style = { marginTop = 8, marginBottom = 4 },
            };
            advanced.Add(CreateProfileCard(result.FullProfile));
            if (result.HotUpdateProfile != null)
                advanced.Add(CreateProfileCard(result.HotUpdateProfile));
            else
                advanced.Add(CreateInfoLabel(result.HotUpdateNote));
            _content.Add(advanced);

            AddSectionTitle("边界检查");
            _content.Add(CreateChecksCard(result));

            var raw = new Foldout
            {
                name = "module-audit-raw-details",
                text = "技术明细与原始报告",
                value = false,
                style = { marginTop = 10 },
            };
            raw.Add(CreateInfoLabel("这里保留适合排查和粘贴到 issue 的完整文本。日常判断通常不需要展开。"));
            var rawText = Wrap(new Label(_rawReport));
            rawText.name = "module-audit-raw-report";
            rawText.style.fontSize = 11;
            rawText.style.paddingLeft = 8;
            rawText.style.paddingRight = 8;
            rawText.style.paddingTop = 6;
            rawText.style.paddingBottom = 6;
            rawText.style.backgroundColor = DetailBackground;
            raw.Add(rawText);
            _content.Add(raw);
        }

        private void AddOverview(FrameworkModuleAudit.AuditResult result)
        {
            var card = CreateCard("module-audit-summary");
            card.style.borderLeftWidth = 4;
            card.style.borderLeftColor = result.IsHealthy ? HealthyColor : WarningColor;

            var title = Wrap(new Label(result.IsHealthy ? "✓ 当前模块边界健康" : "⚠ 当前模块边界需要关注"));
            title.style.fontSize = 17;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = result.IsHealthy ? HealthyTextColor : WarningTextColor;
            card.Add(title);

            var explanation = Wrap(new Label(result.IsHealthy
                ? "核心、UGUI、Toolkit 可以按需选择；没有发现隐式外部引用或反向拖入。"
                : "至少有一项依赖可见性、程序集定位或删除检查未通过。下面会给出处理顺序。"));
            explanation.style.marginTop = 3;
            explanation.style.color = MutedTextColor;
            card.Add(explanation);

            var metrics = CreateResponsiveRow("module-audit-summary-metrics");
            int implicitCount = result.DependencyIssues.Sum(issue => issue.References.Length);
            int passedChecks = result.DeletionChecks.Count(check => check.Passed);
            metrics.Add(CreateMetric("运行时模块", result.RuntimeModules.Length.ToString(), "都应由消费方显式选择"));
            metrics.Add(CreateMetric("隐式外部引用", implicitCount.ToString(), implicitCount == 0 ? "没有隐藏代价" : "需要补进 asmdef"));
            metrics.Add(CreateMetric("删除检查", $"{passedChecks}/{result.DeletionChecks.Length}", "核心与两套 UI 后端"));
            card.Add(metrics);
            _content.Add(card);
        }

        private VisualElement CreateProfileCard(FrameworkModuleAudit.AuditProfile profile)
        {
            var card = CreateCard("module-audit-profile-" + profile.Key);
            var title = Wrap(new Label(profile.Title));
            title.style.fontSize = 15;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(title);

            var description = Wrap(new Label(profile.Description));
            description.style.color = MutedTextColor;
            description.style.marginTop = 2;
            card.Add(description);

            var metrics = CreateResponsiveRow("module-audit-profile-metrics-" + profile.Key);
            metrics.Add(CreateMetric("框架代码", FrameworkModuleAudit.FormatBytes(profile.Footprint.FrameworkBytes),
                $"{profile.Footprint.FrameworkAssemblies.Count} 个程序集"));
            if (profile.Footprint.ProjectBytes > 0)
                metrics.Add(CreateMetric("项目代码", FrameworkModuleAudit.FormatBytes(profile.Footprint.ProjectBytes),
                    $"{profile.Footprint.ProjectAssemblies.Count} 个程序集"));
            metrics.Add(CreateMetric("外部依赖", FrameworkModuleAudit.FormatBytes(profile.Footprint.ExternalBytes),
                "原始 DLL，非最终包体"));
            card.Add(metrics);

            var details = new Foldout
            {
                text = "查看包含内容",
                value = false,
                style = { marginTop = 5 },
            };
            AddStringList(details, "入口程序集", profile.Roots);
            AddStringList(details, "Framework 程序集", profile.Footprint.FrameworkAssemblies);
            if (profile.Footprint.ProjectAssemblies.Count > 0)
                AddStringList(details, "项目程序集", profile.Footprint.ProjectAssemblies);
            if (profile.Footprint.ExternalAssemblies.Count > 0)
            {
                details.Add(CreateDetailHeading("外部依赖（由大到小）"));
                foreach (var pair in profile.Footprint.ExternalAssemblies
                             .OrderByDescending(pair => pair.Value)
                             .ThenBy(pair => pair.Key, StringComparer.Ordinal))
                    details.Add(CreateDetailLine(pair.Key, FrameworkModuleAudit.FormatBytes(pair.Value)));
            }
            if (profile.Footprint.UnresolvedAssemblies.Count > 0)
                details.Add(new HelpBox("无法定位：" + string.Join("、", profile.Footprint.UnresolvedAssemblies),
                    HelpBoxMessageType.Warning));
            card.Add(details);
            return card;
        }

        private VisualElement CreateChecksCard(FrameworkModuleAudit.AuditResult result)
        {
            var card = CreateCard("module-audit-checks");
            card.Add(CreateCheckRow(result.AllRuntimeModulesOptIn,
                "运行时模块由消费方显式选择",
                result.AllRuntimeModulesOptIn
                    ? "所有 Runtime Module 都是 autoReferenced:false。"
                    : "有模块仍会被 Unity 自动加入编译可见范围。"));
            card.Add(CreateCheckRow(result.DependencyIssues.Length == 0,
                "外部依赖都写进 asmdef",
                result.DependencyIssues.Length == 0
                    ? "代码真实使用的外部 DLL 都能从模块声明直接看见。"
                    : string.Join("；", result.DependencyIssues.Select(issue =>
                        issue.ModuleName + " → " + string.Join("、", issue.References)))));
            foreach (var check in result.DeletionChecks)
                card.Add(CreateCheckRow(check.Passed, check.Name, check.Explanation));
            card.Add(CreateCheckRow(!result.HasUnresolvedAssemblies,
                "报告没有缺失的程序集文件",
                result.HasUnresolvedAssemblies
                    ? "至少一个闭包节点无法定位，当前大小统计不完整。"
                    : "常用组合、完整组合和热更配置都能解析到实际程序集。"));
            return card;
        }

        private void BuildFailure(Exception ex)
        {
            _content.Clear();
            _responsiveRows.Clear();
            var card = CreateCard("module-audit-failure");
            var title = Wrap(new Label("无法完成检测"));
            title.style.fontSize = 17;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = WarningTextColor;
            card.Add(title);
            card.Add(CreateInfoLabel("审计不会把读取失败当成“零依赖”。通常先确认 Unity 已完成编译，再点“重新检测”。"));
            var details = new Foldout { text = "查看异常信息", value = false };
            details.Add(CreateInfoLabel(ex.ToString()));
            card.Add(details);
            _content.Add(card);
        }

        private void AddSectionTitle(string text)
        {
            var title = Wrap(new Label(text));
            title.style.fontSize = 15;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginTop = 11;
            title.style.marginBottom = 4;
            _content.Add(title);
        }

        private VisualElement CreateResponsiveRow(string name)
        {
            var row = new VisualElement
            {
                name = name,
                style =
                {
                    flexDirection = FlexDirection.Row,
                    marginTop = 7,
                },
            };
            _responsiveRows.Add(row);
            return row;
        }

        private static VisualElement CreateMetric(string caption, string value, string note)
        {
            var metric = new VisualElement
            {
                style =
                {
                    flexGrow = 1,
                    flexBasis = 0,
                    minWidth = 0,
                    marginLeft = 2,
                    marginRight = 2,
                    marginTop = 2,
                    marginBottom = 2,
                    paddingLeft = 8,
                    paddingRight = 8,
                    paddingTop = 6,
                    paddingBottom = 6,
                    backgroundColor = DetailBackground,
                    borderTopLeftRadius = 4,
                    borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4,
                    borderBottomRightRadius = 4,
                },
            };
            var captionLabel = Wrap(new Label(caption));
            captionLabel.style.fontSize = 11;
            captionLabel.style.color = MutedTextColor;
            metric.Add(captionLabel);

            var valueLabel = Wrap(new Label(value));
            valueLabel.style.fontSize = 15;
            valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            valueLabel.style.marginTop = 1;
            metric.Add(valueLabel);

            var noteLabel = Wrap(new Label(note));
            noteLabel.style.fontSize = 10;
            noteLabel.style.color = MutedTextColor;
            metric.Add(noteLabel);
            return metric;
        }

        private static VisualElement CreateCard(string name)
        {
            return new VisualElement
            {
                name = name,
                style =
                {
                    flexShrink = 0,
                    marginTop = 3,
                    marginBottom = 5,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 9,
                    paddingBottom = 9,
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

        private static VisualElement CreateCheckRow(bool passed, string title, string explanation)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    marginTop = 3,
                    marginBottom = 3,
                },
            };
            var icon = new Label(passed ? "✓" : "!");
            icon.style.width = 22;
            icon.style.flexShrink = 0;
            icon.style.fontSize = 15;
            icon.style.unityFontStyleAndWeight = FontStyle.Bold;
            icon.style.color = passed ? HealthyTextColor : WarningTextColor;
            row.Add(icon);

            var text = new VisualElement { style = { flexGrow = 1, minWidth = 0 } };
            var titleLabel = Wrap(new Label(title));
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            text.Add(titleLabel);
            var explanationLabel = Wrap(new Label(explanation));
            explanationLabel.style.color = MutedTextColor;
            explanationLabel.style.marginTop = 1;
            text.Add(explanationLabel);
            row.Add(text);
            return row;
        }

        private static Label CreateBullet(string text)
        {
            var label = Wrap(new Label("• " + text));
            label.style.marginTop = 3;
            label.style.marginBottom = 3;
            return label;
        }

        private static Label CreateInfoLabel(string text)
        {
            var label = Wrap(new Label(text));
            label.style.color = MutedTextColor;
            label.style.marginTop = 4;
            label.style.marginBottom = 4;
            return label;
        }

        private static Label CreateDetailHeading(string text)
        {
            var label = Wrap(new Label(text));
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 6;
            label.style.marginBottom = 2;
            return label;
        }

        private static VisualElement CreateDetailLine(string name, string value)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    marginTop = 1,
                    marginBottom = 1,
                    paddingLeft = 8,
                },
            };
            var nameLabel = Wrap(new Label(name));
            nameLabel.style.flexGrow = 1;
            nameLabel.style.minWidth = 0;
            row.Add(nameLabel);
            var valueLabel = new Label(value);
            valueLabel.style.flexShrink = 0;
            valueLabel.style.marginLeft = 8;
            valueLabel.style.color = MutedTextColor;
            row.Add(valueLabel);
            return row;
        }

        private static void AddStringList(VisualElement parent, string title, IEnumerable<string> values)
        {
            string[] array = values.ToArray();
            if (array.Length == 0) return;
            parent.Add(CreateDetailHeading(title));
            foreach (string value in array)
            {
                var label = Wrap(new Label("• " + value));
                label.style.paddingLeft = 8;
                parent.Add(label);
            }
        }

        private static Label Wrap(Label label)
        {
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1;
            return label;
        }

        private void CopyReport()
        {
            EditorGUIUtility.systemCopyBuffer = _rawReport;
            if (_status != null)
            {
                _status.text = string.IsNullOrEmpty(_rawReport) ? "当前没有可复制的报告。" : "完整报告已复制。";
                _status.messageType = HelpBoxMessageType.Info;
            }
        }

        private static void OpenAsset(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) return;
            AssetDatabase.OpenAsset(asset);
            EditorGUIUtility.PingObject(asset);
        }

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => ApplyResponsiveLayout(evt.newRect.width);

        private void ApplyResponsiveLayout(float width)
        {
            bool compact = width < CompactWidth;
            if (_actions != null)
            {
                _actions.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
                ApplyChildSizing(_actions, compact);
            }
            if (_responsiveRows == null) return;
            foreach (var row in _responsiveRows)
            {
                row.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
                ApplyChildSizing(row, compact);
            }
        }

        private static void ApplyChildSizing(VisualElement parent, bool compact)
        {
            foreach (var child in parent.Children())
            {
                if (compact)
                {
                    child.style.flexBasis = StyleKeyword.Auto;
                    child.style.flexGrow = 0;
                }
                else
                {
                    child.style.flexBasis = 0;
                    child.style.flexGrow = 1;
                }
            }
        }

        private static Color WindowBackground => EditorGUIUtility.isProSkin
            ? new Color(0.115f, 0.115f, 0.115f, 1f)
            : new Color(0.82f, 0.82f, 0.82f, 1f);

        private static Color CardBackground => EditorGUIUtility.isProSkin
            ? new Color(0.16f, 0.16f, 0.16f, 1f)
            : new Color(0.94f, 0.94f, 0.94f, 1f);

        private static Color DetailBackground => EditorGUIUtility.isProSkin
            ? new Color(0.115f, 0.115f, 0.115f, 1f)
            : new Color(0.86f, 0.86f, 0.86f, 1f);

        private static Color BorderColor => EditorGUIUtility.isProSkin
            ? new Color(0.28f, 0.28f, 0.28f, 1f)
            : new Color(0.68f, 0.68f, 0.68f, 1f);

        private static Color MutedTextColor => EditorGUIUtility.isProSkin
            ? new Color(0.68f, 0.68f, 0.68f, 1f)
            : new Color(0.32f, 0.32f, 0.32f, 1f);

        private static Color HealthyColor => EditorGUIUtility.isProSkin
            ? new Color(0.18f, 0.56f, 0.32f, 1f)
            : new Color(0.10f, 0.46f, 0.22f, 1f);

        private static Color HealthyTextColor => EditorGUIUtility.isProSkin
            ? new Color(0.42f, 0.88f, 0.58f, 1f)
            : new Color(0.05f, 0.38f, 0.16f, 1f);

        private static Color WarningColor => EditorGUIUtility.isProSkin
            ? new Color(0.86f, 0.52f, 0.15f, 1f)
            : new Color(0.72f, 0.36f, 0.04f, 1f);

        private static Color WarningTextColor => EditorGUIUtility.isProSkin
            ? new Color(1f, 0.68f, 0.28f, 1f)
            : new Color(0.62f, 0.25f, 0.02f, 1f);
    }
}
