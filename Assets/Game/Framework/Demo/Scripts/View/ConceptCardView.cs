using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// Demo 每个 Page 顶部的"概念卡片"。从 <see cref="DemoCopySO"/> 按 Id 取章节文案并填充 UI——
    /// 标题、一句话核心、详细说明，以及若干段示例代码块。
    /// </summary>
    /// <remarks>
    /// <b>使用：</b>在 Inspector 配置 <see cref="DemoCopySO"/> 引用 + <c>ChapterId</c>，Awake 时自动渲染。<br/>
    /// <b>不订阅事件：</b>纯文案展示组件，没有动态数据更新需求，不持有 Bag。<br/>
    /// <b>代码块：</b>动态实例化 <see cref="CodeSnippetView"/> prefab，按 SO 中的列表顺序填充。
    /// </remarks>
    public sealed class ConceptCardView : MonoBehaviour
    {
        [Header("数据源")]
        [SerializeField] private DemoCopySO _copy;
        [SerializeField] private string _chapterId;

        [Header("UI 引用")]
        [Tooltip("章节标题；推荐配 Overflow.Ellipsis。")]
        [SerializeField] private TMP_Text _title;
        [Tooltip("一句话核心要点；正文级文字，允许换行。")]
        [SerializeField] private TMP_Text _oneLiner;
        [Tooltip("详细说明文本（讲用法/接口）；外包 ScrollRect 兜底过长。")]
        [SerializeField] private TMP_Text _body;
        [Tooltip("设计考量文本（讲为什么这样设计）；推荐用不同底色 / 边框与 Body 区分。可空——SO 中该字段为空时整个 Section 隐藏。")]
        [SerializeField] private TMP_Text _designRationale;
        [Tooltip("设计考量区的根 GameObject；为 null 时不参与显隐控制。")]
        [SerializeField] private GameObject _designRationaleSection;
        [Tooltip("章节序号 + ID 标签（如 \"01 · ARCHITECTURE\"），Render 时自动按 chapter 在 SO 中的索引生成；可空。")]
        [SerializeField] private TMP_Text _chapterBadge;

        [Header("代码块")]
        [Tooltip("代码片段容器（VerticalLayoutGroup）。每个 SO 中的 CodeSnippet 会实例化一个 CodeSnippetView 到此容器下。")]
        [SerializeField] private Transform _codeSnippetContainer;
        [Tooltip("CodeSnippetView prefab，必须挂有 CodeSnippetView 组件。")]
        [SerializeField] private CodeSnippetView _codeSnippetPrefab;

        [Header("主题色")]
        [Tooltip("本章主题色，应用到所有 accent 元素（accent bar / badge / 强调标签）。每章一个不同色。")]
        [SerializeField] private Color _themeColor = new Color(0.35f, 0.62f, 1.00f);
        [Tooltip("需要染色为主题色的 Image 列表（PageHeader 的 AccentBar、Section 的 AccentBar、按钮高亮等）。")]
        [SerializeField] private List<Image> _accentImages = new();
        [Tooltip("需要染色为主题色的 TMP 文本列表（ChapterBadge、设计考量 Label、代码块 caption 等）。")]
        [SerializeField] private List<TMP_Text> _accentTexts = new();
        [Tooltip("CodeSnippet prefab 实例化后，按此名字查找子 GameObject 染色 (Image)；为空则不染。")]
        [SerializeField] private string _codeSnippetAccentChildName = "AccentBar";

        private void Awake()
        {
            Render();
        }

        /// <summary>按 Inspector 配置的 SO + chapterId 渲染卡片。运行时改字段也可手动调一次重新渲染。</summary>
        public void Render()
        {
            if (_copy == null)
            {
                Debug.LogError($"[ConceptCardView] '{name}': DemoCopySO 未配置。", this);
                return;
            }

            var chapter = _copy.FindById(_chapterId);
            if (chapter == null)
            {
                Debug.LogError($"[ConceptCardView] '{name}': SO 中找不到 chapter id '{_chapterId}'。", this);
                return;
            }

            if (_title != null) _title.text = chapter.Title;
            if (_oneLiner != null) _oneLiner.text = chapter.OneLiner;
            if (_body != null) _body.text = chapter.Body;

            // 章节序号 badge：从 SO 找 chapter 的索引（0-based），格式化成 "01 · CHAPTER-ID"
            if (_chapterBadge != null)
            {
                int idx = IndexOfChapter(_chapterId);
                _chapterBadge.text = idx >= 0
                    ? string.Format("{0:00}  ·  {1}", idx + 1, _chapterId.ToUpper())
                    : _chapterId.ToUpper();
            }

            // 设计考量区——SO 中字段为空时整个 Section 隐藏（避免空标题占位）
            bool hasRationale = !string.IsNullOrWhiteSpace(chapter.DesignRationale);
            if (_designRationale != null) _designRationale.text = chapter.DesignRationale;
            if (_designRationaleSection != null) _designRationaleSection.SetActive(hasRationale);

            ApplyThemeColor();
            RenderCodeSnippets(chapter);
        }

        private int IndexOfChapter(string id)
        {
            if (_copy == null || string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < _copy.Chapters.Count; i++)
                if (_copy.Chapters[i].Id == id) return i;
            return -1;
        }

        /// <summary>将 <see cref="_themeColor"/> 应用到所有 accent 元素。Render 时调用。</summary>
        private void ApplyThemeColor()
        {
            for (int i = 0; i < _accentImages.Count; i++)
                if (_accentImages[i] != null) _accentImages[i].color = _themeColor;
            for (int i = 0; i < _accentTexts.Count; i++)
                if (_accentTexts[i] != null) _accentTexts[i].color = _themeColor;
        }

        private void RenderCodeSnippets(DemoChapterCopy chapter)
        {
            if (_codeSnippetContainer == null || _codeSnippetPrefab == null) return;

            // 清空已有子物体（支持运行时重渲染 / Editor 编辑预览）
            for (int i = _codeSnippetContainer.childCount - 1; i >= 0; i--)
            {
                var child = _codeSnippetContainer.GetChild(i);
                if (Application.isPlaying) Destroy(child.gameObject);
                else DestroyImmediate(child.gameObject);
            }

            var snippets = chapter.CodeSnippets;
            for (int i = 0; i < snippets.Count; i++)
            {
                var view = Instantiate(_codeSnippetPrefab, _codeSnippetContainer);
                view.Render(snippets[i]);
                // 给实例化的 CodeSnippet 的 AccentBar 染上本章主题色
                if (!string.IsNullOrEmpty(_codeSnippetAccentChildName))
                {
                    var bar = view.transform.Find(_codeSnippetAccentChildName);
                    if (bar != null)
                    {
                        var barImg = bar.GetComponent<Image>();
                        if (barImg != null) barImg.color = _themeColor;
                    }
                }
            }
        }
    }
}
