using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// Demo 页面内部的左侧子导航。
    /// 每个顶部 Tab 都可以拥有若干教学子页，本组件只负责按钮切换和高亮，
    /// 具体示例逻辑仍由各 Page_xxxView 负责。
    /// </summary>
    public sealed class DemoSubPageNavigatorView : MonoViewBase
    {
        [SerializeField] private Button[] _buttons;
        [SerializeField] private GameObject[] _pages;

        [Header("Colors")]
        [SerializeField] private Color _activeButtonColor = new(0.22f, 0.42f, 0.95f);
        [SerializeField] private Color _inactiveButtonColor = new(0.12f, 0.14f, 0.19f);
        [SerializeField] private Color _activeTextColor = Color.white;
        [SerializeField] private Color _inactiveTextColor = new(0.78f, 0.82f, 0.88f);

        protected override void Awake()
        {
            base.Awake();
            int count = Mathf.Min(_buttons?.Length ?? 0, _pages?.Length ?? 0);
            for (int i = 0; i < count; i++)
            {
                int index = i;
                // 用 Bag 管理订阅生命周期，OnDestroy 自动反订阅，符合框架统一规范（AGENTS §17）。
                Bag.Subscribe(_buttons[i].onClick, () => Show(index));
            }

            Show(0);
        }

        public void Show(int index)
        {
            int count = Mathf.Min(_buttons?.Length ?? 0, _pages?.Length ?? 0);
            for (int i = 0; i < count; i++)
            {
                bool active = i == index;
                if (_pages[i] != null)
                    _pages[i].SetActive(active);

                SetButtonStyle(_buttons[i], active);
            }
        }

        private void SetButtonStyle(Button button, bool active)
        {
            if (button == null) return;

            if (button.TryGetComponent<Image>(out var image))
                image.color = active ? _activeButtonColor : _inactiveButtonColor;

            var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
                label.color = active ? _activeTextColor : _inactiveTextColor;
        }
    }
}
