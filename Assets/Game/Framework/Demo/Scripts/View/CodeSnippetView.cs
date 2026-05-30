using TMPro;
using UnityEngine;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// 单段示例代码视图。由 <see cref="ConceptCardView"/> 实例化，渲染一段 <see cref="DemoCodeSnippet"/>：
    /// caption（这段在演示什么）+ code（实际代码内容）。
    /// </summary>
    /// <remarks>
    /// <b>代码块渲染约定：</b>
    /// <list type="bullet">
    ///   <item><b>关闭 RichText</b>——避免 <c>&lt;T&gt;</c> 之类的泛型尖括号被 TMP 当作富文本标签吃掉。Awake 时强制设置。</item>
    ///   <item>建议在 prefab 上配等宽字体（如 JetBrains Mono / Cascadia Code），代码可读性强。</item>
    ///   <item>外包 ScrollRect（横向 + 纵向），长代码不溢出卡片。</item>
    /// </list>
    /// <b>跳转按钮：</b>本组件不直接管理 <see cref="CodeLinkButton"/>——后者在 prefab 上静态配置（不同 page 用不同 prefab 即可），
    /// 避免运行时动态实例化跳转按钮的复杂度。
    /// </remarks>
    public sealed class CodeSnippetView : MonoBehaviour
    {
        [Tooltip("代码块上方小标题，说明这段代码演示什么。")]
        [SerializeField] private TMP_Text _caption;

        [Tooltip("代码内容。Awake 时强制关闭 RichText 以保留 <T> 等泛型尖括号。")]
        [SerializeField] private TMP_Text _code;

        private void Awake()
        {
            if (_code != null) _code.richText = false;
        }

        /// <summary>用 SO 数据填充本组件。<paramref name="snippet"/> 为 null 时清空文本。</summary>
        public void Render(DemoCodeSnippet snippet)
        {
            if (_caption != null) _caption.text = snippet?.Caption ?? string.Empty;
            if (_code != null) _code.text = snippet?.Code ?? string.Empty;
        }
    }
}
