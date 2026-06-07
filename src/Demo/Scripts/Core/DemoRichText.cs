using System.Text;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// 把讲解文案里的轻量内联标记渲染成 UI Toolkit 富文本，给不同类别内容上色，让大段说明读起来有节奏：
    ///   · 反引号 <c>`code`</c> → 代码色（API / 类型 / 方法 / 路径等标识符）；
    ///   · 中文「术语」→ 术语色（专名 / 章节名 / 关键操作），直接沿用文案里现成的「」，无需额外标注。
    /// 每段文本都用 <c><![CDATA[<noparse>]]></c> 包裹，让 <c>List&lt;int&gt;</c> / <c>RP&lt;T&gt;</c> 等尖括号按字面显示、
    /// 不被富文本当成标签解析——UI Toolkit <b>不</b>认 <c>&amp;lt;</c> 这类 HTML 字符实体，只能靠 noparse；颜色标签套在 noparse 之外。
    /// </summary>
    /// <remarks>
    /// 仅用于 note / subnote / step / 概念说明 / 表格单元格这类<b>静态讲解文本</b>；
    /// 按钮、状态值、标题、Tip、徽标各有自己的强样式，不走这里，避免颜色相互打架。
    /// 两种标记互斥、不嵌套（先遇到谁进谁的模式）；未配对的 <c>`</c> 或 <c>「」</c> 按普通文本处理，不会吞掉后续内容。
    /// 标注约定：反引号要包住<b>完整</b>的代码单元，别把 <c>GameObject(prefab)</c> 切成「青+白」两半。
    /// </remarks>
    internal static class DemoRichText
    {
        // 暗底（demo-root rgb(24,25,30)）上的两类强调色：代码偏青、术语偏紫，均低饱和，
        // 刻意避开蓝（标题 / 源码链接）、黄（Tip）、绿 / 红（状态徽标）等已被占用的语义色。
        private const string CodeColor = "#7DCDD2";
        private const string TermColor = "#C6AAEB";

        /// <summary>
        /// 渲染为富文本。无标记的纯文本也安全：整段照原样（noparse 包裹）显示，可对所有讲解文本无脑调用。
        /// </summary>
        public static string Render(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            var sb = new StringBuilder(raw.Length + 64);
            int plainStart = 0;

            // 把 [plainStart, end) 这段普通文本以 noparse 原样输出（尖括号按字面显示）。
            void FlushPlain(int end)
            {
                if (end > plainStart)
                {
                    sb.Append("<noparse>");
                    sb.Append(raw, plainStart, end - plainStart);
                    sb.Append("</noparse>");
                }
            }

            int i = 0;
            while (i < raw.Length)
            {
                char c = raw[i];
                if (c == '`')
                {
                    int e = raw.IndexOf('`', i + 1);
                    if (e > i)
                    {
                        FlushPlain(i);
                        // 代码段：去掉反引号本身，内容用 noparse 原样显示（含泛型尖括号），外套代码色。
                        sb.Append("<color=").Append(CodeColor).Append("><noparse>");
                        sb.Append(raw, i + 1, e - i - 1);
                        sb.Append("</noparse></color>");
                        i = e + 1;
                        plainStart = i;
                        continue;
                    }
                }
                else if (c == '「')
                {
                    int e = raw.IndexOf('」', i + 1);
                    if (e > i)
                    {
                        FlushPlain(i);
                        // 术语段：连「」一起，外套术语色。
                        sb.Append("<color=").Append(TermColor).Append("><noparse>");
                        sb.Append(raw, i, e - i + 1);
                        sb.Append("</noparse></color>");
                        i = e + 1;
                        plainStart = i;
                        continue;
                    }
                }
                i++;
            }
            FlushPlain(raw.Length);
            return sb.ToString();
        }
    }
}
