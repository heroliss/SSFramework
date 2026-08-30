using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor
{
    /// <summary>解释稳定 AI 自动化菜单的即时执行语义、影响与人工替代入口。</summary>
    public sealed class FrameworkAutomationGuideWindow : EditorWindow
    {
        private VisualElement _flow;

        [MenuItem(FrameworkMenuPaths.AutomationGuide, priority = 1)]
        public static void Open() => GetWindow<FrameworkAutomationGuideWindow>("AI 自动化说明").Show();

        public void CreateGUI()
        {
            minSize = new Vector2(360f, 420f);
            VisualElement root = rootVisualElement;
            root.Clear();
            FrameworkEditorVisuals.ApplyWindowSurface(root);

            var scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "automation-guide-content",
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto,
                style = { flexGrow = 1 },
            };
            scroll.contentContainer.style.paddingLeft = 10;
            scroll.contentContainer.style.paddingRight = 10;
            scroll.contentContainer.style.paddingBottom = 12;
            root.Add(scroll);

            scroll.Add(FrameworkEditorVisuals.CreateHero(
                "automation-guide-header",
                "MCP / CI INTERFACE · 点击即执行",
                "AI 自动化菜单说明",
                "这个子菜单面向机器调用：稳定路径本身就是命令 Interface，所以不会先打开窗口，也不会弹确认框。",
                FrameworkEditorVisuals.Tone.Warning));

            var boundary = FrameworkEditorVisuals.CreateCard(
                "automation-guide-boundary", FrameworkEditorVisuals.Tone.Warning);
            boundary.Add(FrameworkEditorVisuals.CreateCardTitle("为什么不能先弹确认？"));
            boundary.Add(FrameworkEditorVisuals.CreateMutedLabel(
                "MCP 与 CI 需要在无焦点、无人值守的情况下得到确定结果。窗口按钮或模态确认会重新引入焦点依赖，" +
                "也可能占住 Unity 主线程队列。人工操作请从工具中心进入说明充分的工作台；下列三项只在你明确需要机器契约时点击。"));
            scroll.Add(boundary);

            _flow = new VisualElement
            {
                name = "automation-guide-flow",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = UnityEngine.UIElements.Wrap.NoWrap,
                    marginTop = 4,
                    marginBottom = 7,
                },
            };
            _flow.Add(FrameworkEditorVisuals.CreateMetric(
                "automation-guide-human", "人工使用", "打开工作台", "先读说明，再点击动作"));
            _flow.Add(FrameworkEditorVisuals.CreateMetric(
                "automation-guide-machine", "AI / CI", "调用稳定菜单", "无需窗口与焦点"));
            _flow.Add(FrameworkEditorVisuals.CreateMetric(
                "automation-guide-evidence", "完成判据", "Console / 报告", "不以菜单存在冒充成功"));
            scroll.Add(_flow);

            scroll.Add(FrameworkEditorVisuals.CreateSectionTitle("三个稳定机器 Interface"));
            scroll.Add(CreateInterfaceCard(
                "automation-guide-preflight",
                "PlayMode 测试预检（保存脏场景）",
                "会立即保存所有“已加载、脏、且已有资产路径”的场景；发现未命名场景、Unity 忙碌、Player Build 或 Play Mode 时整批拒绝。它只建立无弹窗前置条件，不会自动启动测试。",
                "Console 必须出现 [SSFramework.Automation] READY；失败为 BLOCKED 并传播给调用方。",
                FrameworkEditorVisuals.Tone.Active));
            scroll.Add(CreateInterfaceCard(
                "automation-guide-core-build",
                "Core 隔离构建（Player Build）",
                "会在 Library/SSFramework/BuildSizeProbe 创建隔离工程、冻结当前 Core 输入并启动隐藏 Unity 子进程。首次 IL2CPP 构建可能耗时数分钟。",
                "菜单只证明任务被接受；最终成功以本轮 report.json / report.md 与子进程日志为准。",
                FrameworkEditorVisuals.Tone.Warning));
            scroll.Add(CreateInterfaceCard(
                "automation-guide-common-build",
                "常用档位隔离构建（Core + UGUI + Toolkit）",
                "与 Core 入口相同，但会顺序构建三个互相隔离的档位；每档重建派生状态，耗时通常明显更长。",
                "最终报告必须包含本轮 Core、UGUI、Toolkit 三项；缺项或失败不能解释为完整矩阵通过。",
                FrameworkEditorVisuals.Tone.Warning));

            var actions = new VisualElement
            {
                name = "automation-guide-actions",
                style = { flexDirection = FlexDirection.Row, marginTop = 8 },
            };
            actions.Add(FrameworkEditorVisuals.CreateActionButton(
                "打开模块审计", FrameworkModuleAuditWindow.Open,
                "人工查看依赖、保留根和删除边界。", "automation-guide-open-audit", primary: true));
            actions.Add(FrameworkEditorVisuals.CreateActionButton(
                "打开体积工作台", FrameworkBuildSizeProbeWindow.Open,
                "人工选择组合、阅读影响并启动构建。", "automation-guide-open-probe"));
            actions.Add(FrameworkEditorVisuals.CreateActionButton(
                "打开自动化文档", () => OpenDocument("docs/unity-mcp-tips.md"),
                "查看完整 MCP 测试与后台运行流程。", "automation-guide-open-docs"));
            scroll.Add(actions);

            root.RegisterCallback<GeometryChangedEvent>(evt => ApplyResponsiveLayout(actions, evt.newRect.width));
            ApplyResponsiveLayout(actions, position.width);
        }

        private static VisualElement CreateInterfaceCard(
            string name,
            string title,
            string effect,
            string evidence,
            FrameworkEditorVisuals.Tone tone)
        {
            VisualElement card = FrameworkEditorVisuals.CreateCard(name, tone);
            var heading = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            var titleLabel = FrameworkEditorVisuals.Wrap(new Label(title));
            titleLabel.style.flexGrow = 1;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.Add(titleLabel);
            var badge = new Label(tone == FrameworkEditorVisuals.Tone.Active ? "立即写入" : "立即启动 · 长耗时");
            badge.style.flexShrink = 0;
            badge.style.marginLeft = 8;
            badge.style.paddingLeft = 6;
            badge.style.paddingRight = 6;
            badge.style.paddingTop = 2;
            badge.style.paddingBottom = 2;
            badge.style.backgroundColor = FrameworkEditorVisuals.ToneColor(tone);
            badge.style.color = Color.white;
            badge.style.borderTopLeftRadius = 8;
            badge.style.borderTopRightRadius = 8;
            badge.style.borderBottomLeftRadius = 8;
            badge.style.borderBottomRightRadius = 8;
            heading.Add(badge);
            card.Add(heading);

            card.Add(FrameworkEditorVisuals.CreateMutedLabel("作用与影响：" + effect));
            var evidenceLabel = FrameworkEditorVisuals.Wrap(new Label("完成证据：" + evidence));
            evidenceLabel.style.marginTop = 3;
            evidenceLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(evidenceLabel);
            return card;
        }

        private void ApplyResponsiveLayout(VisualElement actions, float width)
        {
            bool compact = width < FrameworkEditorVisuals.CompactWidth;
            actions.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
            FrameworkEditorVisuals.ApplyResponsiveChildren(actions, compact);
            if (_flow == null) return;
            _flow.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
            _flow.style.flexWrap = UnityEngine.UIElements.Wrap.NoWrap;
            FrameworkEditorVisuals.ApplyResponsiveChildren(_flow, compact);
        }

        private static void OpenDocument(string relativePath)
        {
            if (!FrameworkProjectPath.TryResolve(
                    relativePath, out _, out string path, out string pathError))
            {
                Debug.LogWarning("[SSFramework.Automation] 文档路径无效：" + pathError);
                return;
            }

            if (File.Exists(path)) EditorUtility.OpenWithDefaultApp(path);
            else Debug.LogWarning("[SSFramework.Automation] 找不到文档：" + path);
        }
    }
}
