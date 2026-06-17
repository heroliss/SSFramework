using Sirenix.OdinInspector.Editor;
using UnityEditor;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// <see cref="UICodeGenDirConfig"/> 交给 Odin 绘制——布局全由字段上的 [LabelText]/[FolderPath]/[ShowInInspector] 等特性决定，
    /// 此处仅声明由 OdinEditor 接管（不再手写 IMGUI）。「父配置」引用、各目录选择器、生效预览都来自那些特性。
    /// </summary>
    [CustomEditor(typeof(UICodeGenDirConfig))]
    public sealed class UICodeGenDirConfigEditor : OdinEditor { }
}
