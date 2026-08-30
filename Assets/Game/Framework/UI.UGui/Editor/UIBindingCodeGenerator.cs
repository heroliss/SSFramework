using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.Framework.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
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
        internal const string OutputClaimSourceId = "ui-binding";

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
            if (profile == null)
                return (false, "没有 UICodeGenProfile。请先打开 SSFramework/代码生成/UI 绑定，创建并填写生成目标。");
            if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab"))
                return (false, $"不是 prefab：{prefabPath}");
            if (data == null)
                return (false, $"{prefabPath} 根上没有 UIBindingData——先在 Prefab 编辑模式选节点标记 / 在 Hierarchy 点「＋」绑定。");
            if (data.Entries == null || data.Entries.Count == 0)
                return (false, $"{prefabPath} 没有任何绑定。先标记要绑的节点。");

            string ns = UIBindingUtil.ResolveNamespace(prefabPath, data, profile);
            string outputDir = UIBindingUtil.ResolveOutputDir(prefabPath, data, profile);
            string generatedDir = UIBindingUtil.ResolveGeneratedDir(prefabPath, data, profile);
            var missingTargets = new List<string>();
            if (string.IsNullOrWhiteSpace(ns)) missingTargets.Add("命名空间");
            if (string.IsNullOrWhiteSpace(outputDir)) missingTargets.Add("逻辑目录");
            if (string.IsNullOrWhiteSpace(generatedDir)) missingTargets.Add("生成目录");
            if (missingTargets.Count > 0)
                return (false, "UI 生成目标尚未配置完整：" + string.Join("、", missingTargets) +
                               "。请在全工程 Profile、目录配置或当前 prefab 覆盖中明确填写。");
            if (!FrameworkCSharpSyntax.TryValidateNamespace(ns.Trim(), out string namespaceError))
                return (false, "UI 生成命名空间无效：" + namespaceError);
            bool outputDirectoryOk = FrameworkProjectPath.TryResolveAssetsDirectory(
                outputDir,
                out string normalizedOutputDir,
                out string outDirAbs,
                out string outputDirectoryError);
            bool generatedDirectoryOk = FrameworkProjectPath.TryResolveAssetsDirectory(
                generatedDir,
                out string normalizedGeneratedDir,
                out string genDirAbs,
                out string generatedDirectoryError);
            if (!outputDirectoryOk || !generatedDirectoryOk)
            {
                var pathErrors = new List<string>();
                if (!outputDirectoryOk) pathErrors.Add("逻辑目录：" + outputDirectoryError);
                if (!generatedDirectoryOk) pathErrors.Add("生成目录：" + generatedDirectoryError);
                return (false,
                    "UI 逻辑目录与生成目录无效；两者都必须是 Assets 下的独立项目相对路径：\n" +
                    string.Join("\n", pathErrors));
            }
            outputDir = normalizedOutputDir;
            generatedDir = normalizedGeneratedDir;

            string className = UIBindingUtil.ResolveClassName(prefabPath, data, profile); // 文件名模板 + 占位符 → 类名 / 文件名
            if (!TryValidateOutputClaimsBeforeWrite(
                    prefabPath,
                    normalizedOutputDir,
                    outDirAbs,
                    normalizedGeneratedDir,
                    genDirAbs,
                    className,
                    out string ownershipError))
                return (false, ownershipError);

            string location = Path.GetFileNameWithoutExtension(prefabPath); // AddressByFileName：[UIWindow(Asset)] 恒 = prefab 文件名，与类名无关
            var root = data.transform;

            // 变体：本 prefab 是变体且基有绑定 → 子类继承基窗口类，只产出「净新增」字段；继承字段由 base.BindNodes() 绑。
            bool isVariant = UIBindingUtil.TryResolveVariantBase(prefabPath, profile,
                out var baseData, out string baseClassName, out string baseNamespace);

            var baseBindings = new HashSet<string>(); // 已被基类绑定的 "path\0componentId"：变体净新增时跳过（继承字段不重生成）
            var warnings = new List<string>();
            // 变体相较基类的「失效继承字段」——都生成 [Obsolete] 遮蔽（IDE 悬停/用到即知）。Dead 区分成因、给不同提示：
            //   Dead=true ：节点没了 / 该组件被移除 → base.BindNode 找不到 → 运行时为 null。
            //   Dead=false：组件还在、基类仍绑（值非 null），但变体已取消勾选/解绑 → 本变体不该再用它（子类无法真正移除继承字段）。
            var removedFields = new List<(string Name, string TypeRef, string Path, bool Dead)>();
            if (isVariant)
            {
                // 逐「基字段（节点+组件）」判定：删节点、改名、换组件（Button→Image）、或仅取消勾选，都能精确识别到具体失效字段。
                foreach (var be in baseData.Entries)
                {
                    string bp = be.Path ?? string.Empty;
                    var bnode = string.IsNullOrEmpty(bp) ? root : root.Find(bp);
                    var ve = data.Find(bp); // 变体对该路径的绑定（解绑则 null）
                    var btypes = be.ComponentTypes ?? new List<string>();
                    foreach (var typeId in btypes)
                    {
                        baseBindings.Add(bp + "\0" + typeId);
                        var btype = UIBindingUtil.ResolveType(typeId);
                        if (btype == null) continue; // 类型解析不出：无法遮蔽，靠 base.BindNode 运行时报错兜底
                        string fname = FieldNameFor(be, btype, btypes.Count, root, profile);
                        // 运行时这条继承字段还能被 base.BindNodes() 绑成吗？=（节点在 且 组件在）——正是基类 BindNode 的判定。
                        bool alive = bnode != null && (btype == typeof(GameObject) || bnode.GetComponent(btype) != null);
                        // 变体的绑定清单里是否还勾着它？（解绑、或取消该组件勾选，都算「不再勾」）
                        bool keptByVariant = ve != null && (ve.ComponentTypes ?? new List<string>()).Contains(typeId);
                        if (!alive)
                        {
                            removedFields.Add((fname, UIBindingUtil.CSharpTypeName(btype), bp, true));
                            warnings.Add($"基节点 \"{bp}\" 的继承字段 {fname} 在变体里已失效（节点/组件没了）——运行时为 null，已标 [Obsolete] 遮蔽。");
                        }
                        else if (!keptByVariant)
                        {
                            removedFields.Add((fname, UIBindingUtil.CSharpTypeName(btype), bp, false));
                            warnings.Add($"变体取消了基节点 \"{bp}\" 的继承字段 {fname}——基类仍会绑定（非 null），但本变体不应再用，已标 [Obsolete] 遮蔽。要彻底去掉请改建独立窗口。");
                        }
                    }
                }
            }

            // 校验路径/组件并收集字段。变体下跳过继承条目（基类已有），只收净新增；字段名并入基字段名查重，避免子类 shadow 基类同名字段。
            var fields = new List<Field>();
            var seen = isVariant ? BaseFieldNames(baseData, root, profile) : new HashSet<string>();
            foreach (var entry in data.Entries)
            {
                string path = entry.Path ?? string.Empty;

                var nodeTf = string.IsNullOrEmpty(path) ? root : root.Find(path);
                if (nodeTf == null)
                {
                    // 节点改名/删除/移走，旧路径已失联——跳过该条、不挡整份生成（生成永远按当前绑定状态产出，不反过来卡住）。不需要它就在 Hierarchy 解绑。
                    warnings.Add($"绑定路径找不到节点 \"{path}\"（改名/删除/移走了？）——已跳过该绑定。不需要它请在 Hierarchy 解绑。");
                    continue;
                }

                var types = entry.ComponentTypes ?? new List<string>();
                if (types.Count == 0)
                {
                    // 一个组件都没勾：该条目不产出任何字段，跳过并提示——不因一个空条目挡掉整份生成。不需要它就直接解绑。
                    warnings.Add($"节点 \"{path}\" 没勾选任何组件——已跳过（不生成字段）。不需要它请解绑。");
                    continue;
                }

                foreach (var typeId in types)
                {
                    if (isVariant && baseBindings.Contains(path + "\0" + typeId))
                        continue; // 该(节点,组件)已是基类绑定的继承字段——子类不重生成，只产基类没有的净新增（如基节点上新加的组件）
                    var type = UIBindingUtil.ResolveType(typeId);
                    if (type == null)
                    {
                        warnings.Add($"节点 \"{path}\" 的组件类型解析不出：{typeId}（程序集没加载？）——已跳过该字段。");
                        continue;
                    }
                    // GameObject 不是组件、绑的是节点本身（生成 BindGameObject），跳过「组件是否存在」校验；其余组件须真实挂在节点上。
                    if (type != typeof(GameObject) && nodeTf.GetComponent(type) == null)
                    {
                        warnings.Add($"节点 \"{path}\" 上已没有组件 {type.Name}（prefab 改了？）——已跳过该字段。");
                        continue;
                    }

                    string fieldName = FieldNameFor(entry, type, types.Count, root, profile);
                    if (!seen.Add(fieldName))
                    {
                        warnings.Add(isVariant
                            ? $"变体字段名与基类或其它字段重复：{fieldName}（节点 \"{path}\"）——已跳过该字段，请设置不同字段名。"
                            : $"字段名重复：{fieldName}（节点 \"{path}\"）——已跳过该字段，请给冲突节点设置不同字段名。");
                        continue;
                    }

                    fields.Add(new Field(fieldName, type, path));
                }
            }

            // 生成目标：覆盖链 prefab 覆盖 → 目录配置 → Profile（命名空间须保持逻辑/绑定两 partial 一致，故走同一解析）。
            // 落盘：绑定 partial 与逻辑骨架分目录。
            Directory.CreateDirectory(genDirAbs);
            string nodesAbs = Path.Combine(genDirAbs, className + ".nodes.g.cs");
            File.WriteAllText(nodesAbs, BuildNodesFile(ns, className, prefabPath, fields, isVariant ? baseClassName : null, removedFields), Utf8NoBom);

            Directory.CreateDirectory(outDirAbs);
            string logicAbs = Path.Combine(outDirAbs, className + ".cs");
            bool createdLogic = !File.Exists(logicAbs);
            if (createdLogic)
                File.WriteAllText(logicAbs, BuildLogicFile(ns, className, location, isVariant ? baseClassName : null, isVariant ? baseNamespace : null), Utf8NoBom);

            AssetDatabase.Refresh();

            // 可选：把生成的窗口脚本自动挂到 prefab 根（缺则挂、旧则换；变体即替换继承的基脚本）。默认开；类未编译完时跳过，prefab 在编辑模式打开则改活 stage 根、随 Ctrl+S 保存。
            string swapNote = profile.AutoAssignWindowScript
                ? TryAutoAssignWindowScript(prefabPath, ns, className)
                : string.Empty;

            string kind = isVariant ? $"变体——继承 {baseClassName}，{fields.Count} 个净新增字段" : $"{fields.Count} 个绑定字段";
            string logicNote = createdLogic
                ? $"逻辑骨架 → {outputDir}/{className}.cs（新建，请填 OnCreated）"
                : $"逻辑文件已存在，未覆盖：{outputDir}/{className}.cs";
            string warnNote = warnings.Count > 0 ? "\n  ⚠ " + string.Join("\n  ⚠ ", warnings) : string.Empty;
            return (true, $"生成完成：{className}（{kind}，命名空间 {ns}）\n  绑定 → {generatedDir}/{className}.nodes.g.cs（已覆盖）\n  {logicNote}{swapNote}{warnNote}");
        }

        // 把生成的窗口脚本挂到 prefab 根：移除根上任何「非目标」的窗口脚本（变体继承的基脚本 / 改名前的旧脚本），加目标脚本。
        // 基础与变体共用（变体的「替换继承基脚本」只是其中一种情形）。类未编译完时跳过（提示编译后再生成）。
        // prefab 正在编辑模式打开：直接改活的 stage 根（Undo + 标脏，随 Ctrl+S 保存，prefab stage 的未保存改动可跨域重载存活）；
        // 否则对磁盘 prefab 的临时副本改并回存（不能对开着的 prefab 走 LoadPrefabContents/SaveAsPrefabAsset，会冲突）。
        private static string TryAutoAssignWindowScript(string prefabPath, string ns, string className)
        {
            var windowType = ResolveType(ns + "." + className);
            if (windowType == null)
                return "\n  · 窗口脚本未自动挂：窗口类还没编译完，编译完成后再次生成即自动挂。";

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == prefabPath)
                return EnsureWindowComponent(stage.prefabContentsRoot, windowType, className, stage);

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                bool already = root.GetComponent(windowType) != null;
                string note = EnsureWindowComponent(root, windowType, className, null);
                if (!already) PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return note;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // 确保 root 上挂着 windowType（已有则不动；否则移除其它窗口脚本再加它）。stage 非 null = 改活的 stage 根、用 Undo 并标脏；
        // stage 为 null = 改 LoadPrefabContents 的副本（由调用方回存）。返回生成日志用的说明。
        private static string EnsureWindowComponent(GameObject root, Type windowType, string className, PrefabStage stage)
        {
            if (root.GetComponent(windowType) != null)
                return "\n  · 窗口脚本已是最新，无需改动。";

            Component old = null;
            foreach (var c in root.GetComponents<Component>())
                if (c != null && IsWindowComponent(c.GetType()) && c.GetType() != windowType) { old = c; break; }
            string oldName = old != null ? old.GetType().Name : null;

            if (stage != null)
            {
                if (old != null) Undo.DestroyObjectImmediate(old);
                Undo.AddComponent(root, windowType);
                EditorSceneManager.MarkSceneDirty(stage.scene);
            }
            else
            {
                if (old != null) UnityEngine.Object.DestroyImmediate(old, true);
                root.AddComponent(windowType);
            }

            string tail = stage != null ? "（已标脏，Ctrl+S 保存）" : string.Empty;
            return oldName != null
                ? $"\n  · 已自动把 prefab 根的窗口脚本从 {oldName} 换成 {className}{tail}。"
                : $"\n  · 已自动把窗口脚本 {className} 挂到 prefab 根{tail}。";
        }

        // 按全名在已加载程序集里找类型（autoReferenced:false 的业务窗口类也能找到）。
        private static Type ResolveType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        // 类型链上是否有名为 UGuiWindowBase 的基类（名称判定，避开对运行时程序集硬引用）。
        private static bool IsWindowComponent(Type t)
        {
            for (var b = t; b != null && b != typeof(object); b = b.BaseType)
                if (b.Name == "UGuiWindowBase") return true;
            return false;
        }

        // 字段命名口径与 Inspector 展示共用 UIBindingUtil.EffectiveFieldName，避免「显示名」与「生成名」漂移。
        private static string FieldNameFor(UIBindingEntry entry, Type type, int typeCount, Transform root, UICodeGenProfile profile)
            => UIBindingUtil.EffectiveFieldName(entry, type, typeCount, root.name, profile);

        // 基类已生成的字段名集合——变体净新增字段名并入查重，避免子类字段 shadow 基类同名字段（CS0108）。与主流程同一命名口径。
        private static HashSet<string> BaseFieldNames(UIBindingData baseData, Transform root, UICodeGenProfile profile)
        {
            var names = new HashSet<string>();
            foreach (var entry in baseData.Entries)
            {
                var types = entry.ComponentTypes ?? new List<string>();
                foreach (var typeId in types)
                {
                    var type = UIBindingUtil.ResolveType(typeId);
                    if (type != null) names.Add(FieldNameFor(entry, type, types.Count, root, profile));
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

        /// <summary>
        /// 采集所有当前可生成的 UI Prefab 输出。候选路径来自会话级增量索引，只重新加载确实含
        /// <see cref="UIBindingData"/> 的 Prefab；Prefab Stage 的未保存覆盖由
        /// <see cref="Generate(string,UIBindingData,UICodeGenProfile)"/> 在写盘前替换同 id 声明。
        /// </summary>
        internal static IReadOnlyList<FrameworkGeneratedOutputClaim> CollectRegisteredOutputClaims()
        {
            if (!UICodeGenProfile.TryResolve(out UICodeGenProfile profile))
                return Array.Empty<FrameworkGeneratedOutputClaim>();

            var claims = new List<FrameworkGeneratedOutputClaim>();
            foreach (string prefabPath in UIBindingPrefabCatalog.GetPaths())
            {
                GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                UIBindingData data = root != null ? root.GetComponent<UIBindingData>() : null;
                if (data == null || data.Entries == null || data.Entries.Count == 0) continue;
                if (TryCreateOutputClaims(prefabPath, data, profile,
                        out IReadOnlyList<FrameworkGeneratedOutputClaim> prefabClaims))
                    claims.AddRange(prefabClaims);
            }

            return claims;
        }

        private static bool TryValidateOutputClaimsBeforeWrite(
            string prefabPath,
            string outputDir,
            string outputDirAbsolute,
            string generatedDir,
            string generatedDirAbsolute,
            string className,
            out string error)
        {
            var claims = new List<FrameworkGeneratedOutputClaim>(CollectRegisteredOutputClaims());
            string claimPrefix = prefabPath.Replace('\\', '/') + ":";
            claims.RemoveAll(claim => claim.ClaimId.StartsWith(claimPrefix, StringComparison.Ordinal));
            claims.AddRange(CreateOutputClaims(
                prefabPath,
                outputDir,
                outputDirAbsolute,
                generatedDir,
                generatedDirAbsolute,
                className));

            return FrameworkGeneratedOutputClaimCatalog.TryValidateBeforeWrite(
                OutputClaimSourceId, claims, out error);
        }

        private static bool TryCreateOutputClaims(
            string prefabPath,
            UIBindingData data,
            UICodeGenProfile profile,
            out IReadOnlyList<FrameworkGeneratedOutputClaim> claims)
        {
            claims = Array.Empty<FrameworkGeneratedOutputClaim>();
            string outputDir = UIBindingUtil.ResolveOutputDir(prefabPath, data, profile);
            string generatedDir = UIBindingUtil.ResolveGeneratedDir(prefabPath, data, profile);
            if (string.IsNullOrWhiteSpace(outputDir) || string.IsNullOrWhiteSpace(generatedDir)) return false;
            if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                    outputDir, out string normalizedOutputDir, out string outputDirAbsolute, out _))
                return false;
            if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                    generatedDir, out string normalizedGeneratedDir, out string generatedDirAbsolute, out _))
                return false;

            string className = UIBindingUtil.ResolveClassName(prefabPath, data, profile);
            claims = CreateOutputClaims(
                prefabPath,
                normalizedOutputDir,
                outputDirAbsolute,
                normalizedGeneratedDir,
                generatedDirAbsolute,
                className);
            return true;
        }

        private static IReadOnlyList<FrameworkGeneratedOutputClaim> CreateOutputClaims(
            string prefabPath,
            string outputDir,
            string outputDirAbsolute,
            string generatedDir,
            string generatedDirAbsolute,
            string className)
        {
            string normalizedPrefabPath = prefabPath.Replace('\\', '/');
            string logicFileName = className + ".cs";
            string nodesFileName = className + ".nodes.g.cs";
            return new[]
            {
                FrameworkGeneratedOutputClaim.ExactFile(
                    normalizedPrefabPath + ":logic",
                    $"UI 绑定【{normalizedPrefabPath}】逻辑骨架",
                    outputDir + "/" + logicFileName,
                    Path.Combine(outputDirAbsolute, logicFileName)),
                FrameworkGeneratedOutputClaim.ExactFile(
                    normalizedPrefabPath + ":nodes",
                    $"UI 绑定【{normalizedPrefabPath}】节点代码",
                    generatedDir + "/" + nodesFileName,
                    Path.Combine(generatedDirAbsolute, nodesFileName)),
            };
        }

        /// <summary>生成并把结果打到 Console，返回 (ok, message)。供菜单 / 根行按钮 / 弹面板 / 自动生成共用。</summary>
        public static (bool ok, string message) GenerateAndLog(string prefabPath, UIBindingData data, UICodeGenProfile profile)
        {
            var result = Generate(prefabPath, data, profile);
            Debug.Log("[UI 绑定] " + result.message);
            return result;
        }

        private static string BuildNodesFile(string ns, string className, string prefabPath, List<Field> fields, string baseClassName,
            List<(string Name, string TypeRef, string Path, bool Dead)> removedFields = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//     由 UIBindingCodeGenerator 从 prefab 节点绑定生成，勿手改；重新生成会覆盖。");
            sb.AppendLine($"//     源 prefab：{prefabPath}");
            if (baseClassName != null)
                sb.AppendLine($"//     变体增量：本类继承 {baseClassName}，仅含基类之外的净新增字段；继承字段由 base.BindNodes() 绑。失效的继承字段见下方 [Obsolete] 遮蔽成员。");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    partial class {className}");
            sb.AppendLine("    {");
            foreach (var f in fields)
                sb.AppendLine($"        protected {UIBindingUtil.CSharpTypeName(f.Type)} {f.Name};");

            // 变体里失效的继承字段：生成 [Obsolete] 遮蔽成员（new 隐藏基类同名字段），把「本变体不该再用此字段」落到字段本身——
            // IDE 悬停可见、用到即编译告警；属性体 base.Name 取基类原字段（不触发本告警）。Dead 区分「运行时为 null」与「基类仍绑但本变体已取消」。
            if (removedFields != null && removedFields.Count > 0)
            {
                sb.AppendLine();
                foreach (var (name, typeRef, path, dead) in removedFields)
                {
                    string note = dead
                        ? $"本变体中节点 \"{path}\" 或其对应组件已失效（删除/改名/移除组件），此继承自 {baseClassName} 的字段运行时为 null"
                        : $"本变体已取消节点 \"{path}\" 此组件的绑定——基类仍会绑定它（值非 null），但本变体不应再用此继承字段";
                    sb.AppendLine($"        /// <summary>{EscapeXml(note)}。要用请在本变体重新绑定，或改建独立窗口。</summary>");
                    sb.AppendLine($"        [global::System.Obsolete(\"{Escape(note)}（请重新绑定或改建独立窗口）\")]"); // global:: 与生成代码其余类型一致、自包含
                    sb.AppendLine($"        protected new {typeRef} {name} => base.{name};");
                }
            }

            sb.AppendLine();
            sb.AppendLine("        protected override void BindNodes()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.BindNodes();");
            foreach (var f in fields)
                sb.AppendLine(f.Type == typeof(GameObject)
                    ? $"            {f.Name} = BindGameObject(\"{Escape(f.Path)}\");"
                    : $"            {f.Name} = BindNode<{UIBindingUtil.CSharpTypeName(f.Type)}>(\"{Escape(f.Path)}\");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string BuildLogicFile(string ns, string className, string location, string baseClassName, string baseNamespace)
        {
            // 变体：继承基窗口类。同命名空间用短名；跨命名空间才用 global:: 全限定（规避命名空间差异）。OnCreated 先 base.OnCreated() 复用基类接线。
            bool isVariant = baseClassName != null;
            string baseTypeRef = !isVariant ? "UGuiWindowBase"
                : (baseNamespace == ns ? baseClassName : $"global::{baseNamespace}.{baseClassName}");

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

        // XML doc 注释按 XML 解析：节点名/路径里的 < > & 会破坏 <summary> 结构（CS1570 警告 + IDE 悬停空白）。
        // 写进 /// 注释前先转义（& 必须先于 < > 替换，否则会二次转义已生成的实体）。
        private static string EscapeXml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
