using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// 配置表生成配置（编辑器资产）——<b>一套</b>配置表「Luban CLI 怎么调、产物落到哪」的单一真源。
    ///
    /// 生成管线（<see cref="LubanCodeGenerator"/>）只读本资产，不在代码里散落路径常量；
    /// 换项目 / 换目录结构时改 Inspector 即可，不动代码。
    ///
    /// 路径字段一律相对工程根目录，保证多人协作不受本机绝对路径影响。
    /// <b>工程可并存多套</b>（如 demo 一套 + 正式游戏一套）：每套指向各自的 luban.conf 源与输出目录、互不干扰；
    /// <see cref="ResolveAll"/> 返回全部、生成菜单逐套生成。一套都没有时 <see cref="Resolve"/> 按默认布局自动建一套。
    /// </summary>
    [CreateAssetMenu(fileName = "LubanConfigProfile", menuName = "SSFramework/配置表生成配置 (Luban Profile)")]
    public sealed class LubanConfigProfile : ScriptableObject
    {
        [Header("Luban CLI")]
        [Tooltip("Luban CLI 可执行文件（相对工程根目录）。\n" +
                 "工具不入库（体积大且可重下）：从 https://github.com/focus-creative-games/luban 的 release 解压到该路径。\n" +
                 "需要 .NET 运行时；缺 .NET 8 时管线会带 DOTNET_ROLL_FORWARD=LatestMajor 用更高版本运行。")]
        [SerializeField] private string _lubanToolPath = "Tools/Luban/Luban.exe";

        [Tooltip("luban.conf 路径（相对工程根目录）。表定义（Defines/）与数据（Datas/）的入口都由它声明。\n" +
                 "可放任意位置；随模块删除 / 抽包就放该模块目录下并用 ~ 后缀避免 Unity 导入（demo 那套在 Demo/Configs~/）。")]
        [SerializeField] private string _confPath = "Assets/Game/Framework/Demo/Configs~/luban.conf";

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
        [SerializeField] private string _outputDataDir = "Assets/Game/Framework/Demo/Res/Configs";

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
        /// 返回工程内**所有** Luban profile（按资产路径排序，显示稳定）。每套对应一套配置表，生成菜单逐套生成、互不干扰。
        /// 一套都没有时按默认布局自动建一套并返回（首次接入即可直接用）。
        /// </summary>
        public static IReadOnlyList<LubanConfigProfile> ResolveAll()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(LubanConfigProfile));
            if (guids.Length == 0)
                return new[] { CreateDefault() };

            return guids.Select(g => AssetDatabase.LoadAssetAtPath<LubanConfigProfile>(AssetDatabase.GUIDToAssetPath(g)))
                        .Where(p => p != null)
                        .OrderBy(AssetDatabase.GetAssetPath, System.StringComparer.Ordinal)
                        .ToList();
        }

        /// <summary>
        /// 第一套 profile（按路径序）——只需任意 / 主配置时的便利访问。
        /// 要按各套操作（定位 / 打开目录 / 单独生成）用 <see cref="ResolveAll"/> 或「配置总览」窗口（<see cref="LubanConfigOverviewWindow"/>）。
        /// </summary>
        public static LubanConfigProfile Resolve() => ResolveAll()[0];

        // 工程内一套 profile 都没有时按本仓库默认布局建一套（落在 Config/，默认指向 demo 那套源 / 输出）。
        private static LubanConfigProfile CreateDefault()
        {
            var profile = CreateInstance<LubanConfigProfile>();
            const string path = "Assets/Game/Framework/Config/LubanConfigProfile.asset";
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[配置表构建] 未找到 Luban profile，已按默认布局自动创建：{path}");
            return profile;
        }
    }
}
