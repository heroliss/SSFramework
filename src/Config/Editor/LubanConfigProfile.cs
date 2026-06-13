using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// 配置表生成配置（编辑器资产）——「Luban CLI 怎么调、产物落到哪」的单一真源。
    ///
    /// 生成管线（<see cref="LubanCodeGenerator"/>）只读本资产，不在代码里散落路径常量；
    /// 换项目 / 换目录结构时改 Inspector 即可，不动代码。
    ///
    /// 路径字段一律相对工程根目录（含 Assets 外的 Configs / Tools），保证多人协作不受本机绝对路径影响。
    /// 资产入库；<see cref="Resolve"/> 找不到时按本仓库默认布局自动创建（落在 <c>Assets/Game/Framework/Config/</c>），
    /// 找到多个时取第一个并警告。
    /// </summary>
    [CreateAssetMenu(fileName = "LubanConfigProfile", menuName = "SSFramework/配置表生成配置 (Luban Profile)")]
    public sealed class LubanConfigProfile : ScriptableObject
    {
        [Header("Luban CLI")]
        [Tooltip("Luban CLI 可执行文件（相对工程根目录）。\n" +
                 "工具不入库（体积大且可重下）：从 https://github.com/focus-creative-games/luban 的 release 解压到该路径。\n" +
                 "需要 .NET 运行时；缺 .NET 8 时管线会带 DOTNET_ROLL_FORWARD=LatestMajor 用更高版本运行。")]
        [SerializeField] private string _lubanToolPath = "Tools/Luban/Luban.exe";

        [Tooltip("luban.conf 路径（相对工程根目录）。表定义（Defines/）与数据（Datas/）的入口都由它声明。")]
        [SerializeField] private string _confPath = "Configs/luban.conf";

        [Header("生成目标")]
        [Tooltip("luban.conf 里 targets 的 name（决定 topModule / 分组）。")]
        [SerializeField] private string _target = "client";

        [Tooltip("代码模板：cs-bin = C# 类 + 二进制反序列化（推荐，紧凑、解析快）。改用 json 数据时换 cs-simple-json。")]
        [SerializeField] private string _codeTarget = "cs-bin";

        [Tooltip("数据格式：bin = 二进制 .bytes（与 cs-bin 配对）。")]
        [SerializeField] private string _dataTarget = "bin";

        [Header("产物输出")]
        [Tooltip("生成 C# 代码的输出目录（相对工程根目录，生成器会整理该目录，勿手放文件）。")]
        [SerializeField] private string _outputCodeDir = "Assets/Game/Framework/Demo/Config/Gen";

        [Tooltip("生成数据文件的输出目录（相对工程根目录）。须在某个 YooAsset 收集器范围内（.bytes 按普通资源收集成 TextAsset 即可，按文件名寻址），数据才打得进资源包。")]
        [SerializeField] private string _outputDataDir = "Assets/Game/Framework/Res/Configs";

        [Tooltip("表清单类（LubanTableManifest.g.cs）的命名空间——须与 luban.conf 该 target 的 topModule 一致，清单才和生成代码同住一个命名空间。\n" +
                 "⚠ topModule 不要嵌在含 System 子命名空间的层级下（如 Game.Framework.*）：生成代码裸写 System.Func/Collections，会被就近解析劫持（CS0234）。")]
        [SerializeField] private string _manifestNamespace = "DemoCfg";

        [Tooltip("附加 CLI 参数（原样追加，如 -x l10n.provider=default）。一般留空。")]
        [SerializeField] private string _extraArgs = "";

        public string LubanToolPath => _lubanToolPath.Trim();
        public string ConfPath => _confPath.Trim();
        public string Target => _target.Trim();
        public string CodeTarget => _codeTarget.Trim();
        public string DataTarget => _dataTarget.Trim();
        public string OutputCodeDir => _outputCodeDir.Trim().TrimEnd('/', '\\');
        public string OutputDataDir => _outputDataDir.Trim().TrimEnd('/', '\\');
        public string ManifestNamespace => _manifestNamespace.Trim();
        public string ExtraArgs => _extraArgs.Trim();

        /// <summary>
        /// 解析全工程唯一的 Luban profile：先找已有资产（找到多个 → 取第一个并警告），
        /// 没有就按本仓库默认布局自动建一个（落在 <c>Assets/Game/Framework/Config/</c>）。
        /// </summary>
        public static LubanConfigProfile Resolve()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(LubanConfigProfile));
            if (guids.Length > 0)
            {
                if (guids.Length > 1)
                {
                    var paths = guids.Select(AssetDatabase.GUIDToAssetPath);
                    Debug.LogWarning("[配置表构建] 找到多个 Luban profile，仅第一个生效，请删到只剩一个：\n  " +
                                     string.Join("\n  ", paths));
                }
                return AssetDatabase.LoadAssetAtPath<LubanConfigProfile>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            var profile = CreateInstance<LubanConfigProfile>();
            const string path = "Assets/Game/Framework/Config/LubanConfigProfile.asset";
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[配置表构建] 未找到 Luban profile，已按默认布局自动创建：{path}");
            return profile;
        }
    }
}
