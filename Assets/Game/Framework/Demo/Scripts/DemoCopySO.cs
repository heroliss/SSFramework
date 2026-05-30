using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Framework.Demo
{
    /// <summary>
    /// Demo 文案数据 ScriptableObject。承载所有 Page 顶部"概念卡片"的标题、一句话核心、详细说明、示例代码。
    /// </summary>
    /// <remarks>
    /// <b>为什么抽到 SO：</b>原版 Demo 把所有文案硬编码在 C# 字符串里，每改一处文案都要修代码 + 重新编译；
    /// 抽到 SO 后文案与代码解耦，Inspector 可视化编辑，便于后期 i18n 与非程序员协作。<br/>
    /// <b>怎么用：</b>每个 Page_xxxView 持有一个 <see cref="ConceptCardView"/> 引用，调
    /// <c>card.Render(copy, "chapter-id")</c>，组件按 Id 在 SO 中找到对应章节并填充 UI。<br/>
    /// <b>章节 Id 约定：</b>稳定 kebab-case 字符串（"philosophy"/"minimal-counter"/...），代码侧用常量集中管理，
    /// 避免散落字面量。
    /// </remarks>
    [CreateAssetMenu(menuName = "SSFramework/Demo/DemoCopy", fileName = "DemoCopy")]
    public sealed class DemoCopySO : ScriptableObject
    {
        [SerializeField] private List<DemoChapterCopy> _chapters = new();

        /// <summary>章节文案条目（只读视图，给 ConceptCardView 用）。</summary>
        public IReadOnlyList<DemoChapterCopy> Chapters => _chapters;

        /// <summary>按 Id 查找章节文案；未找到时返回 null（调用方应在 Editor 下 Debug.LogError 提示配置缺失）。</summary>
        public DemoChapterCopy FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < _chapters.Count; i++)
                if (_chapters[i].Id == id) return _chapters[i];
            return null;
        }
    }

    /// <summary>
    /// 单章节文案。一个章节对应一张概念卡片，包含 [标题 + 一句话核心 + 详细说明 + 0~N 段示例代码]。
    /// </summary>
    [Serializable]
    public sealed class DemoChapterCopy
    {
        [Tooltip("章节稳定标识符，kebab-case 字符串（如 \"philosophy\" / \"minimal-counter\"）。代码侧按此 Id 查找。")]
        [SerializeField] private string _id;

        [Tooltip("章节标题，单行；UI 上配 Overflow.Ellipsis。")]
        [SerializeField] private string _title;

        [Tooltip("一句话核心要点，建议 ≤ 50 字。让用户一眼抓住本章重点。")]
        [TextArea(1, 3)]
        [SerializeField] private string _oneLiner;

        [Tooltip("详细说明文本（支持多行）。讲本章的功能/用法/接口。外包 ScrollRect 兜底文本过长。")]
        [TextArea(3, 15)]
        [SerializeField] private string _body;

        [Tooltip("设计考量：为什么这样设计、有什么权衡（精简说明，可选）。UI 上独立渲染，与 Body 视觉区分。")]
        [TextArea(2, 10)]
        [SerializeField] private string _designRationale;

        [Tooltip("示例代码片段列表，每条独立卡片，按顺序展示。")]
        [SerializeField] private List<DemoCodeSnippet> _codeSnippets = new();

        public string Id => _id;
        public string Title => _title;
        public string OneLiner => _oneLiner;
        public string Body => _body;
        public string DesignRationale => _designRationale;
        public IReadOnlyList<DemoCodeSnippet> CodeSnippets => _codeSnippets;
    }

    /// <summary>
    /// 示例代码片段。caption 是这段代码"在演示什么"，code 是实际代码内容。
    /// </summary>
    /// <remarks>
    /// <b>渲染约定：</b>UI 上代码块要关闭 <c>TextMeshProUGUI.richText</c>，避免 <c>&lt;T&gt;</c> 这种泛型尖括号
    /// 被 TMP 当作富文本标签吃掉（参见 D6 设计要点）。
    /// </remarks>
    [Serializable]
    public sealed class DemoCodeSnippet
    {
        [Tooltip("这段代码演示什么，作为代码块上方的小标题。")]
        [SerializeField] private string _caption;

        [Tooltip("代码内容（建议等宽字体渲染）。允许包含泛型尖括号 <T>，UI 渲染时关闭 RichText。")]
        [TextArea(3, 30)]
        [SerializeField] private string _code;

        public string Caption => _caption;
        public string Code => _code;
    }
}
