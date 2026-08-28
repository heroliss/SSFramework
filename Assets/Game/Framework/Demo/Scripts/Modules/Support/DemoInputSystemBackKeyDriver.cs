using Game.Framework.Logging;
using Game.Framework.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// Demo 的输入接线样板：把新 Input System 的 Esc / Android Back 映射为 UI 框架的返回导航语义。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 物理输入属于项目 composition layer，而不是 UI Core：正式项目可以在 Input Action、平台输入或业务路由中
    /// 调用 <see cref="IUIUtility.Back"/>，无需让渲染中立的窗口模块依赖某个输入 Package。
    /// </para>
    /// <para>
    /// 本样板与 UI 入口（<c>MonoUGuiUI</c> / <c>MonoToolkitUI</c>）挂在同一 GameObject；
    /// <see cref="IUIUtility.Back"/> 返回 false 时不擅自退出应用，“再按一次退出”等策略仍由项目决定。
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SSFramework 演示/UI/Input System 返回键驱动")]
    public sealed class DemoInputSystemBackKeyDriver : MonoBehaviour
    {
        private IUIUtility _ui;

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;

            if (_ui == null)
            {
                _ui = GetComponent<IUIUtility>();
                if (_ui == null)
                {
                    Log.Error(
                        "同节点上没有 UI 入口（MonoUGuiUI / MonoToolkitUI）；返回键接线样板已自动停用。",
                        category: nameof(DemoInputSystemBackKeyDriver), context: this);
                    enabled = false;
                    return;
                }
            }

            _ui.Back();
        }
    }
}
