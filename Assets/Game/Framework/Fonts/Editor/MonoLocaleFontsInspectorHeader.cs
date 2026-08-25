using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Fonts.Editor
{
    /// <summary>
    /// 把 Fonts 专属诊断单向注册到通用 Inspector contributor 接缝；原生 fallback、可选 Odin Adapter 与
    /// 默认 Header 路径都复用同一绘制器，通用 Editor 无需反向引用 Fonts。
    /// </summary>
    [InitializeOnLoad]
    internal static class MonoLocaleFontsInspectorHeader
    {
        static MonoLocaleFontsInspectorHeader()
        {
            FrameworkInspectorDiagnostics.Register<MonoLocaleFonts>(Draw);
        }

        private static void Draw(MonoLocaleFonts fonts)
        {
            if (!Application.isPlaying || fonts == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("字体 fallback 诊断", EditorStyles.boldLabel);
                var lines = fonts.EditorDiagnostics;
                if (lines.Count == 0)
                {
                    EditorGUILayout.LabelField("（未配置主字体）", EditorStyles.wordWrappedMiniLabel);
                    return;
                }
                foreach (string line in lines)
                    EditorGUILayout.LabelField("• " + line, EditorStyles.wordWrappedMiniLabel);
            }
        }
    }
}
