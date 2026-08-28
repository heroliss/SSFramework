using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Network.Proto.Editor
{
    /// <summary>
    /// Protobuf 协议生成配置（编辑器资产）——<b>一套</b> .proto 契约「源在哪、protoc 怎么调、生成 C# 落到哪」
    /// 的单一真源。生成管线（<see cref="ProtoCodeGenerator"/>）只读本资产，不在代码里散落路径常量；
    /// 换项目 / 换目录结构改 Inspector 即可，不动代码。
    ///
    /// 路径字段一律相对工程根目录，保证多人协作不受本机绝对路径影响。
    /// <b>工程可并存多套</b>（正式协议一套 + 框架测试一套等）：每套必须独占一个位于 <c>Assets</c> 内、
    /// 且与其它配置不嵌套的输出目录；<see cref="ResolveAll"/> 返回全部。生成入口会先比较所有已经成立的安全输出声明，
    /// 再按 Profile 就绪状态逐套生成；空白新配置不声明所有权，也不会冻结其它可用配置。
    /// 无自动创建（默认路径无从捏造）：经 <c>Assets/Create/SSFramework/Protobuf 生成配置</c> 或「配置总览」窗口新建。
    /// </summary>
    [CreateAssetMenu(fileName = "ProtoConfigProfile", menuName = "SSFramework/Protobuf 生成配置 (Proto Profile)")]
    public sealed class ProtoConfigProfile : ScriptableObject
    {
        [Header("protoc 工具")]
        [Tooltip("protoc 所在根目录（相对工程根目录），按编辑器平台取 <目录>/windows_x64/protoc.exe、macosx_x64/protoc、linux_x64/protoc。\n" +
                 "仓库自带 Windows x64；其余平台从 https://github.com/protocolbuffers/protobuf/releases 下载，解压出的 bin/protoc 放入对应子目录。")]
        [InspectorName("protoc 工具目录")]
        [SerializeField] private string _protocDir = "Tools/Protoc";

        [Header(".proto 源")]
        [Tooltip(".proto 源目录（相对工程根目录，含子目录全收）。推荐放业务模块下的 Proto~（~ 后缀 Unity 不导入源文件）。\n" +
                 "该目录同时作为 protoc 的 --proto_path：.proto 之间的 import 以此为根。")]
        [InspectorName(".proto 源目录")]
        [SerializeField] private string _protoDir = "";

        [Header("产物输出")]
        [Tooltip("生成 C#（*.g.cs）的输出目录。必须是 Assets 下由本 Profile 独占的子目录，不能与其它 Profile 相同或嵌套。\n" +
                 "目录里的 *.g.cs 由生成器接管：.proto 改名 / 删除后遗留的陈旧 *.g.cs 会被自动清理，勿手放同后缀文件。")]
        [InspectorName("代码输出目录")]
        [SerializeField] private string _outputCodeDir = "";

        [Tooltip("附加 protoc 参数（按空格切分逐个传入，如 --csharp_opt=internal_access 或额外 --proto_path=...）。一般留空。")]
        [InspectorName("附加 protoc 参数")]
        [SerializeField] private string _extraArgs = "";

        public string ProtocDir => _protocDir?.Trim().TrimEnd('/', '\\') ?? "";
        public string ProtoDir => _protoDir?.Trim().TrimEnd('/', '\\') ?? "";
        public string OutputCodeDir => _outputCodeDir?.Trim().TrimEnd('/', '\\') ?? "";
        public string ExtraArgs => _extraArgs?.Trim() ?? "";

        /// <summary>
        /// 返回工程内**所有** Proto profile（按资产路径排序，显示稳定）。每套对应一套 .proto 契约，
        /// 生成入口先验证各套输出目录互不相同或嵌套，再逐套生成。一套都没有时返回空表（由工作台引导创建）。
        /// </summary>
        public static IReadOnlyList<ProtoConfigProfile> ResolveAll() =>
            AssetDatabase.FindAssets("t:" + nameof(ProtoConfigProfile))
                .Select(g => AssetDatabase.LoadAssetAtPath<ProtoConfigProfile>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(p => p != null)
                .OrderBy(AssetDatabase.GetAssetPath, System.StringComparer.Ordinal)
                .ToList();
    }
}
