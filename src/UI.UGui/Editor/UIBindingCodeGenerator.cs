using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 从一个 UGUI 窗口 prefab 根上的 <see cref="UIBindingData"/> 生成两份代码：
    /// <list type="bullet">
    ///   <item><c>&lt;Name&gt;.nodes.g.cs</c>（落 <c>GeneratedCodeDir</c>）——partial，节点字段 + <c>BindNodes()</c>，<b>每次覆盖</b>。</item>
    ///   <item><c>&lt;Name&gt;.cs</c>（落 <c>OutputCodeDir</c>）——窗口逻辑骨架（<c>[UIWindow]</c> + <c>OnCreated</c>），<b>仅当不存在时</b>生成、之后不覆盖。</item>
    /// </list>
    /// 绑定走 <c>UGuiWindowBase.BindNode</c> 的运行时 <c>transform.Find</c>，不依赖序列化引用——改完 prefab 重新生成即可。
    /// </summary>
    public static class UIBindingCodeGenerator
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private readonly struct Field
        {
            public readonly string Name;
            public readonly Type Type;
            public readonly string Path;
            public Field(string name, Type type, string path) { Name = name; Type = type; Path = path; }
        }

        /// <summary>
        /// 从已在内存里的绑定数据组件生成代码（root = <paramref name="data"/> 所在节点）。
        /// 传 stage 里的活组件 = 反映未保存的当前绑定；传 <see cref="PrefabUtility.LoadPrefabContents"/> 的组件 = 反映磁盘已存状态。
        /// </summary>
        public static (bool ok, string message) Generate(string prefabPath, UIBindingData data, UICodeGenProfile profile)
        {
            if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab"))
                return (false, $"不是 prefab：{prefabPath}");
            if (data == null)
                return (false, $"{prefabPath} 根上没有 UIBindingData——先在 Prefab 编辑模式选节点标记 / 在 Hierarchy 点「＋」绑定。");
            if (data.Entries == null || data.Entries.Count == 0)
                return (false, $"{prefabPath} 没有任何绑定。先标记要绑的节点。");

            string className = UIBindingUtil.SanitizeIdentifier(Path.GetFileNameWithoutExtension(prefabPath));
            string location = Path.GetFileNameWithoutExtension(prefabPath); // AddressByFileName：地址 = 文件名
            var root = data.transform;

            // 校验路径/组件并收集字段。
            var fields = new List<Field>();
            var seen = new HashSet<string>();
            foreach (var entry in data.Entries)
            {
                var nodeTf = string.IsNullOrEmpty(entry.Path) ? root : root.Find(entry.Path);
                if (nodeTf == null)
                    return (false, $"绑定路径找不到节点：\"{entry.Path}\"（prefab 结构改了？重新标记该节点）。");

                var types = entry.ComponentTypes ?? new List<string>();
                if (types.Count == 0)
                    return (false, $"节点 \"{entry.Path}\" 没有指定要绑定的组件。");

                string baseName = !string.IsNullOrEmpty(entry.FieldName)
                    ? UIBindingUtil.SanitizeIdentifier(entry.FieldName)
                    : UIBindingUtil.DeriveBaseName(entry.Path, root.name);

                foreach (var typeId in types)
                {
                    var type = UIBindingUtil.ResolveType(typeId);
                    if (type == null)
                        return (false, $"无法解析组件类型：{typeId}（节点 \"{entry.Path}\"）。");
                    if (nodeTf.GetComponent(type) == null)
                        return (false, $"节点 \"{entry.Path}\" 上没有组件 {type.Name}（prefab 改了？重新标记）。");

                    string fieldName = types.Count > 1 ? baseName + type.Name : baseName;
                    if (!seen.Add(fieldName))
                        return (false, $"字段名重复：{fieldName}（节点 \"{entry.Path}\"）。给冲突的节点设置不同的字段名。");

                    fields.Add(new Field(fieldName, type, entry.Path));
                }
            }

            // 生成目标：本 prefab 覆盖 > Profile 默认（命名空间须保持逻辑/绑定两 partial 一致，故走同一解析）。
            string ns = UIBindingUtil.ResolveNamespace(data, profile);
            string outputDir = UIBindingUtil.ResolveOutputDir(data, profile);
            string generatedDir = UIBindingUtil.ResolveGeneratedDir(data, profile);

            // 落盘：绑定 partial 与逻辑骨架分目录。
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;

            string genDirAbs = Path.Combine(projectRoot, generatedDir);
            Directory.CreateDirectory(genDirAbs);
            string nodesAbs = Path.Combine(genDirAbs, className + ".nodes.g.cs");
            File.WriteAllText(nodesAbs, BuildNodesFile(ns, className, prefabPath, fields), Utf8NoBom);

            string outDirAbs = Path.Combine(projectRoot, outputDir);
            Directory.CreateDirectory(outDirAbs);
            string logicAbs = Path.Combine(outDirAbs, className + ".cs");
            bool createdLogic = !File.Exists(logicAbs);
            if (createdLogic)
                File.WriteAllText(logicAbs, BuildLogicFile(ns, className, location), Utf8NoBom);

            AssetDatabase.Refresh();

            string logicNote = createdLogic
                ? $"逻辑骨架 → {outputDir}/{className}.cs（新建，请填 OnCreated）"
                : $"逻辑文件已存在，未覆盖：{outputDir}/{className}.cs";
            return (true, $"生成完成：{className}（{fields.Count} 个绑定字段，命名空间 {ns}）\n  绑定 → {generatedDir}/{className}.nodes.g.cs（已覆盖）\n  {logicNote}");
        }

        /// <summary>
        /// 对一个磁盘上的 prefab 生成代码：临时载入 prefab 内容（<see cref="PrefabUtility.LoadPrefabContents"/>）读其绑定组件 → 生成。
        /// 用于 Project 右键 / 该 prefab 未在 Stage 打开的场景；按磁盘已存状态生成。
        /// </summary>
        public static (bool ok, string message) GenerateFromAsset(string prefabPath, UICodeGenProfile profile)
        {
            if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab"))
                return (false, $"不是 prefab：{prefabPath}");

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                return Generate(prefabPath, root.GetComponent<UIBindingData>(), profile);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>生成并把结果打到 Console，返回 (ok, message)。供菜单 / 根行按钮 / 弹面板 / 自动生成共用。</summary>
        public static (bool ok, string message) GenerateAndLog(string prefabPath, UIBindingData data, UICodeGenProfile profile)
        {
            var result = Generate(prefabPath, data, profile);
            Debug.Log("[UI 绑定] " + result.message);
            return result;
        }

        private static string BuildNodesFile(string ns, string className, string prefabPath, List<Field> fields)
        {
            var sb = new StringBuilder();
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//     由 UIBindingCodeGenerator 从 prefab 节点绑定生成，勿手改；重新生成会覆盖。");
            sb.AppendLine($"//     源 prefab：{prefabPath}");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    partial class {className}");
            sb.AppendLine("    {");
            foreach (var f in fields)
                sb.AppendLine($"        protected {UIBindingUtil.CSharpTypeName(f.Type)} {f.Name};");
            sb.AppendLine();
            sb.AppendLine("        protected override void BindNodes()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.BindNodes();");
            foreach (var f in fields)
                sb.AppendLine($"            {f.Name} = BindNode<{UIBindingUtil.CSharpTypeName(f.Type)}>(\"{Escape(f.Path)}\");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string BuildLogicFile(string ns, string className, string location)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using Game.Framework.UI;");
            sb.AppendLine("using Game.Framework.UI.UGui;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>{className} 窗口逻辑。节点绑定字段在 {className}.nodes.g.cs（重新生成会覆盖）；本文件只写业务逻辑，不会被覆盖。</summary>");
            sb.AppendLine($"    [UIWindow(Asset = \"{Escape(location)}\", Layer = UILayer.Window)]");
            sb.AppendLine($"    public partial class {className} : UGuiWindowBase");
            sb.AppendLine("    {");
            sb.AppendLine("        protected override void OnCreated()");
            sb.AppendLine("        {");
            sb.AppendLine("            // TODO: 在此接线——订阅查询 Command、接按钮（绑定字段此时已就绪）。");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
