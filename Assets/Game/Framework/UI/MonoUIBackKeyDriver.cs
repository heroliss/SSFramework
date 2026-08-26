using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.UI
{
    /// <summary>
    /// 返回键接线组件（ADR-0020）：每帧检测 Esc（Android 硬件返回键在 Unity 即 Escape），
    /// 按下时调同节点 UI 入口的 <see cref="IUIUtility.Back"/>。挂在 UI 入口（<c>MonoUGuiUI</c> / <c>MonoToolkitUI</c>）
    /// 同一 GameObject 上即启用——要不要接返回键是项目决策，所以做成独立组件而非内置进入口。
    /// </summary>
    /// <remarks>
    /// 输入兼容：启用新 Input System 时走 <c>Keyboard.current</c>，否则退回旧 <c>Input.GetKeyDown</c>——
    /// 由 <c>ENABLE_INPUT_SYSTEM</c> 编译开关自动选择。<c>Back()</c> 返回 false（三层皆空）时本组件不做额外动作，
    /// 「再按一次退出」之类的兜底属于业务，自行订阅或另写组件处理。
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MonoUIBackKeyDriver : MonoBehaviour
    {
        private IUIUtility _ui;

        private void Update()
        {
            if (!EscapePressedThisFrame()) return;

            if (_ui == null)
            {
                _ui = GetComponent<IUIUtility>();
                if (_ui == null)
                {
                    Log.Error("同节点上没有 UI 入口（MonoUGuiUI / MonoToolkitUI）——本组件应与 UI 入口挂在同一 GameObject 上。",
                        category: nameof(MonoUIBackKeyDriver), context: this);
                    enabled = false;
                    return;
                }
            }
            _ui.Back();
        }

        private static bool EscapePressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            return keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Escape);
#endif
        }
    }
}
