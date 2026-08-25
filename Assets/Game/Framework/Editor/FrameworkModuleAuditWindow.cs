using System;
using System.Collections.Generic;
using System.IO;
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
            _actions.Add(CreateActionButton("打开模块地图", () => OpenFile("docs/framework-module-map.md"),
                "查看各程序集的职责、依赖方向与删除标准。"));
            _actions.Add(CreateActionButton("真实构建对比", FrameworkBuildSizeProbeWindow.Open,
                "在隔离空工程里真正删除未选 Module，并读取当前平台 Player BuildReport。"));
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
                _status.text = result.RequiresAttention
                    ? "检测完成：依赖方向与最终保留原因已分开显示；请先看顶部结论和 Module 状态。"
                    : "检测完成：当前模块边界健康，且没有发现无条件 Module 保留规则。大小数字不代表最终包体。";
                _status.messageType = result.RequiresAttention
                    ? HelpBoxMessageType.Warning
                    : HelpBoxMessageType.Info;
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
            AddHotUpdateDeployment(result.HotUpdateDeployment);
            AddSectionTitle("值得关注");
            var recommendations = CreateCard("module-audit-recommendations");
            foreach (string recommendation in result.Recommendations)
                recommendations.Add(CreateBullet(recommendation));
            _content.Add(recommendations);

            AddSectionTitle("当前 Module · 为什么可能被带入");
            var model = CreateCard("module-audit-retention-model");
            model.Add(CreateInfoLabel(
                "不要把五件事混成“自动裁剪”：① 源码/Package 已安装；② asmdef 参与 Player 编译；③ 业务或 Module 真实引用；④ link.xml / 场景 / 反射成为 UnityLinker 根；⑤ HybridCLR 按 Profile 同步、Generate 后部署完整 DLL。最终 Player 还要经过链接、IL2CPP 与压缩；下面只显示当前可证明的输入。"));
            _content.Add(model);

            FrameworkModuleAudit.ModuleStatus[] attentionStatuses = result.ModuleStatuses
                .Where(status => status.HasUnconditionalPreservation || status.HasHotUpdateViolation)
                .ToArray();
            if (attentionStatuses.Length > 0)
            {
                var attention = new Foldout
                {
                    name = "module-audit-attention-statuses",
                    text = $"优先理解的 Module（{attentionStatuses.Length} 个）",
                    value = true,
                    style = { marginTop = 4, marginBottom = 4 },
                };
                foreach (var status in attentionStatuses)
                    attention.Add(CreateModuleStatusCard(status));
                _content.Add(attention);
            }

            var moduleStatuses = new Foldout
            {
                name = "module-audit-module-statuses",
                text = $"查看全部 {result.ModuleStatuses.Length} 个 Runtime Module",
                value = false,
                style = { marginTop = 4, marginBottom = 4 },
            };
            foreach (var status in result.ModuleStatuses)
                moduleStatuses.Add(CreateModuleStatusCard(status));
            _content.Add(moduleStatuses);

            if (result.GlobalPreservations.Length > 0)
                _content.Add(CreateGlobalPreservationsFoldout(result.GlobalPreservations));

            AddSectionTitle("常用组合");
            foreach (var profile in result.CommonProfiles)
                _content.Add(CreateProfileCard(profile));

            var advanced = new Foldout
            {
                name = "module-audit-advanced-profiles",
                text = "完整模块、任意入口与热更 Profile（进阶）",
                value = false,
                style = { marginTop = 8, marginBottom = 4 },
            };
            advanced.Add(CreateProfileCard(result.FullProfile));
            if (result.HotUpdateProfile != null)
                advanced.Add(CreateProfileCard(result.HotUpdateProfile));
            else
                advanced.Add(CreateInfoLabel(result.HotUpdateNote));
            var arbitraryModules = new Foldout
            {
                name = "module-audit-module-profiles",
                text = "任意 Module 作为入口（自动计算依赖闭包）",
                value = false,
                style = { marginTop = 5 },
            };
            arbitraryModules.Add(CreateInfoLabel(
                "这不是全局启用开关，而是 what-if：假设业务只从某个 Module 进入，会自动带上哪些 Framework 与外部依赖。"));
            foreach (var profile in result.ModuleProfiles)
                arbitraryModules.Add(CreateProfileCard(profile));
            advanced.Add(arbitraryModules);
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

            bool clear = !result.RequiresAttention;
            card.style.borderLeftColor = clear ? HealthyColor : WarningColor;
            string titleText = clear
                ? "✓ 当前模块边界与保留规则健康"
                : result.IsHealthy
                    ? "△ 依赖方向健康，但保留 / 派生证据需关注"
                    : "⚠ 当前模块边界需要关注";
            var title = Wrap(new Label(titleText));
            title.style.fontSize = 17;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = clear ? HealthyTextColor : WarningTextColor;
            card.Add(title);

            var explanation = Wrap(new Label(clear
                ? "Framework Module 由消费方显式选择；没有发现隐式引用、反向拖入或无条件 Module link.xml 根。"
                : result.IsHealthy
                    ? "asmdef 删除测试通过，但 link.xml 或热更派生状态仍可能让“Profile 已配置”不等于“当前产物已同步”。"
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
            metrics.Add(CreateMetric("无条件保留", result.UnconditionalModulePreservations.Length.ToString(),
                result.HasRetentionWarnings ? "需要理解为何存在" : "未发现 Module 级根"));
            card.Add(metrics);
            _content.Add(card);
        }

        private void AddHotUpdateDeployment(FrameworkModuleAudit.HotUpdateDeploymentEvidence evidence)
        {
            if (evidence == null || !evidence.BuildModuleAvailable) return;

            AddSectionTitle("热更产物链 · Profile 到当前中转清单");
            var card = CreateCard("module-audit-hot-update-evidence");
            card.style.borderLeftWidth = 4;
            bool clear = evidence.ProfileAvailable && !evidence.RequiresAttention;
            card.style.borderLeftColor = clear ? HealthyColor : WarningColor;

            var title = Wrap(new Label(!evidence.ProfileAvailable
                ? "未找到热更 Profile"
                : clear
                    ? "✓ 当前本地派生状态无冲突"
                    : "△ 本地热更派生状态需要处理"));
            title.style.fontSize = 15;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = clear ? HealthyTextColor : WarningTextColor;
            card.Add(title);
            card.Add(CreateInfoLabel(evidence.Note));

            if (evidence.ProfileAvailable && evidence.InspectionAvailable)
            {
                var metrics = CreateResponsiveRow("module-audit-hot-update-metrics");
                metrics.Add(CreateMetric("Profile", evidence.ProfileAssemblies.Length.ToString(),
                    "期望热更程序集"));
                metrics.Add(CreateMetric("HybridCLRSettings",
                    evidence.SettingsAvailable && evidence.SettingsMatch ? "一致" : "漂移",
                    evidence.SettingsAvailable ? "同步输入" : "无法读取"));
                metrics.Add(CreateMetric("Generate",
                    !evidence.GenerationRequired ? "不需要" : evidence.GenerationFresh ? "新鲜" : "过期",
                    "AOT / 桥接生成环境"));
                string stagingValue = !evidence.StagingRequired && !evidence.StagedManifestExists
                    ? "可选"
                    : evidence.StagedManifestAvailable && evidence.StagedManifestMatches
                        ? "一致"
                        : "漂移";
                metrics.Add(CreateMetric("DLL 中转",
                    stagingValue,
                    !evidence.StagingRequired && !evidence.StagedManifestExists
                        ? "纯 AOT 可不建代码包"
                        : string.IsNullOrWhiteSpace(evidence.StagedVersion)
                        ? "尚无可读版本"
                        : "版本 " + evidence.StagedVersion));
                card.Add(metrics);

                var details = new Foldout
                {
                    name = "module-audit-hot-update-details",
                    text = "查看每一层的证据与恢复入口",
                    value = evidence.RequiresAttention,
                    style = { marginTop = 5 },
                };
                details.Add(CreateBullet(evidence.SettingsMessage));
                details.Add(CreateBullet(evidence.GenerationMessage));
                details.Add(CreateBullet(evidence.StagedMessage));
                card.Add(details);
            }

            card.Add(CreateInfoLabel(
                "边界：中转一致只证明清单结构与当前派生输入相符、所列文件存在；" +
                "不证明 DLL 内容相对源码新鲜，也不代表 YooAsset bundle 或 CDN 已部署。"));

            var actions = CreateResponsiveRow("module-audit-hot-update-actions");
            if (!evidence.ProfileAvailable)
                actions.Add(CreateActionButton("打开 / 创建热更配置", OpenHotUpdateProfile,
                    "创建默认 Profile；若目标是纯 AOT，请保留空列表作为明确单一真源。"));
            else if (!string.IsNullOrWhiteSpace(evidence.ProfilePath))
                actions.Add(CreateActionButton("定位 Profile", () => LocatePath(evidence.ProfilePath),
                    "在 Unity Project 中选中热更期望配置；这里只定位，不自动同步。"));
            if (evidence.StagedManifestExists)
                actions.Add(CreateActionButton("定位中转清单",
                    () => LocatePath("Assets/HotUpdateDlls/hotupdate_manifest.bytes"),
                    "在 Unity Project 中选中最近一次代码包构建写入的本地清单；即使损坏也保留此排查入口。"));
            actions.Add(CreateActionButton("复制派生证据", () => CopyHotUpdateEvidence(evidence),
                "复制 Profile、Settings、Generate 与 DLL 中转状态，便于 issue / AI 排查。"));
            card.Add(actions);
            _content.Add(card);
        }

        private VisualElement CreateModuleStatusCard(FrameworkModuleAudit.ModuleStatus status)
        {
            var card = CreateCard("module-audit-status-" + status.Module.Name.Replace('.', '-').ToLowerInvariant());
            var title = Wrap(new Label(status.Module.Name));
            title.style.fontSize = 14;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(title);

            var metrics = CreateResponsiveRow("module-audit-status-metrics-" + status.Module.Name);
            metrics.Add(CreateMetric("Player 实际消费", status.DirectConsumers.Length.ToString(),
                status.DirectConsumers.Length == 0 ? "未发现元数据引用" : "参与运行时保留判断"));
            metrics.Add(CreateMetric("删除阻塞", status.RemovalBlockers.Length.ToString(),
                status.RemovalBlockers.Length == 0 ? "没有 asmdef 声明引用" : "来自完整 asmdef 图"));
            metrics.Add(CreateMetric("Profile 热更", status.IsHotUpdateRoot ? "已列入" : "未列入",
                status.HasHotUpdateViolation
                    ? "⚠ 当前 AOT → 热更非法"
                    : status.IsHotUpdateRoot
                    ? status.HotUpdateDependencies.Length > 0
                        ? "受热更依赖传播约束"
                        : "完整 DLL 进入 CodePackage"
                    : "不由热更清单保留"));
            int unconditional = status.TargetingPreservations
                .Concat(status.OwnedPreservations)
                .Where(rule => rule.IsUnconditional)
                .GroupBy(rule => rule.Path + "\0" + rule.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .Count();
            metrics.Add(CreateMetric("link.xml 根", unconditional.ToString(),
                unconditional > 0 ? "可能阻止自动裁剪" : "未发现无条件规则"));
            card.Add(metrics);

            var details = new Foldout
            {
                text = "为什么可能进入构建 · 移除前做什么",
                value = status.HasHotUpdateViolation,
                style = { marginTop = 5 },
            };
            details.Add(CreateDetailHeading("当前可证明的保留输入"));
            foreach (string reason in status.RetentionReasons)
                details.Add(CreateBullet(reason));
            details.Add(CreateDetailHeading("安全移除顺序"));
            foreach (string step in status.RemovalSteps)
                details.Add(CreateBullet(step));
            card.Add(details);

            var actions = CreateResponsiveRow("module-audit-status-actions-" + status.Module.Name);
            actions.Add(CreateActionButton("定位 asmdef", () => LocatePath(status.Module.AsmdefPath),
                "在 Unity Project 中选中这个 Module 的程序集定义；引用列表是编译期真相。"));
            if (status.OwnedPreservations.Length > 0)
            {
                string linkPath = status.OwnedPreservations[0].Path;
                actions.Add(CreateActionButton("定位 link.xml", () => LocatePath(linkPath),
                    "在 Unity Project 中选中本 Module 的 UnityLinker 保留规则；需要编辑时再双击打开。"));
            }
            if (status.IsHotUpdateRoot)
                actions.Add(CreateActionButton("定位热更配置", OpenHotUpdateProfile,
                    "在单一真源中调整该程序集是否作为热更 DLL 部署。"));
            actions.Add(CreateActionButton("复制移除清单", () => CopyRemovalChecklist(status),
                "复制直接消费者、保留原因和安全移除顺序。"));
            card.Add(actions);
            return card;
        }

        private VisualElement CreateGlobalPreservationsFoldout(
            IReadOnlyCollection<FrameworkModuleAudit.LinkerPreservation> preservations)
        {
            var foldout = new Foldout
            {
                name = "module-audit-global-preservations",
                text = $"全局与生成的 link.xml（{preservations.Count} 条，仅供追踪）",
                value = false,
                style = { marginTop = 4, marginBottom = 4 },
            };
            foldout.Add(CreateInfoLabel(
                "这些规则不归属于某个 Framework Module，因此不直接算作模块边界失败。HybridCLRGenerate 是生成物，应修改来源配置后重新 Generate；第三方规则应先确认升级与反射边界，不能在这里一键删除。"));

            foreach (var group in preservations.GroupBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase))
            {
                var card = CreateCard("module-audit-global-link-" + Math.Abs(group.Key.GetHashCode()));
                string origin = group.First().IsGenerated ? "HybridCLR 生成物" : "项目 / 第三方规则";
                var title = Wrap(new Label(origin + " · " + group.Key));
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                card.Add(title);
                foreach (var rule in group)
                {
                    string condition = rule.IsUnconditional ? "无条件根" : "仅被引用时生效";
                    card.Add(CreateBullet(rule.AssemblyName + " · " + rule.Scope + " · " + condition));
                }
                card.Add(CreateActionButton("定位 link.xml", () => LocatePath(group.Key),
                    "在 Unity Project 中选中这份规则；生成文件只用于查看，不应直接修改。"));
                foldout.Add(card);
            }
            return foldout;
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
            card.Add(CreateCheckRow(!result.HasRetentionWarnings,
                "可选 Module 没有无条件 link.xml 根",
                result.HasRetentionWarnings
                    ? string.Join("；", result.UnconditionalModulePreservations.Select(rule =>
                        rule.OwnerModuleName + " → " + rule.AssemblyName + "（" + rule.Scope + "）"))
                    : "没有发现会独立成为 UnityLinker 根的 Module 内保留规则。"));
            card.Add(CreateCheckRow(!result.HasHotUpdateViolations,
                "热更 Profile 对引用关系闭合",
                result.HasHotUpdateViolations
                    ? string.Join("；", result.ModuleStatuses
                        .Where(status => status.HasHotUpdateViolation)
                        .Select(status => status.Module.Name + "（AOT）→ " +
                                          string.Join("、", status.HotUpdateDependencies) + "（热更）"))
                    : "没有发现 AOT Framework Module 直接引用热更程序集。"));
            if (result.HotUpdateDeployment?.BuildModuleAvailable == true)
                card.Add(CreateCheckRow(!result.HasHotUpdateDeploymentWarnings,
                    "热更配置与本地派生状态可解释",
                    result.HasHotUpdateDeploymentWarnings
                        ? "Profile 缺失 / 重复，或至少一层 Settings、Generate、DLL 中转证据已漂移；查看顶部热更产物链。"
                        : result.HotUpdateDeployment.StagingRequired
                            ? "唯一 Profile、HybridCLRSettings、Generate stamp 与 DLL 中转清单相互一致。"
                            : "唯一空 Profile 明确选择纯 AOT；Generate 与 DLL 中转不作强制要求。"));
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

        private void CopyRemovalChecklist(FrameworkModuleAudit.ModuleStatus status)
        {
            string text = status.Module.Name + " 移除准备\n" +
                          "Player 实际消费者：" +
                          (status.DirectConsumers.Length == 0
                              ? "（未发现）"
                              : string.Join("、", status.DirectConsumers)) + "\n\n" +
                          "asmdef 删除阻塞者：" +
                          (status.RemovalBlockers.Length == 0
                              ? "（未发现）"
                              : string.Join("、", status.RemovalBlockers)) + "\n\n" +
                          "当前保留原因：\n- " + string.Join("\n- ", status.RetentionReasons) + "\n\n" +
                          "安全顺序：\n- " + string.Join("\n- ", status.RemovalSteps);
            EditorGUIUtility.systemCopyBuffer = text;
            if (_status == null) return;
            _status.text = status.Module.Name + " 的移除准备清单已复制。";
            _status.messageType = HelpBoxMessageType.Info;
        }

        private void CopyHotUpdateEvidence(FrameworkModuleAudit.HotUpdateDeploymentEvidence evidence)
        {
            string text = "Framework 热更派生证据\n" +
                          evidence.Note + "\n\n" +
                          "HybridCLRSettings：" + evidence.SettingsMessage + "\n" +
                          "Generate：" + evidence.GenerationMessage + "\n" +
                          "DLL 中转：" + evidence.StagedMessage + "\n\n" +
                          "边界：中转一致只证明清单结构与当前派生输入相符、所列文件存在；" +
                          "不证明 DLL 内容相对源码新鲜，也不代表 YooAsset bundle 或 CDN 已部署。";
            EditorGUIUtility.systemCopyBuffer = text;
            if (_status == null) return;
            _status.text = "热更派生证据已复制。";
            _status.messageType = HelpBoxMessageType.Info;
        }

        private static void OpenHotUpdateProfile()
        {
            if (!EditorApplication.ExecuteMenuItem("SSFramework/热更构建/热更配置 (HotUpdate Profile)"))
                Debug.LogWarning("[ModuleAudit] 未安装热更构建 Module，无法定位 FrameworkHotUpdateProfile。");
        }

        private static void OpenFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string normalized = path.Replace('\\', '/');
            if (IsUnityAssetPath(normalized))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(normalized);
                if (asset != null) AssetDatabase.OpenAsset(asset);
                else Debug.LogWarning("[ModuleAudit] AssetDatabase 无法打开：" + normalized);
                return;
            }

            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath)) EditorUtility.OpenWithDefaultApp(fullPath);
            else Debug.LogWarning("[ModuleAudit] 找不到文档或资产：" + fullPath);
        }

        private static void LocatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string normalized = path.Replace('\\', '/');
            if (IsUnityAssetPath(normalized))
            {
                if (!TryLocateProjectAsset(normalized))
                    Debug.LogWarning("[ModuleAudit] AssetDatabase 无法定位：" + normalized);
                return;
            }

            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) || Directory.Exists(fullPath)) EditorUtility.RevealInFinder(fullPath);
            else Debug.LogWarning("[ModuleAudit] 找不到可定位的文件或目录：" + fullPath);
        }

        /// <summary>
        /// 在 Unity Project 中选中并闪烁项目资产，不触发外部编辑器。asmdef、link.xml、Profile 与 bytes
        /// 都由 AssetDatabase 管理；只有 Assets/Packages 之外的普通文件才回退到系统文件浏览器。
        /// </summary>
        internal static bool TryLocateProjectAsset(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string normalized = path.Replace('\\', '/');
            if (!IsUnityAssetPath(normalized)) return false;
            var asset = AssetDatabase.LoadMainAssetAtPath(normalized);
            if (asset == null) return false;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            return true;
        }

        private static bool IsUnityAssetPath(string path) =>
            path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);

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
