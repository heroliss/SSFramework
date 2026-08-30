using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 「框架配置中心」hub：把框架各模块的配置 profile 资产聚合到一页——
    /// 每类配置一张卡片，列出找到的资产、标注单份 / 多份语义并做健康检查，
    /// 再提供跳转到所属 Module 工作台的入口。
    /// </summary>
    /// <remarks>
    /// 各 Editor Module 通过 <see cref="FrameworkConfigRegistry"/> 登记配置类型、数量语义与工作台；
    /// 本窗口只消费 Registry 与 <see cref="FrameworkEditorProfileCatalog"/> 的稳定发现快照，不创建资产，
    /// 也不编译期引用可选 Module。生成、构建与配置校验仍由各 Module 的工作台拥有。
    /// </remarks>
    public sealed class FrameworkConfigOverviewWindow : EditorWindow
    {
        private const string ResponsiveRowClass = "config-overview-responsive-row";

        private ScrollView _content;
        private HelpBox _status;
        private Button _scanButton;
        private IVisualElementScheduledItem _scheduledRefresh;
        private bool _catalogReady;
        private bool _refreshScheduled;
        private string _catalogError;

        [MenuItem(FrameworkMenuPaths.Configuration, priority = 1)]
        public static void Open() => GetWindow<FrameworkConfigOverviewWindow>("SSFramework 配置中心").Show();

        private void OnEnable()
        {
            minSize = new Vector2(300f, 360f);
            FrameworkConfigRegistry.Changed += OnCatalogInvalidated;
            FrameworkEditorProfileCatalog.Invalidated += OnCatalogInvalidated;
        }

        private void OnDisable()
        {
            FrameworkConfigRegistry.Changed -= OnCatalogInvalidated;
            FrameworkEditorProfileCatalog.Invalidated -= OnCatalogInvalidated;
            EditorApplication.delayCall -= RefreshNow;
            _scheduledRefresh?.Pause();
            _scheduledRefresh = null;
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            _refreshScheduled = false;
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();
            FrameworkEditorVisuals.ApplyWindowSurface(root);

            root.Add(FrameworkEditorVisuals.CreateHero(
                "config-overview-hero",
                "CONFIGURATION · 只读目录",
                "框架配置中心",
                "集中查看各 Module 的 Profile、数量语义与位置；实际生成和构建仍在所属工作台完成。"));

            var actions = new VisualElement
            {
                name = "config-overview-actions",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexShrink = 0,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 6,
                    paddingBottom = 4,
                },
            };
            actions.AddToClassList(ResponsiveRowClass);
            actions.Add(FrameworkEditorVisuals.CreateMutedLabel(
                "路径可点击定位；配置缺失不会在浏览时被暗中创建。"));
            _scanButton = FrameworkEditorVisuals.CreateActionButton(
                "重新扫描",
                ScheduleRefresh,
                "重新读取当前已登记的 Profile 资产路径。",
                "config-overview-rescan",
                primary: true);
            _scanButton.style.flexGrow = 0;
            _scanButton.style.flexBasis = StyleKeyword.Auto;
            _scanButton.style.minWidth = 108;
            actions.Add(_scanButton);
            root.Add(actions);

            _status = new HelpBox(string.Empty, HelpBoxMessageType.Info)
            {
                name = "config-overview-status",
                style =
                {
                    marginLeft = 12,
                    marginRight = 12,
                    marginBottom = 4,
                    display = DisplayStyle.None,
                },
            };
            root.Add(_status);

            _content = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "config-overview-content",
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto,
                style = { flexGrow = 1 },
            };
            _content.contentContainer.style.paddingLeft = 10;
            _content.contentContainer.style.paddingRight = 10;
            _content.contentContainer.style.paddingTop = 3;
            _content.contentContainer.style.paddingBottom = 12;
            root.Add(_content);

            root.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            if (_catalogReady)
                BuildCatalog();
            else if (!string.IsNullOrWhiteSpace(_catalogError))
                BuildFailure(_catalogError);
            else
                BuildLoading();
            ApplyResponsiveLayout(position.width);
            if (!_catalogReady && string.IsNullOrWhiteSpace(_catalogError))
                ScheduleRefresh();
        }

        internal void RefreshForTests() => RefreshNow();
        internal void ApplyResponsiveLayoutForTests(float width) => ApplyResponsiveLayout(width);

        private void OnCatalogInvalidated()
        {
            _catalogReady = false;
            _catalogError = null;
            ScheduleRefresh();
        }

        private void ScheduleRefresh()
        {
            if (_refreshScheduled) return;
            _catalogReady = false;
            _catalogError = null;
            _refreshScheduled = true;
            _scanButton?.SetEnabled(false);
            ShowStatus("正在建立 Profile 发现快照…", HelpBoxMessageType.Info);
            BuildLoading();
            EditorApplication.delayCall -= RefreshNow;
            _scheduledRefresh?.Pause();
            _scheduledRefresh = _content != null
                ? rootVisualElement.schedule.Execute(RefreshNow)
                : null;
            if (_scheduledRefresh == null)
                EditorApplication.delayCall += RefreshNow;
        }

        private void RefreshNow()
        {
            EditorApplication.delayCall -= RefreshNow;
            _scheduledRefresh?.Pause();
            _scheduledRefresh = null;
            _refreshScheduled = false;
            if (this == null) return;
            try
            {
                FrameworkConfigDescriptor[] configurations = FrameworkConfigRegistry.Snapshot().ToArray();
                FrameworkEditorProfileCatalog.Refresh(configurations
                    .SelectMany(configuration => configuration.SecondaryProfileType == null
                        ? new[] { configuration.ProfileType }
                        : new[] { configuration.ProfileType, configuration.SecondaryProfileType }));
                _catalogReady = true;
                _catalogError = null;
                HideStatus();
                BuildCatalog();
            }
            catch (Exception exception)
            {
                _catalogReady = false;
                _catalogError = exception.ToString();
                ShowStatus(
                    "Profile 扫描失败；没有用不完整清单冒充当前工程状态。",
                    HelpBoxMessageType.Error);
                BuildFailure(_catalogError);
                FrameworkEditorFeedback.Report(
                    "配置 Profile 扫描失败",
                    FrameworkEditorFeedback.Level.Failure,
                    "影响：配置中心无法给出完整资产清单。\n" +
                    "下一步：检查损坏的 Profile 资产。\n" + exception);
            }
            finally
            {
                _scanButton?.SetEnabled(true);
            }

            ApplyResponsiveLayout(rootVisualElement.resolvedStyle.width > 0f
                ? rootVisualElement.resolvedStyle.width
                : position.width);
            Repaint();
        }

        private void BuildLoading()
        {
            if (_content == null) return;
            _content.Clear();
            VisualElement card = FrameworkEditorVisuals.CreateCard(
                "config-overview-loading",
                FrameworkEditorVisuals.Tone.Active);
            card.Add(FrameworkEditorVisuals.CreateCardTitle("正在读取配置目录"));
            card.Add(FrameworkEditorVisuals.CreateMutedLabel(
                "窗口已经完成绘制；发现任务会在下一次 Editor tick 读取各 Module 登记的 Profile 类型。"));
            _content.Add(card);
        }

        private void BuildFailure(string exception)
        {
            if (_content == null) return;
            _content.Clear();
            VisualElement card = FrameworkEditorVisuals.CreateCard(
                "config-overview-failure",
                FrameworkEditorVisuals.Tone.Error);
            card.Add(FrameworkEditorVisuals.CreateCardTitle("无法建立完整配置目录"));
            card.Add(FrameworkEditorVisuals.CreateMutedLabel(
                "检查 Console 与损坏的 Profile 资产后重试；扫描失败不会显示部分结果。"));
            var details = new Foldout
            {
                name = "config-overview-failure-details",
                text = "异常详情",
                value = false,
            };
            details.Add(FrameworkEditorVisuals.Wrap(new Label(exception ?? "未知错误")));
            card.Add(details);
            _content.Add(card);
        }

        private void BuildCatalog()
        {
            if (_content == null) return;
            _content.Clear();
            FrameworkConfigDescriptor[] configurations = FrameworkConfigRegistry.Snapshot().ToArray();
            int primaryCount = configurations.Sum(section =>
                FrameworkEditorProfileCatalog.GetPaths(section.ProfileType).Count);
            int secondaryCount = configurations
                .Where(section => section.SecondaryProfileType != null)
                .Sum(section => FrameworkEditorProfileCatalog.GetPaths(section.SecondaryProfileType).Count);
            int singletonConflicts = configurations.Count(section => section.Singleton &&
                FrameworkEditorProfileCatalog.GetPaths(section.ProfileType).Count > 1);

            var metrics = new VisualElement
            {
                name = "config-overview-metrics",
                style = { flexDirection = FlexDirection.Row, marginBottom = 4 },
            };
            metrics.AddToClassList(ResponsiveRowClass);
            metrics.Add(FrameworkEditorVisuals.CreateMetric(
                "config-overview-metric-modules",
                "已登记 Module",
                configurations.Length.ToString(),
                "由 owner Module 自注册"));
            metrics.Add(FrameworkEditorVisuals.CreateMetric(
                "config-overview-metric-assets",
                "发现资产",
                (primaryCount + secondaryCount).ToString(),
                secondaryCount > 0 ? $"含 {secondaryCount} 份附属配置" : "Profile 路径快照"));
            metrics.Add(FrameworkEditorVisuals.CreateMetric(
                "config-overview-metric-health",
                "单例状态",
                singletonConflicts == 0 ? "正常" : $"{singletonConflicts} 项冲突",
                singletonConflicts == 0 ? "未发现重复单例" : "仅第一份会生效"));
            _content.Add(metrics);

            if (configurations.Length == 0)
            {
                VisualElement empty = FrameworkEditorVisuals.CreateCard(
                    "config-overview-empty",
                    FrameworkEditorVisuals.Tone.Warning);
                empty.Add(FrameworkEditorVisuals.CreateCardTitle("没有已登记的配置 Module"));
                empty.Add(FrameworkEditorVisuals.CreateMutedLabel(
                    "若工程本应包含配置工具，请先检查 Console 编译错误与可选 Module 是否安装。"));
                _content.Add(empty);
                return;
            }

            _content.Add(FrameworkEditorVisuals.CreateSectionTitle("配置目录"));
            foreach (FrameworkConfigDescriptor section in configurations)
                _content.Add(CreateSection(section));
        }

        private VisualElement CreateSection(FrameworkConfigDescriptor section)
        {
            IReadOnlyList<string> paths = FrameworkEditorProfileCatalog.GetPaths(section.ProfileType);
            bool singletonConflict = section.Singleton && paths.Count > 1;
            VisualElement card = FrameworkEditorVisuals.CreateCard(
                "config-overview-section-" + section.Id,
                singletonConflict ? FrameworkEditorVisuals.Tone.Warning : FrameworkEditorVisuals.Tone.Neutral);

            var header = new VisualElement
            {
                name = "config-overview-header-" + section.Id,
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center },
            };
            header.AddToClassList(ResponsiveRowClass);
            var title = FrameworkEditorVisuals.CreateCardTitle($"{section.Title} · {paths.Count} 份");
            title.style.flexGrow = 1;
            title.style.fontSize = 14;
            header.Add(title);
            Button jump = FrameworkEditorVisuals.CreateActionButton(
                section.MenuLabel,
                () => OpenWorkbench(section),
                $"打开“{section.Title}”所属工作台。",
                "config-overview-open-" + section.Id);
            jump.style.flexGrow = 0;
            jump.style.flexBasis = StyleKeyword.Auto;
            jump.style.minWidth = 104;
            header.Add(jump);
            card.Add(header);

            card.Add(FrameworkEditorVisuals.CreateMutedLabel(section.Note));
            if (singletonConflict)
                card.Add(new HelpBox(
                    "找到多份单例 Profile，仅第一份生效；请定位后删到只剩一份。",
                    HelpBoxMessageType.Warning));

            AddAssetGroup(card, section.Singleton ? "主 Profile（单例）" : "Profile 资产", paths);
            if (section.SecondaryProfileType != null)
                AddAssetGroup(
                    card,
                    section.SecondaryLabel,
                    FrameworkEditorProfileCatalog.GetPaths(section.SecondaryProfileType));
            return card;
        }

        private void AddAssetGroup(VisualElement parent, string label, IReadOnlyList<string> paths)
        {
            var title = FrameworkEditorVisuals.CreateMutedLabel($"{label} · {paths.Count}");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginTop = 7;
            parent.Add(title);
            if (paths.Count == 0)
            {
                var missing = FrameworkEditorVisuals.CreateMutedLabel("尚未创建；请从所属工作台显式创建。 ");
                missing.style.backgroundColor = FrameworkEditorVisuals.DetailBackground;
                missing.style.paddingLeft = 8;
                missing.style.paddingRight = 8;
                missing.style.paddingTop = 6;
                missing.style.paddingBottom = 6;
                parent.Add(missing);
                return;
            }

            foreach (string path in paths)
            {
                string capturedPath = path;
                var button = new Button(() => LocateAsset(capturedPath))
                {
                    text = capturedPath,
                    tooltip = capturedPath + "\n点击定位并选中",
                    name = "config-overview-asset-" + capturedPath,
                    style =
                    {
                        minHeight = 24,
                        marginTop = 2,
                        marginBottom = 2,
                        paddingLeft = 8,
                        paddingRight = 8,
                        unityTextAlign = TextAnchor.MiddleLeft,
                        whiteSpace = WhiteSpace.Normal,
                        backgroundColor = FrameworkEditorVisuals.DetailBackground,
                    },
                };
                parent.Add(button);
            }
        }

        private void OpenWorkbench(FrameworkConfigDescriptor section)
        {
            if (!EditorApplication.ExecuteMenuItem(section.MenuPath))
                FrameworkEditorFeedback.Warn(
                    "配置工作台入口失效",
                    $"影响：没有打开“{section.Title}”工作台。\n原因：找不到菜单 {section.MenuPath}。\n" +
                    "下一步：确认所属可选 Module 已安装，并检查 Console 编译错误。");
        }

        private void LocateAsset(string path)
        {
            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
            {
                FrameworkEditorFeedback.Warn(
                    "配置资产已经变化",
                    $"影响：无法定位缓存路径 {path}。\n下一步：点击“重新扫描”更新配置目录。");
                return;
            }

            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;
        }

        private void ShowStatus(string message, HelpBoxMessageType messageType)
        {
            if (_status == null) return;
            _status.text = message;
            _status.messageType = messageType;
            _status.style.display = DisplayStyle.Flex;
        }

        private void HideStatus()
        {
            if (_status != null) _status.style.display = DisplayStyle.None;
        }

        private void OnRootGeometryChanged(GeometryChangedEvent evt) =>
            ApplyResponsiveLayout(evt.newRect.width);

        private void ApplyResponsiveLayout(float width)
        {
            bool compact = width < FrameworkEditorVisuals.CompactWidth;
            foreach (VisualElement row in rootVisualElement.Query<VisualElement>(className: ResponsiveRowClass)
                         .ToList())
            {
                row.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
                row.style.alignItems = compact ? Align.Stretch : Align.Center;
                FrameworkEditorVisuals.ApplyResponsiveChildren(row, compact);
            }

            if (_scanButton != null)
            {
                _scanButton.style.flexGrow = compact ? 1 : 0;
                _scanButton.style.minWidth = compact ? 0 : 108;
            }
        }
    }
}
