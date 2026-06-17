using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 对象池章的场景资产持有者：提供 GameObject/prefab 池演示用的小方块 prefab 与显示容器。
    /// 挂在 demo 根节点下；PoolDemoModule 由反射创建，靠 FindFirstObjectByType 找到这里取引用。
    /// </summary>
    public sealed class DemoPoolAssets : MonoBehaviour
    {
        [SerializeField] private GameObject _chipPrefab;
        [SerializeField] private RectTransform _spawnRoot;

        private VisualElement _anchor;

        public GameObject ChipPrefab => _chipPrefab;
        public RectTransform SpawnRoot => _spawnRoot;

        /// <summary>
        /// 把 UGUI 容器对齐到 UI Toolkit 里的占位元素。UGUI 不能成为 VisualElement 子节点，
        /// 但可用 worldBound 做屏幕坐标同步，视觉上像嵌在 UI Toolkit 布局里。
        /// </summary>
        public void BindAnchor(VisualElement anchor)
        {
            _anchor = anchor;
            AlignToAnchor();
        }

        public void ClearAnchor() => _anchor = null;

        private void LateUpdate() => AlignToAnchor();

        private void AlignToAnchor()
        {
            if (_anchor == null || _spawnRoot == null || _anchor.panel == null) return;
            var b = _anchor.worldBound;
            // 用 >（而非 <=）判定有效：窗口拖到极窄时 worldBound 可能退化成 NaN，而 NaN<=1 恒为 false 会绕过保护，
            // 把 NaN 灌进 RectTransform.sizeDelta 会让 UGUI Canvas 布局反复重算 / 卡死。> 判定下 NaN 一律跳过。
            if (!(b.width > 1f) || !(b.height > 1f)) return;

            _spawnRoot.anchorMin = new Vector2(0f, 1f);
            _spawnRoot.anchorMax = new Vector2(0f, 1f);
            _spawnRoot.pivot = new Vector2(0f, 1f);
            _spawnRoot.anchoredPosition = new Vector2(b.xMin, -b.yMin);
            _spawnRoot.sizeDelta = new Vector2(b.width, b.height);
        }
    }
}
