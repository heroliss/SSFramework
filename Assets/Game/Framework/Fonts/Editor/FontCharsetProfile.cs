using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Fonts.Editor
{
    /// <summary>
    /// 常用字集生成配置（ADR-0025 §5）：声明「扫哪些目录、认哪些文件、额外补哪些字、输出到哪」。
    /// 工作台「SSFramework/代码生成/字体字集」按本配置扫描去重出 charset 文件，
    /// 喂 TMP Font Asset Creator（Characters from File）烘焙①主字体的 static atlas。
    /// </summary>
    /// <remarks>
    /// 全工程单例（只在工作台明确点击创建；默认落到 <c>Assets/Settings/SSFramework/</c>，项目配置不进框架包——ADR-0011）；
    /// 误建多份时 <see cref="Resolve"/> 取按路径排序的第一份并警告。样例与业务文本可以合并扫描——
    /// charset 是并集、多几个字只多几个字形，比维护两份配置省心。
    /// </remarks>
    [CreateAssetMenu(fileName = "FontCharsetProfile", menuName = "SSFramework/字体常用字集配置 (Charset Profile)")]
    public sealed class FontCharsetProfile : ScriptableObject
    {
        private static FontCharsetProfile _cached;
        private static bool _cacheInitialized;

        static FontCharsetProfile()
        {
            EditorApplication.projectChanged += () =>
            {
                _cached = null;
                _cacheInitialized = false;
            };
        }

        [Tooltip("要扫描的目录（工程相对路径）。支持 Unity 不导入的 ~ 目录（如 Luban 源表目录 Configs~）。")]
        [InspectorName("扫描目录")]
        [SerializeField] private string[] _scanDirs = { "Assets" };

        [Tooltip("按扩展名决定提取方式：\n• *.cs —— 只取字符串字面量（注释 / 标识符不进字集）\n• *.xlsx —— 读 sharedStrings（Excel 全部文本单元格）\n• 其余（*.json / *.txt 等）—— 全文")]
        [InspectorName("文件匹配模式")]
        [SerializeField] private string[] _filePatterns = { "*.json", "*.txt", "*.cs", "*.xlsx" };

        [Tooltip("是否包含 ASCII 可打印区（0x20~0x7E：空格、数字、字母、标点）。\n主字体一般都要，除非 Latin 部分单独烘焙。")]
        [InspectorName("包含 ASCII 可打印字符")]
        [SerializeField] private bool _includeAsciiPrintable = true;

        [Tooltip("额外必收字符（直接写在这里，如 …—×÷℃①②）——扫描没覆盖到但确定会显示的字。")]
        [InspectorName("额外必收字符")]
        [SerializeField] private string _extraChars = "";

        [Tooltip("charset 输出路径（UTF-8 文本，字符按码点升序）。生成后在 TMP Font Asset Creator 用 Characters from File 引用。")]
        [InspectorName("字集输出路径")]
        [SerializeField] private string _outputPath = "Assets/Generated/SSFramework/Fonts/CommonCharset.txt";

        public string[] ScanDirs => _scanDirs;
        public string[] FilePatterns => _filePatterns;
        public bool IncludeAsciiPrintable => _includeAsciiPrintable;
        public string ExtraChars => _extraChars;
        public string OutputPath => _outputPath;

        /// <summary>无副作用查找全工程唯一配置；不存在时返回 <c>false</c>。</summary>
        public static bool TryResolve(out FontCharsetProfile profile)
        {
            if (_cacheInitialized)
            {
                profile = _cached;
                return profile != null;
            }

            var paths = AssetDatabase.FindAssets("t:" + nameof(FontCharsetProfile))
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, System.StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0)
            {
                _cacheInitialized = true;
                _cached = null;
                profile = null;
                return false;
            }
            if (paths.Length > 1)
                Debug.LogWarning("[FontCharset] 找到多个常用字集 profile，仅第一个生效，请删到只剩一个：\n  " +
                                 string.Join("\n  ", paths));
            _cached = AssetDatabase.LoadAssetAtPath<FontCharsetProfile>(paths[0]);
            _cacheInitialized = true;
            profile = _cached;
            return profile != null;
        }

        /// <summary>定位全工程唯一配置；不存在则创建默认资产。此写入 API 只供工作台的明确“创建配置”动作调用。</summary>
        public static FontCharsetProfile Resolve()
        {
            if (TryResolve(out FontCharsetProfile existing)) return existing;

            var profile = CreateInstance<FontCharsetProfile>();
            const string root = "Assets/Settings";
            const string dir = root + "/SSFramework";
            EnsureChildFolder("Assets", "Settings", root);
            EnsureChildFolder(root, "SSFramework", dir);
            string path = dir + "/FontCharsetProfile.asset";
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            _cached = profile;
            _cacheInitialized = true;
            Debug.Log($"[FontCharset] 已按用户请求创建常用字集 profile：{path}");
            return profile;
        }

        private static void EnsureChildFolder(string parent, string name, string expectedPath)
        {
            if (AssetDatabase.IsValidFolder(expectedPath)) return;
            if (AssetDatabase.LoadMainAssetAtPath(expectedPath) != null)
                throw new System.InvalidOperationException($"无法创建字体项目配置目录：{expectedPath} 已被同名文件占用。");
            string guid = AssetDatabase.CreateFolder(parent, name);
            if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(expectedPath))
                throw new System.InvalidOperationException($"无法创建字体项目配置目录：{expectedPath}。请检查 Assets 写权限与同名资产。");
        }
    }
}
