using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// 传给每个模块 <see cref="IDemoModule.Build"/> 的宿主。模块往 <see cref="Content"/> 塞 UI，
    /// 并用这里的便利方法构造统一风格的小节 / 说明 / 提示 / 动作行。
    /// </summary>
    /// <remarks>
    /// 所有元素只挂 USS class（具体样式集中在 <c>DemoTheme.uss</c>），保持"整体风格与展示内容分离"——
    /// 想换肤只改 USS，不动模块代码。动作行把"演示按钮"和"跳转源码"紧挨着排成一行，方便边看边跳。
    /// </remarks>
    public sealed class DemoModuleHost
    {
        /// <summary>模块内容根容器。模块把自己的 UI 都加到这里。</summary>
        public VisualElement Content { get; }

        // 当前 Add* 的目标容器栈：空栈时落到根 Content（默认）。用于把一段构建临时塞进子容器（如分栏布局的某一列）。
        private readonly Stack<VisualElement> _targets = new();

        // 后续 Add* 实际落到的容器：栈顶优先，否则根 Content。
        private VisualElement Target => _targets.Count > 0 ? _targets.Peek() : Content;

        public DemoModuleHost(VisualElement content) => Content = content;

        /// <summary>
        /// 临时把后续 <c>Add*</c> 的目标切到 <paramref name="container"/>（如分栏布局里的某一列），
        /// 让分栏内的按钮 / 值显示照样复用统一的样式与源码跳转约定，而不必手搓 VisualElement 重复一遍这些逻辑。
        /// 用 <c>using</c> 包住一段构建，作用域结束自动恢复上一层目标：
        /// <code><![CDATA[
        /// host.Content.Add(row);          // 先搭好分栏骨架
        /// using (host.Into(leftColumn))   // 后续 Add* 落到 leftColumn
        ///     host.AddActionRow("做点什么", DoThing);
        /// ]]></code>
        /// </summary>
        public IDisposable Into(VisualElement container)
        {
            _targets.Push(container);
            return new TargetScope(this);
        }

        // Into 的配套作用域：Dispose 时弹出目标、恢复到上一层。幂等，重复 Dispose 无副作用。
        private sealed class TargetScope : IDisposable
        {
            private readonly DemoModuleHost _host;
            private bool _disposed;
            public TargetScope(DemoModuleHost host) => _host = host;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _host._targets.Pop();
            }
        }

        /// <summary>小节标题。</summary>
        public Label AddSectionTitle(string text)
        {
            var l = new Label(text);
            l.AddToClassList("demo-section-title");
            Target.Add(l);
            return l;
        }

        /// <summary>讲解段落（说明使用方法 / 设计理念）。自动换行。</summary>
        public Label AddNote(string text)
        {
            var l = new Label(text);
            l.AddToClassList("demo-note");
            l.style.whiteSpace = WhiteSpace.Normal;
            l.enableRichText = false; // 讲解文本常含 List<int> / RP<T> 等泛型尖括号，关掉富文本免得 <T> 被当标签吞掉
            Target.Add(l);
            return l;
        }

        /// <summary>
        /// 讲解段落 + 行末紧跟一个源码跳转——让"说明"和"它的源码"待在一起，不用翻到最底下找链接。
        /// </summary>
        public Label AddNote(string text, CodeRef code)
        {
            var row = new VisualElement();
            row.AddToClassList("demo-note-row");

            var l = new Label(text);
            l.AddToClassList("demo-note");
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.flexGrow = 1;
            l.enableRichText = false;
            row.Add(l);

            AppendCodeLink(row, code);
            Target.Add(row);
            return l;
        }

        /// <summary>提示 / 注意事项（强调样式）。自动换行。</summary>
        public Label AddTip(string text)
        {
            var l = new Label(text);
            l.AddToClassList("demo-tip");
            l.style.whiteSpace = WhiteSpace.Normal;
            l.enableRichText = false;
            Target.Add(l);
            return l;
        }

        /// <summary>
        /// 概念条目：左侧加粗术语 + 右侧说明，用于"定义列表"式讲解（分层职责、概念释义等）。说明自动换行。
        /// </summary>
        public VisualElement AddConcept(string term, string description)
        {
            var row = new VisualElement();
            row.AddToClassList("demo-concept");

            var t = new Label(term);
            t.AddToClassList("demo-concept-term");
            t.enableRichText = false;
            row.Add(t);

            var d = new Label(description);
            d.AddToClassList("demo-concept-desc");
            d.style.whiteSpace = WhiteSpace.Normal;
            d.enableRichText = false;
            row.Add(d);

            Target.Add(row);
            return row;
        }

        /// <summary>
        /// 一行动作：<c>[按钮] [&lt;/&gt; 源码(可选)]</c>。演示操作与其源码跳转紧挨着排，
        /// 看到效果就能一键跳到真实代码。Build 环境下不显示源码链接（<see cref="CodeNavigator.IsAvailable"/> 为 false）。
        /// </summary>
        /// <returns>动作按钮本身，便于调用方进一步配置（禁用、改文案等）。</returns>
        public Button AddActionRow(string buttonText, Action onClick, CodeRef code = default)
        {
            var row = new VisualElement();
            row.AddToClassList("demo-action-row");

            var btn = new Button(onClick) { text = buttonText };
            btn.AddToClassList("demo-btn");
            row.Add(btn);

            AppendCodeLink(row, code);
            Target.Add(row);
            return btn;
        }

        /// <summary>
        /// 动态值显示（如计数器当前值），返回 Label 供模块订阅后更新 <c>text</c>。
        /// 传入 <paramref name="code"/> 时，值右侧紧跟一个源码跳转（通常指向喂这个值的查询 Command），
        /// 方便从“看到的状态”一键跳到“它从哪来”。
        /// </summary>
        public Label AddValueDisplay(string initial = "", CodeRef code = default)
        {
            var l = new Label(initial);
            l.AddToClassList("demo-value");

            if (code.HasTarget && CodeNavigator.IsAvailable)
            {
                l.style.marginBottom = 0; // 间距交给外层行，避免链接相对文字偏上
                var row = new VisualElement();
                row.AddToClassList("demo-value-row");
                row.Add(l);
                AppendCodeLink(row, code);
                Target.Add(row);
            }
            else
            {
                Target.Add(l);
            }
            return l;
        }

        /// <summary>单独的一个源码跳转链接（不带动作按钮）。Build 环境下不显示。</summary>
        public void AddCodeLink(CodeRef code)
        {
            var row = new VisualElement();
            row.AddToClassList("demo-action-row");
            if (AppendCodeLink(row, code))
                Target.Add(row);
        }

        // 若 code 有效且当前可跳转，往 row 追加一个源码链接按钮。返回是否追加了。
        private static bool AppendCodeLink(VisualElement row, CodeRef code)
        {
            if (!code.HasTarget || !CodeNavigator.IsAvailable) return false;
            var label = string.IsNullOrEmpty(code.Label) ? "查看源码" : "查看源码 · " + code.Label;
            var link = new Button(() => CodeNavigator.Open(code)) { text = label };
            link.AddToClassList("demo-code-link");
            link.tooltip = "在 IDE 中打开：" + code.Path + (string.IsNullOrEmpty(code.Anchor) ? "" : "  ▸ " + code.Anchor);
            row.Add(link);
            return true;
        }
    }
}
