using System;
using System.Collections.Generic;
using System.IO;
using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 「UI 生成配置总览」窗口：集中显示全工程 <see cref="UICodeGenProfile"/>、目录级
    /// <see cref="UICodeGenDirConfig"/> 与绑定 Prefab 索引。窗口只消费快照；完整扫描由明确按钮触发。
    /// </summary>
    public sealed class UICodeGenConfigOverviewWindow : EditorWindow
    {
        private static readonly Type[] CatalogTypes =
        {
            typeof(UICodeGenProfile),
            typeof(UICodeGenDirConfig),
        };

        private readonly List<VisualElement> _responsiveRows = new();
        private VisualElement _actions;
        private ScrollView _content;
        private string _scanError;
        private float _lastWidth = 820f;

        [MenuItem(FrameworkMenuPaths.UIBinding, priority = 43)]
        public static void Open() => GetWindow<UICodeGenConfigOverviewWindow>("UI 生成配置总览").Show();

        [InitializeOnLoadMethod]
        private static void RegisterEditorEntries()
        {
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "ui-binding", FrameworkToolCategory.CodeGeneration, 40,
                "UI 绑定", "查看全工程默认、目录级覆盖链和生成落点；Prefab 生成仍保留在有选择上下文的右键菜单。",
                FrameworkMenuPaths.UIBinding));
            FrameworkConfigRegistry.Register(new FrameworkConfigDescriptor(
                "ui-binding", 20, "UI 绑定（代码生成）", typeof(UICodeGenProfile), singleton: true,
                "全工程 Profile 提供默认约定；业务命名空间与输出目录需显式填写，目录差异用就近配置逐项覆盖。",
                FrameworkMenuPaths.UIBinding,
                secondaryProfileType: typeof(UICodeGenDirConfig), secondaryLabel: "目录级覆盖"));
            FrameworkGeneratedOutputClaimCatalog.Register(new FrameworkGeneratedOutputClaimSource(
                UIBindingCodeGenerator.OutputClaimSourceId,
                "UI 绑定代码生成",
                UIBindingCodeGenerator.CollectRegisteredOutputClaims));
        }

        private void OnEnable()
        {
            minSize = new Vector2(280, 360);
            FrameworkEditorProfileCatalog.Invalidated += OnCatalogInvalidated;
        }

        private void OnDisable() => FrameworkEditorProfileCatalog.Invalidated -= OnCatalogInvalidated;

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();
            FrameworkEditorVisuals.ApplyWindowSurface(root);
            root.Add(FrameworkEditorVisuals.CreateHero(
                "ui-binding-hero",
                "CODE GENERATION · UGUI",
                "UI 节点绑定",
                "先看清 Profile、目录覆盖与生成落点，再从 Prefab 上下文生成两份 partial 代码。"));

            _actions = new VisualElement
            {
                name = "ui-binding-actions",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexShrink = 0,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 7,
                    paddingBottom = 5,
                },
            };
            _actions.Add(FrameworkEditorVisuals.CreateMutedLabel(
                "窗口重绘只读快照；“重新扫描”才完整读取配置与绑定 Prefab。"));
            _actions.Add(FrameworkEditorVisuals.CreateActionButton(
                "重新扫描",
                RefreshCatalogs,
                "重新读取 Profile，并完整重建含 UIBindingData 的 Prefab 会话索引。",
                "ui-binding-rescan",
                primary: true));
            root.Add(_actions);

            _content = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "ui-binding-content",
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                style =
                {
                    flexGrow = 1,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingBottom = 10,
                },
            };
            root.Add(_content);
            root.RegisterCallback<GeometryChangedEvent>(evt => ApplyResponsiveLayout(evt.newRect.width));

            RenderContent();
            ApplyResponsiveLayout(position.width > 0f ? position.width : _lastWidth);
        }

        private void OnCatalogInvalidated()
        {
            if (_content != null) RenderContent();
        }

        private void RenderContent()
        {
            if (_content == null) return;
            _content.Clear();
            _responsiveRows.Clear();
            _responsiveRows.Add(_actions);

            bool profileReady = FrameworkEditorProfileCatalog.TryGetPaths(
                typeof(UICodeGenProfile), out IReadOnlyList<string> profilePaths);
            bool directoryReady = FrameworkEditorProfileCatalog.TryGetPaths(
                typeof(UICodeGenDirConfig), out IReadOnlyList<string> directoryPaths);
            bool prefabIndexReady = UIBindingPrefabCatalog.TryGetPaths(
                out IReadOnlyList<string> bindingPrefabPaths);

            if (!string.IsNullOrEmpty(_scanError)) AddScanError();
            if (!profileReady || !directoryReady || !prefabIndexReady)
            {
                AddIdleState();
                ApplyResponsiveLayout(_lastWidth);
                return;
            }

            AddSnapshotSummary(profilePaths.Count, directoryPaths.Count, bindingPrefabPaths.Count);
            AddResolutionOrder();
            AddProfileSection(profilePaths);
            AddDirectorySections(directoryPaths);
            ApplyResponsiveLayout(_lastWidth);
        }

        private void AddIdleState()
        {
            VisualElement card = FrameworkEditorVisuals.CreateCard(
                "ui-binding-idle", FrameworkEditorVisuals.Tone.Active);
            card.Add(FrameworkEditorVisuals.CreateCardTitle("先建立可审查的配置与 Prefab 索引"));
            card.Add(FrameworkEditorVisuals.CreateMutedLabel(
                "首次绘制不会隐藏扫描全工程。建立后，Profile 路径按工程 revision 复用；绑定 Prefab 路径由导入回调增量维护，并跨脚本域重载恢复。"));

            VisualElement steps = CreateResponsiveRow("ui-binding-idle-steps");
            steps.Add(FrameworkEditorVisuals.CreateMetric(null, "Profile", "待读取", "全工程默认"));
            steps.Add(FrameworkEditorVisuals.CreateMetric(null, "目录覆盖", "待读取", "就近优先"));
            steps.Add(FrameworkEditorVisuals.CreateMetric(null, "Prefab 索引", "待建立", "只记录候选路径"));
            card.Add(steps);

            VisualElement action = CreateResponsiveRow("ui-binding-idle-action");
            action.Add(FrameworkEditorVisuals.CreateActionButton(
                "读取配置与 Prefab 索引",
                RefreshCatalogs,
                "执行一次明确的完整扫描；完成后普通窗口重绘不再扫描。",
                "ui-binding-initial-scan",
                primary: true));
            card.Add(action);
            _content.Add(card);
        }

        private void AddScanError()
        {
            VisualElement card = FrameworkEditorVisuals.CreateCard(
                "ui-binding-scan-error", FrameworkEditorVisuals.Tone.Error);
            card.Add(FrameworkEditorVisuals.CreateCardTitle("上次扫描失败"));
            card.Add(FrameworkEditorVisuals.CreateMutedLabel(_scanError));
            _content.Add(card);
        }

        private void AddSnapshotSummary(int profileCount, int directoryCount, int prefabCount)
        {
            _content.Add(FrameworkEditorVisuals.CreateSectionTitle("当前快照"));
            VisualElement summary = CreateResponsiveRow("ui-binding-summary");
            summary.Add(FrameworkEditorVisuals.CreateMetric(
                "ui-binding-profile-count", "全工程 Profile", profileCount.ToString(),
                profileCount == 1 ? "唯一配置" : profileCount == 0 ? "尚未创建" : "需要收敛为一份"));
            summary.Add(FrameworkEditorVisuals.CreateMetric(
                "ui-binding-directory-count", "目录覆盖", directoryCount.ToString(), "按 Prefab 所在目录解析"));
            summary.Add(FrameworkEditorVisuals.CreateMetric(
                "ui-binding-prefab-count", "绑定 Prefab", prefabCount.ToString(), "claim 只加载这些候选"));
            _content.Add(summary);
        }

        private void AddResolutionOrder()
        {
            VisualElement card = FrameworkEditorVisuals.CreateCard(
                "ui-binding-resolution-order", FrameworkEditorVisuals.Tone.Active);
            card.Add(FrameworkEditorVisuals.CreateCardTitle("覆盖链 · 每个字段独立解析"));
            card.Add(FrameworkEditorVisuals.CreateBullet(
                "Prefab 覆盖 → 最近目录配置 → 父目录配置 → 全工程 Profile"));
            card.Add(FrameworkEditorVisuals.CreateMutedLabel(
                "目录项留空表示继续向上继承；窗口只说明生成落点，实际生成仍从 Project / Prefab Stage 的上下文菜单发起。"));
            _content.Add(card);
        }

        private void AddProfileSection(IReadOnlyList<string> profilePaths)
        {
            _content.Add(FrameworkEditorVisuals.CreateSectionTitle("全工程默认"));
            if (profilePaths.Count > 1)
            {
                VisualElement duplicate = FrameworkEditorVisuals.CreateCard(
                    "ui-binding-profile-duplicate", FrameworkEditorVisuals.Tone.Warning);
                duplicate.Add(FrameworkEditorVisuals.CreateCardTitle(
                    $"找到 {profilePaths.Count} 份 UICodeGenProfile"));
                duplicate.Add(FrameworkEditorVisuals.CreateMutedLabel(
                    "运行时按路径排序只采用第一份；请删到只剩一个，避免“改了但不生效”。"));
                _content.Add(duplicate);
            }

            UICodeGenProfile profile = profilePaths.Count > 0
                ? AssetDatabase.LoadAssetAtPath<UICodeGenProfile>(profilePaths[0])
                : null;
            if (profile == null)
            {
                VisualElement missing = FrameworkEditorVisuals.CreateCard(
                    "ui-binding-profile-missing", FrameworkEditorVisuals.Tone.Warning);
                missing.Add(FrameworkEditorVisuals.CreateCardTitle("尚未创建 UICodeGenProfile"));
                missing.Add(FrameworkEditorVisuals.CreateMutedLabel(
                    "仅打开窗口不会写项目；下面的动作会在项目配置目录创建一份待填写的空 Profile。"));
                VisualElement action = CreateResponsiveRow("ui-binding-create-profile-row");
                action.Add(FrameworkEditorVisuals.CreateActionButton(
                    "创建 UI 生成配置",
                    CreateProfile,
                    "创建空 Profile、定位资产；命名空间与两个输出目录仍需由项目填写。",
                    "ui-binding-create-profile",
                    primary: true));
                missing.Add(action);
                _content.Add(missing);
                return;
            }

            _content.Add(CreateConfigCard(
                "ui-binding-profile-default",
                profile,
                profilePaths[0],
                "全工程默认 · 所有未被覆盖的 Prefab",
                profile.NamespaceRoot,
                profile.OutputCodeDir,
                profile.GeneratedCodeDir,
                profile.FileNameTemplate,
                isProfile: true));
        }

        private void AddDirectorySections(IReadOnlyList<string> directoryPaths)
        {
            _content.Add(FrameworkEditorVisuals.CreateSectionTitle($"目录级覆盖 · {directoryPaths.Count}"));
            if (directoryPaths.Count == 0)
            {
                VisualElement empty = FrameworkEditorVisuals.CreateCard("ui-binding-directory-empty");
                empty.Add(FrameworkEditorVisuals.CreateCardTitle("没有目录级覆盖"));
                empty.Add(FrameworkEditorVisuals.CreateMutedLabel(
                    "所有 Prefab 都沿覆盖链回落到全工程默认；这不是错误。"));
                _content.Add(empty);
                return;
            }

            int visibleIndex = 0;
            foreach (string path in directoryPaths)
            {
                UICodeGenDirConfig config = AssetDatabase.LoadAssetAtPath<UICodeGenDirConfig>(path);
                if (config == null) continue;
                string directory = (Path.GetDirectoryName(path) ?? string.Empty).Replace('\\', '/');
                _content.Add(CreateConfigCard(
                    "ui-binding-directory-" + visibleIndex++,
                    config,
                    path,
                    "管辖 " + directory + "/**",
                    config.NamespaceOrNull,
                    config.OutputDirOrNull,
                    config.GeneratedDirOrNull,
                    config.FileNameOrNull,
                    isProfile: false));
            }
        }

        private VisualElement CreateConfigCard(
            string name,
            UnityEngine.Object asset,
            string path,
            string scope,
            string namespaceValue,
            string outputDir,
            string generatedDir,
            string fileName,
            bool isProfile)
        {
            bool missingRequired = isProfile &&
                                   (string.IsNullOrWhiteSpace(namespaceValue) ||
                                    string.IsNullOrWhiteSpace(outputDir) ||
                                    string.IsNullOrWhiteSpace(generatedDir));
            VisualElement card = FrameworkEditorVisuals.CreateCard(
                name,
                missingRequired ? FrameworkEditorVisuals.Tone.Warning : FrameworkEditorVisuals.Tone.Neutral);

            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginBottom = 3,
                },
            };
            Label title = FrameworkEditorVisuals.CreateCardTitle(Path.GetFileName(path));
            title.tooltip = path;
            title.style.flexGrow = 1;
            header.Add(title);
            Button locate = FrameworkEditorVisuals.CreateActionButton(
                "定位资产",
                () => Locate(asset),
                path,
                primary: false);
            locate.style.flexGrow = 0;
            locate.style.flexBasis = StyleKeyword.Auto;
            locate.style.minWidth = 72;
            header.Add(locate);
            card.Add(header);
            card.Add(FrameworkEditorVisuals.CreateMutedLabel(scope));

            VisualElement identityRow = CreateResponsiveRow(name + "-identity");
            identityRow.Add(CreateFieldMetric("命名空间", namespaceValue, isProfile, "生成代码 namespace"));
            identityRow.Add(CreateFieldMetric("文件名 / 类名", fileName, isProfile, "两份 partial 共用"));
            card.Add(identityRow);

            VisualElement outputRow = CreateResponsiveRow(name + "-outputs");
            outputRow.Add(CreateFieldMetric("手写逻辑目录", outputDir, isProfile, "<Name>.cs · 仅缺失时创建"));
            outputRow.Add(CreateFieldMetric("节点绑定目录", generatedDir, isProfile, "<Name>.nodes.g.cs · 每次覆盖"));
            card.Add(outputRow);
            return card;
        }

        private static VisualElement CreateFieldMetric(
            string caption,
            string value,
            bool isProfile,
            string configuredNote)
        {
            bool missing = string.IsNullOrWhiteSpace(value);
            string display = missing ? (isProfile ? "未配置" : "继承") : value;
            string note = missing
                ? (isProfile ? "需要在全工程 Profile 显式填写" : "继续沿目录链向上解析")
                : configuredNote;
            return FrameworkEditorVisuals.CreateMetric(null, caption, display, note);
        }

        private VisualElement CreateResponsiveRow(string name)
        {
            var row = new VisualElement
            {
                name = name,
                style =
                {
                    flexDirection = FlexDirection.Row,
                    marginTop = 2,
                    marginBottom = 2,
                },
            };
            _responsiveRows.Add(row);
            return row;
        }

        private void CreateProfile()
        {
            if (!FrameworkEditorOperationGate.EnsureCanStart("创建 UI 生成配置")) return;
            UICodeGenProfile profile = UICodeGenProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
            _scanError = string.Empty;
            RenderContent();
        }

        private static void Locate(UnityEngine.Object asset)
        {
            if (asset == null) return;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private void RefreshCatalogs()
        {
            try
            {
                FrameworkEditorProfileCatalog.Refresh(CatalogTypes);
                UIBindingPrefabCatalog.Refresh();
                _scanError = string.Empty;
            }
            catch (Exception exception)
            {
                // 两个 Catalog 各自原子替换，但不是同一事务；任一步失败都丢弃两边快照，
                // 避免窗口把新 Profile 与旧 Prefab 索引拼成一份貌似完整的混合证据。
                UIBindingPrefabCatalog.Invalidate();
                _scanError = exception.GetType().Name + ": " + exception.Message +
                             "（本次两类快照均已丢弃，请重试。）";
                FrameworkEditorProfileCatalog.Invalidate();
                Debug.LogException(exception);
            }
            finally
            {
                FrameworkGeneratedOutputClaimCatalog.Invalidate();
            }

            RenderContent();
        }

        private void ApplyResponsiveLayout(float width)
        {
            if (float.IsNaN(width) || float.IsInfinity(width) || width <= 0f) return;
            _lastWidth = width;
            bool compact = width < FrameworkEditorVisuals.CompactWidth;
            foreach (VisualElement row in _responsiveRows)
            {
                if (row == null) continue;
                row.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
                FrameworkEditorVisuals.ApplyResponsiveChildren(row, compact);
            }
        }

        internal void RefreshForTests() => RefreshCatalogs();
        internal void ApplyResponsiveLayoutForTests(float width) => ApplyResponsiveLayout(width);
    }
}
