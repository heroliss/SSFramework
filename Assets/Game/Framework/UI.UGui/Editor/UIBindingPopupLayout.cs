using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// UI Binding 临时弹窗的工作区高度预算。PopupWindowContent 由 Unity 托管、不能像普通窗口一样可靠地拖边缩放，
    /// 因此这里按弹窗所在显示器的桌面边界留出系统栏和定位余量，超出的内容交给各弹窗自己的 ScrollView。
    /// </summary>
    internal static class UIBindingPopupLayout
    {
        private const float DesktopSafetyMargin = 80f;
        private const float MaximumWindowHeight = 760f;

        internal static float ResolveMaxWindowHeight(EditorWindow window)
        {
            float desktopHeight = Screen.currentResolution.height;
            if (window != null)
            {
                Rect desktop = InternalEditorUtility.GetBoundsOfDesktopAtPoint(window.position.center);
                if (desktop.height > 0f) desktopHeight = desktop.height;
            }

            return CalculateMaxWindowHeight(desktopHeight);
        }

        internal static float CalculateMaxWindowHeight(float desktopHeight)
        {
            if (float.IsNaN(desktopHeight) || float.IsInfinity(desktopHeight) || desktopHeight <= 0f)
                desktopHeight = 720f;

            // 可见性优先于“舒服的最小高度”：分屏、远程桌面或测试宿主可能给出小于 320px 的工作区。
            // 若仍强制 240px，弹窗会反而越过工作区边界，底部操作即使用了 ScrollView 也无法触达。
            return Mathf.Clamp(desktopHeight - DesktopSafetyMargin, 0f, MaximumWindowHeight);
        }

        internal static float ClampRequestedHeight(float requestedHeight, float maxWindowHeight)
            => Mathf.Min(Mathf.Max(0f, requestedHeight), Mathf.Max(0f, maxWindowHeight));

        internal static float CalculateBodyViewportHeight(
            float desiredBodyHeight,
            float maxWindowHeight,
            float fixedHeaderHeight)
        {
            float bodyBudget = Mathf.Max(0f, maxWindowHeight - Mathf.Max(0f, fixedHeaderHeight));
            return ClampRequestedHeight(desiredBodyHeight, bodyBudget);
        }
    }
}
