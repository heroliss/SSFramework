using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// Demo 顶部 Tab 导航。每个 Tab 对应 PageContainer 下的一个 Page，按下 Tab 显示对应 Page、隐藏其他。
    /// </summary>
    /// <remarks>
    /// 用 Bag 管理订阅生命周期（参考 AGENTS §17）。Tab 按下时还会把 Tab 自身底色染成当前 Page 的主题色，强化"切到哪个章节"的视觉反馈。
    /// </remarks>
    public sealed class DemoPageNavigatorView : MonoViewBase
    {
        [Tooltip("Tab 按钮列表。顺序与 _pages 一一对应。")]
        [SerializeField] private Button[] _tabs;
        [Tooltip("对应的 Page GameObject 列表。顺序与 _tabs 一一对应。")]
        [SerializeField] private GameObject[] _pages;
        [Tooltip("可选：每个 Tab 对应的主题色，与 ConceptCardView 主题色保持一致。Tab 被选中时染色（按下背景 + 文字色）。")]
        [SerializeField] private Color[] _tabThemeColors;

        [Header("视觉")]
        [SerializeField] private Color _activeTextColor = Color.white;
        [SerializeField] private Color _inactiveTextColor = new(0.65f, 0.7f, 0.78f);
        [SerializeField] private Color _inactiveBgColor = new(0.13f, 0.16f, 0.22f);

        protected override void Awake()
        {
            base.Awake();
            int n = Mathf.Min(_tabs?.Length ?? 0, _pages?.Length ?? 0);
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                Bag.Subscribe(_tabs[i].onClick, () => Show(idx));
            }
            Show(0);
        }

        public void Show(int index)
        {
            int n = Mathf.Min(_tabs?.Length ?? 0, _pages?.Length ?? 0);
            for (int i = 0; i < n; i++)
            {
                bool active = i == index;
                if (_pages[i] != null) _pages[i].SetActive(active);
                SetTabStyle(i, active);
            }
        }

        private void SetTabStyle(int i, bool active)
        {
            var btn = _tabs[i];
            if (btn == null) return;

            // 背景色：active 用主题色（饱和度降低一些），inactive 用统一暗色
            if (btn.TryGetComponent<Image>(out var img))
            {
                if (active && _tabThemeColors != null && i < _tabThemeColors.Length)
                {
                    var c = _tabThemeColors[i];
                    img.color = new Color(c.r * 0.55f, c.g * 0.55f, c.b * 0.55f, 1f);
                }
                else
                {
                    img.color = _inactiveBgColor;
                }
            }

            // 标签文字色
            var label = btn.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.color = active ? _activeTextColor : _inactiveTextColor;
        }
    }
}
