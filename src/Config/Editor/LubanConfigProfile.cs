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
    /// 路径字段一律相对工程根目录，保证多人协作不受本机绝对路径影响；代码 / 数据输出还必须位于
    /// <c>Assets</c> 的非根子目录，生成入口会在创建目录或启动 CLI 前完成边界校验。
    /// <b>工程可并存多套</b>（例如按数据域或构建目标拆分）：每项代码 / 数据输出必须独占一个与其它项不嵌套的目录；
    /// <see cref="ResolveAll"/> 返回全部，生成入口先统一验证所有权再逐套生成。路径无法从框架推导，因此缺失时不自动制造配置。
    /// </summary>
    [CreateAssetMenu(fileName = "LubanConfigProfile", menuName = "SSFramework/配置表生成配置 (Luban Profile)")]
    public sealed class LubanConfigProfile : ScriptableObject
    {
        [Header("Luban 命令行工具（CLI）")]
        [Tooltip("Luban CLI 可执行文件（相对工程根目录）。\n" +
                 "工具不入库（体积大且可重下）：从 https://github.com/focus-creative-games/luban 的 release 解压到该路径。\n" +
                 "需要 .NET 运行时；缺 .NET 8 时管线会带 DOTNET_ROLL_FORWARD=LatestMajor 用更高版本运行。")]
        [InspectorName("Luban CLI 可执行文件")]
        [SerializeField] private string _lubanToolPath = "Tools/Luban/Luban.exe";

        [Tooltip("luban.conf 路径（相对工程根目录）。表定义（Defines/）与数据（Datas/）的入口都由它声明。\n" +
                 "可放任意位置；随模块删除 / 抽包就放该模块目录下并用 ~ 后缀避免 Unity 导入。")]
        [InspectorName("luban.conf 路径")]
        [SerializeField] private string _confPath = "";

        [Header("生成目标")]
        [Tooltip("luban.conf 里 targets 的 name（决定 topModule / 分组）。")]
        [InspectorName("目标名称（Target）")]
        [SerializeField] private string _target = "client";

        [Tooltip("代码模板：cs-bin = C# 类 + 二进制反序列化（推荐，紧凑、解析快）。改用 json 数据时换 cs-simple-json。")]
        [InspectorName("代码模板")]
        [SerializeField] private string _codeTarget = "cs-bin";

        [Tooltip("数据格式：bin = 二进制 .bytes（与 cs-bin 配对）。")]
        [InspectorName("数据格式")]
        [SerializeField] private string _dataTarget = "bin";

        [Header("产物输出")]
        [Tooltip("生成 C# 代码的输出目录（必须是 Assets 的非根子目录，生成器会整理该目录，勿手放文件）。")]
        [InspectorName("代码输出目录")]
        [SerializeField] private string _outputCodeDir = "";

        [Tooltip("生成数据文件的输出目录（必须是 Assets 的非根子目录）。须在某个 YooAsset 收集器范围内（.bytes 按普通资源收集成 TextAsset 即可，按文件名寻址），数据才打得进资源包。")]
        [InspectorName("数据输出目录")]
        [SerializeField] private string _outputDataDir = "";

        [Tooltip("表清单类（LubanTableManifest.g.cs）的命名空间——通常与 luban.conf 该 target 的 topModule 一致；topModule 为空时可留空，生成到全局命名空间。\n" +
                 "⚠ topModule 不要嵌在含 System 子命名空间的层级下（如 Game.Framework.*）：生成代码裸写 System.Func/Collections，会被就近解析劫持（CS0234）。")]
        [InspectorName("表清单命名空间")]
        [SerializeField] private string _manifestNamespace = "";

        [Tooltip("附加 CLI 参数（原样追加，如 -x l10n.provider=default）。一般留空。")]
        [InspectorName("附加 CLI 参数")]
        [SerializeField] private string _extraArgs = "";

        public string LubanToolPath => _lubanToolPath?.Trim() ?? "";
        public string ConfPath => _confPath?.Trim() ?? "";
        public string Target => _target?.Trim() ?? "";
        public string CodeTarget => _codeTarget?.Trim() ?? "";
        public string DataTarget => _dataTarget?.Trim() ?? "";
        public string OutputCodeDir => _outputCodeDir?.Trim().TrimEnd('/', '\\') ?? "";
        public string OutputDataDir => _outputDataDir?.Trim().TrimEnd('/', '\\') ?? "";
        public string ManifestNamespace => _manifestNamespace?.Trim() ?? "";
        public string ExtraArgs => _extraArgs?.Trim() ?? "";

        /// <summary>
        /// 返回工程内**所有** Luban profile（按资产路径排序，显示稳定）。生成入口会先验证每项输出目录独占，再逐套生成。
        /// 一套都没有时返回空列表；用 Assets/Create 或配置总览的“新建配置”显式创建。
        /// </summary>
        public static IReadOnlyList<LubanConfigProfile> ResolveAll()
        {
            return AssetDatabase.FindAssets("t:" + nameof(LubanConfigProfile))
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .OrderBy(path => path, System.StringComparer.Ordinal)
                        .Select(AssetDatabase.LoadAssetAtPath<LubanConfigProfile>)
                        .Where(p => p != null)
                        .ToList();
        }

        /// <summary>
        /// 第一套 profile（按路径序）——只需任意 / 主配置时的便利访问。工程没有配置时抛出清晰的
        /// <see cref="System.InvalidOperationException"/>，不再创建指向样例目录的隐式资产；需要可选探测时使用 <see cref="ResolveAll"/>。
        /// 要按各套操作（定位 / 打开目录 / 单独生成）用 <see cref="ResolveAll"/> 或「配置总览」窗口（<see cref="LubanConfigOverviewWindow"/>）。
        /// </summary>
        public static LubanConfigProfile Resolve() => ResolveAll().FirstOrDefault() ??
            throw new System.InvalidOperationException(
                "工程里没有 LubanConfigProfile。请在 SSFramework/代码生成/配置表 (Luban) 工作台显式新建并填写项目路径。");
    }
}
