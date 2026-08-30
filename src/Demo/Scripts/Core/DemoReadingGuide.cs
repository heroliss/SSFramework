using System;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 外壳里的常驻阅读辅助：解释视觉语义，并把最常见的框架术语先翻译成白话。
    /// 关键知识始终直接显示在可展开面板里；<see cref="VisualElement.tooltip"/> 只做鼠标用户的补充，
    /// 避免 PlayMode tooltip 设置、触屏或键盘操作让新手丢失必要信息。
    /// </summary>
    internal sealed class DemoReadingGuide
    {
        private readonly Action<bool> _onExpandedChanged;
        private readonly VisualElement _body;
        private readonly Button _toggle;

        internal VisualElement Root { get; }
        internal bool IsExpanded { get; private set; }

        internal DemoReadingGuide(bool initiallyExpanded, Action<bool> onExpandedChanged = null)
        {
            _onExpandedChanged = onExpandedChanged;
            Root = new VisualElement();
            Root.AddToClassList("demo-reading-guide");

            var header = new VisualElement();
            header.AddToClassList("demo-reading-guide-header");
            Root.Add(header);

            var marker = new Label("新手导览");
            marker.AddToClassList("demo-reading-guide-marker");
            marker.enableRichText = false;
            header.Add(marker);

            var heading = new VisualElement();
            heading.AddToClassList("demo-reading-guide-heading");
            header.Add(heading);

            var title = new Label("先用 30 秒看懂颜色、按钮和常见术语");
            title.AddToClassList("demo-reading-guide-title");
            title.enableRichText = false;
            heading.Add(title);

            var summary = new Label("颜色只辅助扫读，文字标签才是可靠含义；关键解释不会只藏在悬停提示里。");
            summary.AddToClassList("demo-reading-guide-summary");
            summary.enableRichText = false;
            heading.Add(summary);

            _toggle = new Button(Toggle);
            _toggle.AddToClassList("demo-reading-guide-toggle");
            header.Add(_toggle);

            _body = new VisualElement();
            _body.AddToClassList("demo-reading-guide-body");
            Root.Add(_body);

            BuildLegend(_body);
            BuildGlossary(_body);
            SetExpanded(initiallyExpanded, notify: false);
        }

        internal void SetExpanded(bool expanded, bool notify = true)
        {
            IsExpanded = expanded;
            _body.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            _toggle.text = expanded ? "收起导览 ︿" : "展开导览 ﹀";
            _toggle.tooltip = expanded ? "收起颜色说明与常见术语" : "展开颜色说明与常见术语";
            if (notify) _onExpandedChanged?.Invoke(expanded);
        }

        private void Toggle() => SetExpanded(!IsExpanded);

        private static void BuildLegend(VisualElement parent)
        {
            var title = new Label("页面怎么读");
            title.AddToClassList("demo-reading-guide-section-title");
            title.enableRichText = false;
            parent.Add(title);

            var grid = new VisualElement();
            grid.AddToClassList("demo-reading-legend-grid");
            parent.Add(grid);

            AddLegendItem(grid, "概念", "定义、原理或设计取舍；本身不会执行代码。", "concept");
            AddLegendItem(grid, "普通演示", "蓝色按钮，执行本章的正常路径；结果会就近显示。", "action");
            AddLegendItem(grid, "重点速记", "青绿色提示，浓缩记忆点或常见误区；本身不会执行代码。", "tip");
            AddLegendItem(grid, "注意边界", "黄色提示，忽略后可能产生错误、泄漏或误判；仍然不会自动执行代码。", "caution");
            AddLegendItem(grid, "教学实验", "橙色区域与按钮，可能故意失败或改变文件、缓存、共享状态；先读影响、证据、恢复。", "experiment");

            var inlineHint = new Label(DemoRichText.Render(
                "正文中，`API / 类型 / 路径` 使用青色，中文「专业术语」使用紫色；它们只是文本类别，不代表可以点击。"));
            inlineHint.AddToClassList("demo-reading-inline-hint");
            inlineHint.style.whiteSpace = WhiteSpace.Normal;
            parent.Add(inlineHint);
        }

        private static void AddLegendItem(
            VisualElement parent,
            string badgeText,
            string description,
            string kind)
        {
            var item = new VisualElement();
            item.AddToClassList("demo-reading-legend-item");
            item.tooltip = description;

            var badge = new Label(badgeText);
            badge.AddToClassList("demo-reading-legend-badge");
            badge.AddToClassList("demo-reading-legend-badge--" + kind);
            badge.enableRichText = false;
            item.Add(badge);

            var text = new Label(description);
            text.AddToClassList("demo-reading-legend-text");
            text.enableRichText = false;
            text.style.whiteSpace = WhiteSpace.Normal;
            item.Add(text);
            parent.Add(item);
        }

        private static void BuildGlossary(VisualElement parent)
        {
            var title = new Label("常见术语 · 先记白话，再看精确定义");
            title.AddToClassList("demo-reading-guide-section-title");
            title.enableRichText = false;
            parent.Add(title);

            var grid = new VisualElement();
            grid.AddToClassList("demo-reading-glossary-grid");
            parent.Add(grid);

            AddGlossaryItem(grid, "Context（上下文 / 作用域）",
                "一块有边界的运行空间：在里面注册对象、寻找依赖，并决定这些对象何时一起释放。可先理解成“带生命周期的小容器”。");
            AddGlossaryItem(grid, "View（界面层）",
                "负责显示状态和接收输入。点击会被翻译成 Command；View 不直接改 Model，避免界面与真实数据各说各话。");
            AddGlossaryItem(grid, "Command（命令 / 意图）",
                "一次明确的请求，例如“购买药水”。它是外部操作进入业务逻辑的统一入口，不等同于系统命令行。");
            AddGlossaryItem(grid, "Model（数据层）",
                "会持续存在、随时可读的业务状态，例如金币、HP、任务进度；变化后可通知订阅者刷新。");
            AddGlossaryItem(grid, "System（逻辑层）",
                "一组可复用的业务规则，例如校验价格、扣款和发货。只有规则变复杂或会复用时才需要抽成 System。");
            AddGlossaryItem(grid, "Utility（基础设施层）",
                "与具体玩法无关的共享能力，例如资源、存储、网络和对象池；它不应该持有金币、背包等业务状态。");
            AddGlossaryItem(grid, "Bag / Dispose（生命周期清理）",
                "Bag 像一张清理清单：订阅、句柄和临时对象登记进去；宿主 Dispose 时统一释放，减少遗忘退订或泄漏。");
            AddGlossaryItem(grid, "Interface / Implementation（接口 / 实现）",
                "Interface 说明“能做什么”，Implementation 说明“具体怎么做”。只有确实需要替换、隔离或测试时，拆开才有价值。");
        }

        private static void AddGlossaryItem(VisualElement parent, string term, string description)
        {
            var item = new VisualElement();
            item.AddToClassList("demo-reading-glossary-item");
            item.tooltip = term + "\n\n" + description;

            var termLabel = new Label(term);
            termLabel.AddToClassList("demo-reading-glossary-term");
            termLabel.enableRichText = false;
            termLabel.style.whiteSpace = WhiteSpace.Normal;
            item.Add(termLabel);

            var descriptionLabel = new Label(description);
            descriptionLabel.AddToClassList("demo-reading-glossary-desc");
            descriptionLabel.enableRichText = false;
            descriptionLabel.style.whiteSpace = WhiteSpace.Normal;
            item.Add(descriptionLabel);
            parent.Add(item);
        }
    }
}
