using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 服务安装器生成的交互外壳：菜单扫全工程的 <see cref="ServiceInstallerProfile"/> 逐个生成；
    /// profile 资产 Inspector 上另有单独的生成按钮（见 <see cref="ServiceInstallerProfileEditor"/>）。
    /// 生成逻辑全在 <see cref="ServiceInstallerGenerator"/>，这里只做定位与结果展示。
    /// </summary>
    public static class ServiceInstallerMenu
    {
        [MenuItem("SSFramework/服务注册/生成服务安装器代码")]
        private static void GenerateAll()
        {
            var profiles = AssetDatabase.FindAssets("t:" + nameof(ServiceInstallerProfile))
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ServiceInstallerProfile>)
                .Where(p => p != null)
                .ToList();
            if (profiles.Count == 0)
            {
                EditorUtility.DisplayDialog("没有找到配置",
                    "工程里没有 ServiceInstallerProfile 资产。\n\n" +
                    "经 Assets/Create/SSFramework/服务安装器配置 创建一个，配置「扫描目录 → 输出路径/命名空间」后再生成。",
                    "好");
                return;
            }

            bool allOk = true;
            var sb = new StringBuilder();
            foreach (var profile in profiles)
            {
                var (ok, message) = ServiceInstallerGenerator.Generate(profile);
                allOk &= ok;
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(message);
            }

            string summary = sb.ToString();
            Debug.Log("[服务安装器] 生成结果：\n" + summary);
            EditorUtility.DisplayDialog(allOk ? "生成完成" : "生成有失败项", summary, "好");
        }
    }

    /// <summary>profile 的 Inspector：默认字段之外补一个就地生成按钮（与构建 profile 的交互习惯一致）。</summary>
    [CustomEditor(typeof(ServiceInstallerProfile))]
    public sealed class ServiceInstallerProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            GUILayout.Space(8);
            if (GUILayout.Button("生成服务安装器代码", GUILayout.Height(28)))
            {
                var (ok, message) = ServiceInstallerGenerator.Generate((ServiceInstallerProfile)target);
                Debug.Log("[服务安装器] 生成结果：\n" + message);
                EditorUtility.DisplayDialog(ok ? "生成完成" : "生成有失败项", message, "好");
            }
        }
    }
}
