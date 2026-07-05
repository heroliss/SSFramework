using UnityEditor;
using UnityEngine;

namespace Game.Framework.Fonts.Editor
{
    /// <summary>字体模块菜单：常用字集生成 + 配置直达（配置 Profile 约定——菜单可达，不靠翻文件夹）。</summary>
    public static class FontCharsetMenu
    {
        [MenuItem("SSFramework/字体/生成常用字集", priority = 1)]
        public static void GenerateCharset() => FontCharsetGenerator.Generate(FontCharsetProfile.Resolve());

        [MenuItem("SSFramework/字体/常用字集配置 (Charset Profile)", priority = 20)]
        public static void LocateProfile()
        {
            var profile = FontCharsetProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }
    }
}
