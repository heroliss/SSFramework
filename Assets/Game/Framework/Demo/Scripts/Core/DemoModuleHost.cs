using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
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
    public sealed class DemoModuleHost : IDisposable
    {
        /// <summary>模块内容根容器。模块把自己的 UI 都加到这里。</summary>
        public VisualElement Content { get; }

        // 当前 Add* 的目标容器栈：空栈时落到根 Content（默认）。用于把一段构建临时塞进子容器（如分栏布局的某一列）。
        private readonly Stack<VisualElement> _targets = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly DemoTeachingTrace _teachingTrace = new();
        private bool _experimentNoticeAvailableInSection;
        private bool _disposed;

        // 后续 Add* 实际落到的容器：栈顶优先，否则根 Content。
        private VisualElement Target => _targets.Count > 0 ? _targets.Peek() : Content;

        /// <summary>本次真实 Build 产生的教学语义；由目录在章节对外可见前统一校验。</summary>
        internal DemoTeachingTrace TeachingTrace => _teachingTrace;

        public DemoModuleHost(VisualElement content) => Content = content;

        /// <summary>
        /// 结束本次章节 UI 的生命周期：取消仍在执行的异步动作。章节目录在切章、重建 UIDocument 或销毁时调用；
        /// 普通模块无需手动处理。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _lifetimeCts.Cancel();
            }
            catch (Exception e)
            {
                Log.Error("Demo action cancellation callback threw while closing the chapter.", e, "DemoAction");
            }
            finally
            {
                _lifetimeCts.Dispose();
            }
        }

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

        /// <summary>
        /// 章节开篇定位：一句话说明“这一章解决什么问题、边界在哪里”。必须是正常章节的第一个教学元素，
        /// Host 统一补上“定位：”前缀，避免普通小节伪装成定位而绕过教学契约。
        /// </summary>
        public Label AddPositioning(string text)
        {
            _experimentNoticeAvailableInSection = false;
            _teachingTrace.Record(DemoTeachingElement.Positioning);
            return AddSectionTitleCore("定位：" + text);
        }

        /// <summary>小节标题。开篇定位请使用 <see cref="AddPositioning"/>。</summary>
        public Label AddSectionTitle(string text)
        {
            _experimentNoticeAvailableInSection = false;
            _teachingTrace.Record(DemoTeachingElement.Section);
            return AddSectionTitleCore(text);
        }

        private Label AddSectionTitleCore(string text)
        {
            var l = new Label(text);
            l.AddToClassList("demo-section-title");
            Target.Add(l);
            return l;
        }

        /// <summary>讲解段落（说明使用方法 / 设计理念）。自动换行。</summary>
        public Label AddNote(string text)
        {
            _teachingTrace.Record(DemoTeachingElement.Note);
            var l = new Label(DemoRichText.Render(text));
            l.AddToClassList("demo-note");
            l.style.whiteSpace = WhiteSpace.Normal;
            // 富文本由 DemoRichText 统一渲染：`code` 染代码色、「术语」染术语色，并把裸尖括号转义掉让 List<int>/RP<T> 照常显示
            Target.Add(l);
            return l;
        }

        /// <summary>
        /// 讲解段落 + 行末紧跟一个源码跳转——让"说明"和"它的源码"待在一起，不用翻到最底下找链接。
        /// </summary>
        public Label AddNote(string text, CodeRef code)
        {
            _teachingTrace.Record(DemoTeachingElement.Note, code);
            var row = new VisualElement();
            row.AddToClassList("demo-note-row");

            var l = new Label(DemoRichText.Render(text));
            l.AddToClassList("demo-note");
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.flexGrow = 1;
            row.Add(l);

            AppendCodeLink(row, code);
            Target.Add(row);
            return l;
        }

        /// <summary>
        /// 手把手步骤行：左侧一个序号徽标（如 <c>⓪①②③④</c>）+ 右侧步骤说明，可带行末源码跳转。
        /// 把一串操作渲染成有序流程，比一堆同级 note 更易按顺序扫读。
        /// </summary>
        public Label AddStep(string badge, string text, CodeRef code = default)
        {
            _teachingTrace.Record(DemoTeachingElement.Step, code);
            var row = new VisualElement();
            row.AddToClassList("demo-step");

            var b = new Label(badge);
            b.AddToClassList("demo-step-badge");
            b.enableRichText = false;
            row.Add(b);

            var l = new Label(DemoRichText.Render(text));
            l.AddToClassList("demo-step-text");
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.flexGrow = 1;
            row.Add(l);

            AppendCodeLink(row, code);
            Target.Add(row);
            return l;
        }

        /// <summary>
        /// 次级细节说明：缩进 + 更小更暗，用于挂在某条主干（步骤 / 说明）之下的"展开看"补充内容
        /// （如清单文件里写了啥、某步背后的原理），与正文 note 拉开层级、缓解长文的视觉压迫。可带行末源码跳转。
        /// </summary>
        public Label AddSubNote(string text, CodeRef code = default)
        {
            _teachingTrace.Record(DemoTeachingElement.SubNote, code);
            if (!code.HasTarget || !CodeNavigator.IsAvailable)
            {
                var only = new Label(DemoRichText.Render(text));
                only.AddToClassList("demo-subnote");
                only.style.whiteSpace = WhiteSpace.Normal;
                Target.Add(only);
                return only;
            }

            var row = new VisualElement();
            row.AddToClassList("demo-note-row");

            var l = new Label(DemoRichText.Render(text));
            l.AddToClassList("demo-subnote");
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.flexGrow = 1;
            row.Add(l);

            AppendCodeLink(row, code);
            Target.Add(row);
            return l;
        }

        /// <summary>提示 / 注意事项（强调样式）。自动换行。</summary>
        public Label AddTip(string text)
        {
            _teachingTrace.Record(DemoTeachingElement.Tip);
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
            _teachingTrace.Record(DemoTeachingElement.Concept);
            var row = new VisualElement();
            row.AddToClassList("demo-concept");

            var t = new Label(term);
            t.AddToClassList("demo-concept-term");
            t.enableRichText = false;
            row.Add(t);

            var d = new Label(DemoRichText.Render(description));
            d.AddToClassList("demo-concept-desc");
            d.style.whiteSpace = WhiteSpace.Normal;
            row.Add(d);

            Target.Add(row);
            return row;
        }

        /// <summary>
        /// 教学实验提示卡：在执行故意失败、持久写入或共享状态变更前，统一说明影响范围、
        /// 可观察证据与恢复动作。这里只约束信息结构，不代替章节捕获预期异常；未处理异常仍代表 Demo 缺陷。
        /// </summary>
        public VisualElement AddExperimentNotice(
            string impact,
            string expectedEvidence,
            string recovery,
            CodeRef code = default)
        {
            EnsureExperimentText(impact, nameof(impact));
            EnsureExperimentText(expectedEvidence, nameof(expectedEvidence));
            EnsureExperimentText(recovery, nameof(recovery));

            _teachingTrace.Record(DemoTeachingElement.ExperimentNotice, code);
            _experimentNoticeAvailableInSection = true;

            var card = new VisualElement();
            card.AddToClassList("demo-experiment");

            var titleRow = new VisualElement();
            titleRow.AddToClassList("demo-experiment-title-row");
            var title = new Label("教学实验 · 执行前先看");
            title.AddToClassList("demo-experiment-title");
            titleRow.Add(title);
            AppendCodeLink(titleRow, code);
            card.Add(titleRow);

            AddExperimentRow(card, "影响范围", impact);
            AddExperimentRow(card, "预期证据", expectedEvidence);
            AddExperimentRow(card, "恢复方式", recovery);

            Target.Add(card);
            return card;
        }

        private static void AddExperimentRow(VisualElement card, string term, string description)
        {
            var row = new VisualElement();
            row.AddToClassList("demo-experiment-row");

            var termLabel = new Label(term);
            termLabel.AddToClassList("demo-experiment-term");
            termLabel.enableRichText = false;
            row.Add(termLabel);

            var descLabel = new Label(DemoRichText.Render(description));
            descLabel.AddToClassList("demo-experiment-desc");
            descLabel.style.whiteSpace = WhiteSpace.Normal;
            row.Add(descLabel);
            card.Add(row);
        }

        private static void EnsureExperimentText(string text, string paramName)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("教学实验必须说明影响范围、预期证据和恢复方式。", paramName);
        }

        /// <summary>
        /// 对比表格：表头行 + 若干数据行，等宽列、单元格自动换行。用于把多项并排对比
        /// （如各 PlayMode / 各目录 / 各清单文件 的作用差异），比一堆 note 更易横向比读。
        /// 每个数据行的单元格数应与 <paramref name="headers"/> 一致。
        /// </summary>
        public VisualElement AddTable(string[] headers, params string[][] rows)
        {
            _teachingTrace.Record(DemoTeachingElement.Table);
            var table = new VisualElement();
            table.AddToClassList("demo-table");

            table.Add(BuildTableRow(headers, isHead: true));
            if (rows != null)
                foreach (var row in rows)
                    table.Add(BuildTableRow(row, isHead: false));

            Target.Add(table);
            return table;
        }

        // 单行：表头或数据；单元格文本经 DemoRichText 渲染（上色 + 转义尖括号），等宽由 USS .demo-table-cell 控制。
        private static VisualElement BuildTableRow(string[] cells, bool isHead)
        {
            var row = new VisualElement();
            row.AddToClassList("demo-table-row");
            if (isHead) row.AddToClassList("demo-table-row--head");

            if (cells != null)
            {
                foreach (var c in cells)
                {
                    var cell = new Label(DemoRichText.Render(c ?? string.Empty));
                    cell.AddToClassList("demo-table-cell");
                    if (isHead) cell.AddToClassList("demo-table-cell--head");
                    cell.style.whiteSpace = WhiteSpace.Normal;
                    row.Add(cell);
                }
            }
            return row;
        }

        /// <summary>
        /// 一行动作：<c>[按钮] [&lt;/&gt; 源码(可选)]</c>。演示操作与其源码跳转紧挨着排，
        /// 看到效果就能一键跳到真实代码。Build 环境下不显示源码链接（<see cref="CodeNavigator.IsAvailable"/> 为 false）。
        /// </summary>
        /// <returns>动作按钮本身，便于调用方进一步配置（禁用、改文案等）。</returns>
        public Button AddActionRow(string buttonText, Action onClick, CodeRef code = default)
            => AddActionRowCore(buttonText, onClick, code, DemoTeachingElement.Action, isExperiment: false);

        /// <summary>
        /// 当前小节中的教学实验动作。必须先调用 <see cref="AddExperimentNotice"/>；Host 自动添加稳定按钮前缀与样式，
        /// 只记录语义、不捕获回调异常，避免把真实 Demo 缺陷伪装成预期失败。
        /// </summary>
        public Button AddExperimentActionRow(string buttonText, Action onClick, CodeRef code = default)
        {
            EnsureExperimentNoticeForAction();
            return AddActionRowCore(
                NormalizeExperimentButtonText(buttonText),
                onClick,
                code,
                DemoTeachingElement.ExperimentAction,
                isExperiment: true);
        }

        private Button AddActionRowCore(
            string buttonText,
            Action onClick,
            CodeRef code,
            DemoTeachingElement element,
            bool isExperiment)
        {
            if (onClick == null) throw new ArgumentNullException(nameof(onClick));
            _teachingTrace.Record(element, code);
            var row = new VisualElement();
            row.AddToClassList("demo-action-row");

            var btn = new Button(onClick) { text = buttonText };
            btn.AddToClassList("demo-btn");
            if (isExperiment) btn.AddToClassList("demo-btn--experiment");
            row.Add(btn);

            AppendCodeLink(row, code);
            Target.Add(row);
            return btn;
        }

        [Obsolete("异步教学实验按钮必须使用 AddExperimentAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddExperimentActionRow(string buttonText, Func<UniTask> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步教学实验按钮必须使用 AddExperimentAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddExperimentActionRow(string buttonText, Func<UniTaskVoid> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步教学实验按钮必须使用 AddExperimentAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddExperimentActionRow(string buttonText, Func<System.Threading.Tasks.Task> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步教学实验按钮必须使用 AddExperimentAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddExperimentActionRow(string buttonText, Func<System.Threading.Tasks.ValueTask> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步教学实验按钮必须使用 AddExperimentAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddExperimentActionRow<T>(string buttonText, Func<UniTask<T>> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步教学实验按钮必须使用 AddExperimentAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddExperimentActionRow<T>(string buttonText, Func<System.Threading.Tasks.Task<T>> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步教学实验按钮必须使用 AddExperimentAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddExperimentActionRow<T>(string buttonText, Func<System.Threading.Tasks.ValueTask<T>> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        // 这组不可调用重载是编译期护栏：返回 task 的表达式 lambda 本来也能被 C# 当成 Action，悄悄丢掉返回值。
        // 给常见异步返回类型提供更精确的候选后，错误写法会命中 [Obsolete(error: true)]，在编译期被引导到带生命周期令牌的入口。
        [Obsolete("异步 Demo 按钮必须使用 AddAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddActionRow(string buttonText, Func<UniTask> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步 Demo 按钮必须使用 AddAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddActionRow(string buttonText, Func<UniTaskVoid> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步 Demo 按钮必须使用 AddAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddActionRow(string buttonText, Func<System.Threading.Tasks.Task> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步 Demo 按钮必须使用 AddAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddActionRow<T>(string buttonText, Func<UniTask<T>> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步 Demo 按钮必须使用 AddAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddActionRow<T>(string buttonText, Func<System.Threading.Tasks.Task<T>> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步 Demo 按钮必须使用 AddAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddActionRow(string buttonText, Func<System.Threading.Tasks.ValueTask> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        [Obsolete("异步 Demo 按钮必须使用 AddAsyncActionRow(Func<CancellationToken, UniTask>)。", true)]
        public Button AddActionRow<T>(string buttonText, Func<System.Threading.Tasks.ValueTask<T>> onClick, CodeRef code = default)
            => throw new NotSupportedException();

        /// <summary>
        /// 一行异步动作。点击后按钮会保持禁用直到任务结束，避免重复提交；切章 / UI 重建时通过
        /// <paramref name="onClick"/> 收到的令牌取消；未处理异常统一进入框架日志，不会退化成不可观察的
        /// <c>async void</c>。业务可预期的失败仍应在回调内捕获并就近显示。
        /// </summary>
        /// <returns>动作按钮本身，便于调用方进一步配置文案等展示状态。</returns>
        public Button AddAsyncActionRow(
            string buttonText,
            Func<CancellationToken, UniTask> onClick,
            CodeRef code = default)
            => AddAsyncActionRowCore(buttonText, onClick, code, DemoTeachingElement.Action, isExperiment: false);

        /// <summary>
        /// 当前小节中的异步教学实验动作。生命周期、禁用与异常兜底语义同 <see cref="AddAsyncActionRow"/>；
        /// 章节仍应只在回调内捕获可精确识别的预期失败。
        /// </summary>
        public Button AddExperimentAsyncActionRow(
            string buttonText,
            Func<CancellationToken, UniTask> onClick,
            CodeRef code = default)
        {
            EnsureExperimentNoticeForAction();
            return AddAsyncActionRowCore(
                NormalizeExperimentButtonText(buttonText),
                onClick,
                code,
                DemoTeachingElement.ExperimentAction,
                isExperiment: true);
        }

        private Button AddAsyncActionRowCore(
            string buttonText,
            Func<CancellationToken, UniTask> onClick,
            CodeRef code,
            DemoTeachingElement element,
            bool isExperiment)
        {
            if (onClick == null) throw new ArgumentNullException(nameof(onClick));
            if (_disposed) throw new ObjectDisposedException(nameof(DemoModuleHost));

            _teachingTrace.Record(element, code);

            var row = new VisualElement();
            row.AddToClassList("demo-action-row");

            var btn = new Button { text = buttonText };
            btn.AddToClassList("demo-btn");
            if (isExperiment) btn.AddToClassList("demo-btn--experiment");
            var binding = new DemoAsyncActionBinding(btn, onClick, _lifetimeCts.Token, buttonText);
            btn.clicked += () => binding.Invoke().Forget();
            row.Add(btn);

            AppendCodeLink(row, code);
            Target.Add(row);
            return btn;
        }

        private void EnsureExperimentNoticeForAction()
        {
            if (!_experimentNoticeAvailableInSection)
                throw new InvalidOperationException(
                    "教学实验动作前必须在同一小节调用 AddExperimentNotice，说明影响范围、预期证据和恢复方式。");
        }

        private static string NormalizeExperimentButtonText(string buttonText)
        {
            if (string.IsNullOrWhiteSpace(buttonText))
                throw new ArgumentException("教学实验按钮必须有可读名称。", nameof(buttonText));
            const string prefix = "教学实验 · ";
            return buttonText.StartsWith(prefix, StringComparison.Ordinal) ? buttonText : prefix + buttonText;
        }

        /// <summary>
        /// 动态值显示（如计数器当前值），返回 Label 供模块订阅后更新 <c>text</c>。
        /// 传入 <paramref name="code"/> 时，值右侧紧跟一个源码跳转（通常指向喂这个值的查询 Command），
        /// 方便从“看到的状态”一键跳到“它从哪来”。
        /// </summary>
        public Label AddValueDisplay(string initial = "", CodeRef code = default)
        {
            _teachingTrace.Record(DemoTeachingElement.Value, code);
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
            _teachingTrace.Record(DemoTeachingElement.CodeReference, code);
            var row = new VisualElement();
            row.AddToClassList("demo-action-row");
            if (AppendCodeLink(row, code))
                Target.Add(row);
        }

        /// <summary>
        /// 构建“当前环境暂不可运行”的结构化降级页。说明缺失原因、恢复方式与继续学习的入口，
        /// 并提供一处能帮助修复接线的源码跳转；这种页面走独立契约，不会被误判为正常交互章节。
        /// </summary>
        public void AddUnavailable(string reason, string recovery, string continuation, CodeRef setupCode)
        {
            _teachingTrace.RecordUnavailable(reason, recovery, continuation, setupCode);
            AddSectionTitleCore("本章当前暂不可运行");
            AddConceptCore("为什么不可用", reason);
            AddConceptCore("如何恢复", recovery);
            AddConceptCore("接下来怎么学", continuation, setupCode);
        }

        private void AddConceptCore(string term, string description, CodeRef code = default)
        {
            var row = new VisualElement();
            row.AddToClassList("demo-concept");

            var t = new Label(term);
            t.AddToClassList("demo-concept-term");
            t.enableRichText = false;
            row.Add(t);

            var d = new Label(DemoRichText.Render(description));
            d.AddToClassList("demo-concept-desc");
            d.style.whiteSpace = WhiteSpace.Normal;
            d.style.flexGrow = 1;
            row.Add(d);

            AppendCodeLink(row, code);
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

    /// <summary>
    /// 单个异步按钮的运行期闸门。独立成小对象是为了让重复点击、取消和异常策略可直接做 EditMode 行为测试。
    /// </summary>
    internal sealed class DemoAsyncActionBinding
    {
        private readonly Button _button;
        private readonly Func<CancellationToken, UniTask> _action;
        private readonly CancellationToken _lifetimeToken;
        private readonly Action<Exception> _reportException;
        private bool _running;

        internal DemoAsyncActionBinding(
            Button button,
            Func<CancellationToken, UniTask> action,
            CancellationToken lifetimeToken,
            string actionName,
            Action<Exception> reportException = null)
        {
            _button = button ?? throw new ArgumentNullException(nameof(button));
            _action = action ?? throw new ArgumentNullException(nameof(action));
            _lifetimeToken = lifetimeToken;
            _reportException = reportException ?? (e =>
                Log.Error($"Demo action '{actionName}' failed.", e, "DemoAction"));
        }

        internal async UniTask Invoke()
        {
            if (_running || _lifetimeToken.IsCancellationRequested) return;

            _running = true;
            _button.SetEnabled(false);
            try
            {
                await _action(_lifetimeToken);
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消或切章都属于正常控制流；需要解释取消原因的章节可在回调内先捕获并更新文案。
            }
            catch (Exception e)
            {
                _reportException(e);
            }
            finally
            {
                _running = false;
                if (!_lifetimeToken.IsCancellationRequested)
                    _button.SetEnabled(true);
            }
        }
    }
}
