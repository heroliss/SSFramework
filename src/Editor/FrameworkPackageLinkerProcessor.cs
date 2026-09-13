using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.UnityLinker;
using UnityEngine;
using Log = Game.Framework.Logging.Log;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 将 Package 中各 Framework Runtime Module 自带的裁剪规则提交给 UnityLinker。
    /// </summary>
    /// <remarks>
    /// Unity 只自动发现 Assets 下的 link.xml。Package 源码通过 Source Catalog 定位，
    /// 规则仍由对应 Module 拥有；本入口不维护具体 Adapter 名单，也不改项目 Assets 或包缓存。
    /// Module 的 link.xml 与 asmdef 放在同一目录；不在目标平台编译图中的 Module 不贡献规则。
    /// </remarks>
    internal sealed class FrameworkPackageLinkerProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            // 编译图包括尚未静态使用的 Adapter，也包括 HybridCLR 热更 Module。
            // 后者仍可能需要保留引擎 AOT 类型；自身 DLL 的缺失策略由 Module 的 ignoreIfMissing 声明。
            string[] modulePaths = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                .Where(assembly => assembly.name == "Game.Framework" ||
                                   assembly.name.StartsWith("Game.Framework.", StringComparison.Ordinal))
                .Select(assembly => CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name))
                .ToArray();
            string[] paths = SelectLinkXmlAssetPaths(modulePaths, AssetDatabase.GetAllAssetPaths());
            FrameworkModuleSourceCatalog.SourceLocation[] sources =
                FrameworkModuleSourceCatalog.ResolveKnownAssetPaths(paths);
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "SSFramework",
                "Linker", data.target.ToString(), "link.xml"));
            WriteLinkXml(sources, output);
            if (sources.Length > 0)
                Log.Info("[SSFramework] 已提交 Package 裁剪规则：\n  " + string.Join("\n  ", paths) +
                         "\n生成文件：" + output);
            return output;
        }

        /// <summary>只选择有效 Runtime asmdef 相邻的 Package 规则；Assets 规则由 Unity 自行处理。</summary>
        internal static string[] SelectLinkXmlAssetPaths(
            IEnumerable<string> runtimeAsmdefPaths, IEnumerable<string> knownAssetPaths)
        {
            if (runtimeAsmdefPaths == null) throw new ArgumentNullException(nameof(runtimeAsmdefPaths));
            if (knownAssetPaths == null) throw new ArgumentNullException(nameof(knownAssetPaths));
            var assets = new HashSet<string>(knownAssetPaths, StringComparer.Ordinal);
            return runtimeAsmdefPaths
                .Where(path => !string.IsNullOrWhiteSpace(path) &&
                               path.StartsWith("Packages/", StringComparison.Ordinal))
                .Select(path => path.Substring(0, path.LastIndexOf('/') + 1) + "link.xml")
                .Where(assets.Contains)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// 校验全部输入后才写派生文件；每轮重建（包括空集合），避免删除 Module 后沿用旧保留根。
        /// XML 的类型、成员及条件属性原样保留，不在汇总时放宽 Module 的裁剪策略。
        /// </summary>
        internal static void WriteLinkXml(
            IEnumerable<FrameworkModuleSourceCatalog.SourceLocation> sources, string outputPath)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            var linker = new XElement("linker");
            foreach (FrameworkModuleSourceCatalog.SourceLocation source in sources
                         .OrderBy(item => item.AssetPath, StringComparer.Ordinal))
            {
                try
                {
                    // link.xml 不需要外部实体；禁止 DTD，也避免合并时展开不可追溯的外部输入。
                    using var reader = XmlReader.Create(source.PhysicalPath,
                        new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    XElement root = XDocument.Load(reader).Root;
                    if (root == null || root.Name != "linker" || root.HasAttributes ||
                        root.Elements().Any(element => element.Name != "assembly"))
                        throw new InvalidDataException("需要无属性的 linker 根及 assembly 子元素。");
                    foreach (XElement assembly in root.Elements())
                        linker.Add(new XElement(assembly));
                }
                catch (Exception exception) when (exception is IOException || exception is XmlException ||
                                                   exception is InvalidDataException ||
                                                   exception is UnauthorizedAccessException)
                {
                    throw new BuildFailedException(
                        $"无法读取 Package 裁剪规则 {source.AssetPath}（{source.PhysicalPath}）：{exception.Message}");
                }
            }

            string output = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, new XDocument(linker).ToString(), new UTF8Encoding(false));
        }
    }
}
