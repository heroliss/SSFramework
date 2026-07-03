using UnityEngine;

namespace Game.Framework.UI.UGui
{
    /// <summary>
    /// UGUI 安全区适配（ADR-0020 §3）：把所挂 <see cref="RectTransform"/> 的锚区收进 <see cref="Screen.safeArea"/>，
    /// 避开刘海 / 挖孔 / 圆角。挂在<b>窗口内容根</b>上（其父链应全屏拉伸）——层根 / 背景刻意不做安全区
    /// （背景该出血铺满整屏，只有交互内容需要避让）。
    /// </summary>
    /// <remarks>
    /// 每帧比较 safeArea 是否变化（转屏 / 折叠屏展开时变），变了才重算锚区——比较是纯值判断，无分配。
    /// 本组件会接管锚区（anchorMin/Max 与 offset），不要再手动改所挂节点的锚。
    /// </remarks>
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public sealed class UGuiSafeArea : MonoBehaviour
    {
        private Rect _applied = new(-1, -1, -1, -1); // 保证首次 Apply

        private void OnEnable() => Apply();

        private void Update()
        {
            if (Screen.safeArea != _applied) Apply();
        }

        private void Apply()
        {
            var sa = Screen.safeArea;
            if (Screen.width <= 0 || Screen.height <= 0) return;

            var rt = (RectTransform)transform;
            var min = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
            var max = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            _applied = sa;
        }
    }
}
