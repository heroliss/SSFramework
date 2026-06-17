using Sirenix.OdinInspector.Editor;
using UnityEditor;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// <see cref="UICodeGenProfile"/> 交给 Odin 绘制——四个根默认值的标签 / 目录选择器全由字段特性决定，
    /// 与 <see cref="UICodeGenDirConfig"/> 同一套呈现（减少两套配置间的心智切换）。此处仅声明由 OdinEditor 接管。
    /// </summary>
    [CustomEditor(typeof(UICodeGenProfile))]
    public sealed class UICodeGenProfileEditor : OdinEditor { }
}
