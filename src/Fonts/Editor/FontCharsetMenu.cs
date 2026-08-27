using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Fonts.Editor
{
    /// <summary>字体字集工作台的动作层：生成字符集合并显式定位配置。</summary>
    public static class FontCharsetMenu
    {
        internal static void GenerateCharset()
        {
            if (!FontCharsetProfile.TryResolve(out var profile))
            {
                FrameworkEditorFeedback.Warn(
                    "字体字集生成未启动",
                    "影响：没有创建配置，也没有写入字集文件。\n原因：工程里还没有 Font Charset Profile。\n" +
                    $"下一步：打开“{FrameworkMenuPaths.FontCharset}”，点击“创建默认字集配置”并复核后重试。");
                return;
            }
            if (!FrameworkEditorOperationGate.EnsureCanStart("字体字集生成")) return;
            var (ok, message, _) = FontCharsetGenerator.TryGenerate(profile);
            FrameworkEditorFeedback.ReportResult(
                "字体字集生成",
                ok,
                message + (ok
                    ? "\n下一步：在 TMP Font Asset Creator 选择 Characters from File 烘焙静态图集。"
                    : "\n影响：没有得到可用的新字集；请修正配置后重试。"),
                profile);
        }

        internal static void LocateProfile()
        {
            if (!FontCharsetProfile.TryResolve(out _) &&
                !FrameworkEditorOperationGate.EnsureCanStart("创建字体字集配置")) return;
            var profile = FontCharsetProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }
    }
}
