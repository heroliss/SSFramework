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
        private Canvas _canvas;   // 承载方块的 ScreenSpaceOverlay Canvas，取其 scaleFactor 把屏幕像素落到 Canvas 本地单位（懒缓存）

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

        /// <summary>
        /// 每帧把 UGUI 容器贴到 UI Toolkit 占位框上：读占位框 <c>worldBound</c>，换算后写进 <see cref="_spawnRoot"/> 的 RectTransform。
        /// </summary>
        /// <remarks>
        /// ⚠ 关键换算——两套 UI 的坐标单位不一定同尺度：<br/>
        /// • <c>worldBound</c> 是 UI Toolkit 的<b>面板点</b>坐标；面板用 <c>ConstantPhysicalSize</c> 时
        ///   1 面板点 = (实际 DPI / referenceDpi) 屏幕像素（本机 144/96 = 1.5），并非 1:1。<br/>
        /// • UGUI 是 <c>ScreenSpaceOverlay</c>，RectTransform 用 <b>Canvas 本地单位</b> = 屏幕像素 / <c>Canvas.scaleFactor</c>。<br/>
        /// 若把面板点当像素直接赋值，容器会按错误比例缩放错位，且滚动时位移比例也错（越滚越飘、与 Toolkit 内容不同步）。
        /// 故先按「面板根 <c>worldBound</c> 覆盖满屏」推出 面板点→屏幕像素 的缩放，再除以 <c>scaleFactor</c> 落到 Canvas 本地单位。
        /// </remarks>
        private void AlignToAnchor()
        {
            if (_anchor == null || _spawnRoot == null || _anchor.panel == null) return;
            var b = _anchor.worldBound;
            // 用 >（而非 <=）判定有效：窗口拖到极窄时 worldBound 可能退化成 NaN，而 NaN<=1 恒为 false 会绕过保护，
            // 把 NaN 灌进 RectTransform.sizeDelta 会让 UGUI Canvas 布局反复重算 / 卡死。> 判定下 NaN 一律跳过。
            if (!(b.width > 1f) || !(b.height > 1f)) return;

            // 面板点 → 屏幕像素：面板根 worldBound 铺满整屏，Screen 与它的比即缩放（ConstantPhysicalSize 下 = DPI/referenceDpi）。
            var panelSize = _anchor.panel.visualTree.worldBound.size;
            if (!(panelSize.x > 0f) || !(panelSize.y > 0f)) return;
            if (_canvas == null) _canvas = _spawnRoot.GetComponentInParent<Canvas>();
            float f = _canvas != null ? _canvas.scaleFactor : 1f;   // 屏幕像素 → Canvas 本地单位（ConstantPixelSize 下 scaleFactor=1）
            float sx = Screen.width / panelSize.x / f;
            float sy = Screen.height / panelSize.y / f;

            _spawnRoot.anchorMin = new Vector2(0f, 1f);
            _spawnRoot.anchorMax = new Vector2(0f, 1f);
            _spawnRoot.pivot = new Vector2(0f, 1f);
            _spawnRoot.anchoredPosition = new Vector2(b.xMin * sx, -b.yMin * sy);
            _spawnRoot.sizeDelta = new Vector2(b.width * sx, b.height * sy);
        }
    }
}
