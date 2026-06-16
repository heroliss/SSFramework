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

            // 变体：本 prefab 是变体且基有绑定 → 子类继承基窗口类，只产出「净新增」字段；继承字段由 base.BindNodes() 绑。
            bool isVariant = UIBindingUtil.TryResolveVariantBase(prefabPath, profile,
                out var baseData, out string baseClassName, out string baseNamespace);

            var basePaths = new HashSet<string>();
            var baseByPath = new Dictionary<string, UIBindingEntry>();
            var warnings = new List<string>();
            if (isVariant)
            {
                foreach (var be in baseData.Entries)
                {
                    string bp = be.Path ?? string.Empty;
                    basePaths.Add(bp);
                    baseByPath[bp] = be;
                    // 删基节点：基类绑定的节点在变体树里没了 → 基类 BindNode 运行时 transform.Find 会失败（该字段为 null）。
                    var bt = string.IsNullOrEmpty(bp) ? root : root.Find(bp);
                    if (bt == null)
                        warnings.Add($"变体删除了基类绑定的节点 \"{bp}\"——基类字段运行时会找不到节点（该字段为 null）。");
                }
            }

            // 校验路径/组件并收集字段。变体下跳过继承条目（基类已有），只收净新增；字段名并入基字段名查重，避免子类 shadow 基类同名字段。
            var fields = new List<Field>();
            var seen = isVariant ? BaseFieldNames(baseData, root) : new HashSet<string>();
            foreach (var entry in data.Entries)
            {
                string path = entry.Path ?? string.Empty;
                if (isVariant && basePaths.Contains(path))
                {
                    // 继承条目：被变体改了组件/字段名 → 警告（不在子类重绑，C# 不能改基类字段类型）。
                    if (!SameBinding(baseByPath[path], entry))
                        warnings.Add($"变体改了基节点 \"{path}\" 的绑定（组件或字段名）——不在子类重绑；要改请改用新建独立窗口。");
                    continue;
                }

                var nodeTf = string.IsNullOrEmpty(path) ? root : root.Find(path);
                if (nodeTf == null)
                    return (false, $"绑定路径找不到节点：\"{path}\"（prefab 结构改了？重新标记该节点）。");

                var types = entry.ComponentTypes ?? new List<string>();
                if (types.Count == 0)
                    return (false, $"节点 \"{path}\" 没有指定要绑定的组件。");

                string baseName = !string.IsNullOrEmpty(entry.FieldName)
                    ? UIBindingUtil.SanitizeIdentifier(entry.FieldName)
                    : UIBindingUtil.DeriveBaseName(path, root.name);

                foreach (var typeId in types)
                {
                    var type = UIBindingUtil.ResolveType(typeId);
                    if (type == null)
                        return (false, $"无法解析组件类型：{typeId}（节点 \"{path}\"）。");
                    if (nodeTf.GetComponent(type) == null)
                        return (false, $"节点 \"{path}\" 上没有组件 {type.Name}（prefab 改了？重新标记）。");

                    string fieldName = types.Count > 1 ? baseName + type.Name : baseName;
                    if (!seen.Add(fieldName))
                        return (false, isVariant
                            ? $"变体字段名与基类字段或其它变体字段重复：{fieldName}（节点 \"{path}\"）。给该节点设置不同字段名。"
                            : $"字段名重复：{fieldName}（节点 \"{path}\"）。给冲突的节点设置不同的字段名。");

                    fields.Add(new Field(fieldName, type, path));
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
            File.WriteAllText(nodesAbs, BuildNodesFile(ns, className, prefabPath, fields, isVariant ? baseClassName : null), Utf8NoBom);

            string outDirAbs = Path.Combine(projectRoot, outputDir);
            Directory.CreateDirectory(outDirAbs);
            string logicAbs = Path.Combine(outDirAbs, className + ".cs");
            bool createdLogic = !File.Exists(logicAbs);
            if (createdLogic)
                File.WriteAllText(logicAbs, BuildLogicFile(ns, className, location, isVariant ? baseClassName : null, isVariant ? baseNamespace : null), Utf8NoBom);

            AssetDatabase.Refresh();

            string kind = isVariant ? $"变体——继承 {baseClassName}，{fields.Count} 个净新增字段" : $"{fields.Count} 个绑定字段";
            string logicNote = createdLogic
                ? $"逻辑骨架 → {outputDir}/{className}.cs（新建，请填 OnCreated）"
                : $"逻辑文件已存在，未覆盖：{outputDir}/{className}.cs";
            string warnNote = warnings.Count > 0 ? "\n  ⚠ " + string.Join("\n  ⚠ ", warnings) : string.Empty;
            return (true, $"生成完成：{className}（{kind}，命名空间 {ns}）\n  绑定 → {generatedDir}/{className}.nodes.g.cs（已覆盖）\n  {logicNote}{warnNote}");
        }

        // 变体「改」判定：两条绑定的组件集合（顺序无关）+ 字段名是否一致。
        private static bool SameBinding(UIBindingEntry a, UIBindingEntry b)
        {
            if ((a.FieldName ?? string.Empty) != (b.FieldName ?? string.Empty)) return false;
            var ca = a.ComponentTypes ?? new List<string>();
            var cb = b.ComponentTypes ?? new List<string>();
            if (ca.Count != cb.Count) return false;
            foreach (var id in ca) if (!cb.Contains(id)) return false;
            return true;
        }

        // 基类已生成的字段名集合——变体净新增字段名并入查重，避免子类字段 shadow 基类同名字段（CS0108）。
        private static HashSet<string> BaseFieldNames(UIBindingData baseData, Transform root)
        {
            var names = new HashSet<string>();
            foreach (var entry in baseData.Entries)
            {
                var types = entry.ComponentTypes ?? new List<string>();
                string baseName = !string.IsNullOrEmpty(entry.FieldName)
                    ? UIBindingUtil.SanitizeIdentifier(entry.FieldName)
                    : UIBindingUtil.DeriveBaseName(entry.Path ?? string.Empty, root.name);
                foreach (var typeId in types)
                {
                    var type = UIBindingUtil.ResolveType(typeId);
                    string tn = type != null ? type.Name : "_";
                    names.Add(types.Count > 1 ? baseName + tn : baseName);
                }
            }
            return names;
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

        private static string BuildNodesFile(string ns, string className, string prefabPath, List<Field> fields, string baseClassName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//     由 UIBindingCodeGenerator 从 prefab 节点绑定生成，勿手改；重新生成会覆盖。");
            sb.AppendLine($"//     源 prefab：{prefabPath}");
            if (baseClassName != null)
                sb.AppendLine($"//     变体增量：本类继承 {baseClassName}，仅含基类之外的净新增字段；继承字段由 base.BindNodes() 绑。");
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

        private static string BuildLogicFile(string ns, string className, string location, string baseClassName, string baseNamespace)
        {
            // 变体：继承基窗口类（全限定 global:: 引用，规避命名空间差异），OnCreated 先 base.OnCreated() 复用基类接线。
            bool isVariant = baseClassName != null;
            string baseTypeRef = isVariant ? $"global::{baseNamespace}.{baseClassName}" : "UGuiWindowBase";

            var sb = new StringBuilder();
            sb.AppendLine("using Game.Framework.UI;");
            if (!isVariant) sb.AppendLine("using Game.Framework.UI.UGui;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            string doc = isVariant
                ? $"{className} 变体窗口逻辑（继承 {baseClassName}）。净新增绑定字段在 {className}.nodes.g.cs（重新生成会覆盖）；本文件只写业务逻辑，不会被覆盖。"
                : $"{className} 窗口逻辑。节点绑定字段在 {className}.nodes.g.cs（重新生成会覆盖）；本文件只写业务逻辑，不会被覆盖。";
            sb.AppendLine($"    /// <summary>{doc}</summary>");
            sb.AppendLine($"    [UIWindow(Asset = \"{Escape(location)}\", Layer = UILayer.Window)]");
            sb.AppendLine($"    public partial class {className} : {baseTypeRef}");
            sb.AppendLine("    {");
            sb.AppendLine("        protected override void OnCreated()");
            sb.AppendLine("        {");
            if (isVariant) sb.AppendLine("            base.OnCreated(); // 复用基类接线（基类已绑字段为 protected，子类可直接用）");
            sb.AppendLine("            // TODO: 在此接线——订阅查询 Command、接按钮（绑定字段此时已就绪）。");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
