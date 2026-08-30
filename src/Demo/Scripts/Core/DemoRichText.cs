using System;
using System.Text;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// 把讲解文案里的轻量内联标记渲染成 UI Toolkit 富文本，给不同类别内容上色 / 加粗，让大段说明读起来有节奏：
    ///   · 反引号 <c>`code`</c> → 代码色（API / 类型 / 方法 / 路径等标识符）；
    ///   · 中文「术语」→ 术语色（专名 / 章节名 / 关键操作），直接沿用文案里现成的「」，无需额外标注；
    ///   · markdown 式 <c>**强调**</c> → 加粗 + 提亮到中性白。强调走<b>字重 + 明度</b>（不是新增语义色相）——代码青 / 术语紫是「类别色」，强调只把正文从灰抬到最亮白做轻重对比，与那两类正交、不抢语义。
    /// 颜色段内容都用 <c><![CDATA[<noparse>]]></c> 包裹，让 <c>List&lt;int&gt;</c> / <c>RP&lt;T&gt;</c> 等尖括号按字面显示、
    /// 不被富文本当成标签解析——UI Toolkit <b>不</b>认 <c>&amp;lt;</c> 这类 HTML 字符实体，只能靠 noparse；颜色 / 加粗标签套在 noparse 之外。
    /// </summary>
    /// <remarks>
    /// 仅用于 note / subnote / step / 概念说明 / 表格单元格这类<b>静态讲解文本</b>；
    /// 按钮、状态值、标题、Tip、徽标各有自己的强样式，不走这里，避免颜色相互打架。
    /// 代码 / 术语两种<b>颜色</b>标记互斥、内容按字面照搬（不再解析内部标记）；<c>**强调**</c> 是<b>字重</b>标记，可外套在颜色之上
    /// （<c>**蓝色「UI Toolkit」**</c> 既加粗又保留术语色——内层递归渲染）。未配对的 <c>`</c> / <c>「」</c> / <c>**</c> 按普通文本处理，不吞后续内容。
    /// 标注约定：反引号要包住<b>完整</b>的代码单元，别把 <c>GameObject(prefab)</c> 切成「青+白」两半。
    /// </remarks>
    internal static class DemoRichText
    {
        // 暗底（demo-root rgb(24,25,30)）上的两类强调色：代码偏青、术语偏紫，均低饱和，
        // 刻意避开蓝（导航 / 普通动作）、青绿（Tip）、黄（Caution）、橙（Experiment）、绿 / 红（状态徽标）等语义色。
        private const string CodeColor = "#7DCDD2";
        private const string TermColor = "#C6AAEB";

        // 强调（**bold**）：加粗 + 提亮到中性白（= 标题 / 值显示用的最亮文本色 rgb(236,238,243)）。
        // 这不是第三种语义<b>色相</b>——代码青 / 术语紫是「类别」，强调只把正文从灰(~190)抬到最亮白做轻重对比，
        // 与那两类正交、不抢语义；加粗叠提亮双重线索，比单加粗在暗底上更跳。
        private const string EmphasisColor = "#ECEEF3";

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
                if (c == '*' && i + 1 < raw.Length && raw[i + 1] == '*')
                {
                    int e = raw.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (e > i)
                    {
                        FlushPlain(i);
                        // 强调段：加粗 + 提亮（字重 + 明度，与代码 / 术语的类别色正交）。内层递归渲染——
                        // 让 **蓝色「术语」** 里的 `code` / 「术语」照常上各自类别色（内层 color 覆盖其区段），
                        // 外层只把普通字提亮到中性白、整体加粗。
                        sb.Append("<b><color=").Append(EmphasisColor).Append('>');
                        sb.Append(Render(raw.Substring(i + 2, e - i - 2)));
                        sb.Append("</color></b>");
                        i = e + 2;
                        plainStart = i;
                        continue;
                    }
                }
                else if (c == '`')
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
