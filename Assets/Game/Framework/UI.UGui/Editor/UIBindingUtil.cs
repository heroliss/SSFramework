using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>UI 节点绑定工具的辅助：类型 id 互转、节点路径、字段名清洗、默认组件挑选、自定义脚本判定，以及根上 <see cref="UIBindingData"/> 的取/加。</summary>
    public static class UIBindingUtil
    {
        // ───────────── 绑定数据组件（prefab 根上）存取 ─────────────

        /// <summary>取根上的绑定数据组件（无则 null）。</summary>
        public static UIBindingData GetData(GameObject root) => root != null ? root.GetComponent<UIBindingData>() : null;

        /// <summary>取根上的绑定数据组件，没有就经 <c>Undo</c> 加一个（可撤销、并把 Stage 标脏）。</summary>
        public static UIBindingData GetOrAddData(GameObject root)
        {
            var data = root.GetComponent<UIBindingData>();
            if (data == null) data = Undo.AddComponent<UIBindingData>(root);
            return data;
        }

        /// <summary>读 prefab 资产根上的绑定数据组件——给场景实例的只读徽标用（按资产路径加载，Unity 内部缓存）。</summary>
        public static UIBindingData LoadAssetData(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            return go != null ? go.GetComponent<UIBindingData>() : null;
        }

        // ───────────── 生成目标解析（覆盖链：本 prefab 覆盖 → 目录配置链 → 全工程 Profile）。生成器与 GUI 共用，避免口径漂移。 ─────────────
        // 逐字段独立解析：prefab 上对应 override 非空即用；否则按 prefab 所在目录向上取最近一个设了该字段的 UICodeGenDirConfig；
        // 都没有再退到全工程 Profile。约定类设置不走此链、恒由 Profile 提供。

        /// <summary>「生成目标」四值的标识，用于覆盖链与占位符解析的逐字段分发。</summary>
        public enum GenTargetField { Namespace, OutputDir, GeneratedDir, FileName }

        public static string ResolveNamespace(string prefabPath, UIBindingData data, UICodeGenProfile profile)
            => ApplyTokens(ResolveRaw(GenTargetField.Namespace, prefabPath, data, profile), prefabPath, GenTargetField.Namespace);

        public static string ResolveOutputDir(string prefabPath, UIBindingData data, UICodeGenProfile profile)
            => ApplyTokens(ResolveRaw(GenTargetField.OutputDir, prefabPath, data, profile), prefabPath, GenTargetField.OutputDir);

        public static string ResolveGeneratedDir(string prefabPath, UIBindingData data, UICodeGenProfile profile)
            => ApplyTokens(ResolveRaw(GenTargetField.GeneratedDir, prefabPath, data, profile), prefabPath, GenTargetField.GeneratedDir);

        /// <summary>解析最终生成的类名 / 文件名（= 文件名模板覆盖链解析 + 占位符展开 + 标识符清洗）。逻辑/绑定两 partial 与类名共用此名。</summary>
        public static string ResolveClassName(string prefabPath, UIBindingData data, UICodeGenProfile profile)
            => ApplyTokens(ResolveRaw(GenTargetField.FileName, prefabPath, data, profile), prefabPath, GenTargetField.FileName);

        // 覆盖链取「原始模板串」（未展开占位符）：prefab 覆盖非空 → 否则目录配置链最近一个设了该字段的 → 否则 Profile 默认。
        private static string ResolveRaw(GenTargetField field, string prefabPath, UIBindingData data, UICodeGenProfile profile)
        {
            string ovr = data == null ? null : field switch
            {
                GenTargetField.Namespace => data.NamespaceOverride,
                GenTargetField.OutputDir => data.OutputDirOverride,
                GenTargetField.GeneratedDir => data.GeneratedDirOverride,
                _ => data.FileNameOverride,
            };
            if (!string.IsNullOrWhiteSpace(ovr)) return ovr.Trim();
            return ResolveInheritedRaw(field, prefabPath, profile).value;
        }

        // 跳过 prefab 覆盖、走目录配置链 → Profile，取原始模板串 + 来源描述。
        private static (string value, string source) ResolveInheritedRaw(GenTargetField field, string prefabPath, UICodeGenProfile profile)
        {
            foreach (var cfg in UICodeGenDirConfig.ChainFor(prefabPath))
            {
                string v = RawFieldValue(cfg, field);
                if (v != null) return (v, $"继承自目录配置 {AssetDatabase.GetAssetPath(cfg)}");
            }
            return (RawFieldValue(profile, field), "全工程默认 (UICodeGenProfile)");
        }

        /// <summary>某目录配置上该字段设定的原始模板（未设则 <c>null</c>）。</summary>
        internal static string RawFieldValue(UICodeGenDirConfig cfg, GenTargetField field) => field switch
        {
            GenTargetField.Namespace => cfg.NamespaceOrNull,
            GenTargetField.OutputDir => cfg.OutputDirOrNull,
            GenTargetField.GeneratedDir => cfg.GeneratedDirOrNull,
            _ => cfg.FileNameOrNull,
        };

        /// <summary>全工程 Profile 上该字段的根默认（原始模板）。</summary>
        internal static string RawFieldValue(UICodeGenProfile profile, GenTargetField field) => field switch
        {
            GenTargetField.Namespace => profile.NamespaceRoot,
            GenTargetField.OutputDir => profile.OutputCodeDir,
            GenTargetField.GeneratedDir => profile.GeneratedCodeDir,
            _ => profile.FileNameTemplate,
        };

        /// <summary>
        /// 目录配置 Inspector 专用：该字段「继承来源」的<b>原始模板</b> + 来源描述 + 来源资产（供点击跳转）。
        /// 从本配置所在目录的<b>父目录</b>起向上找首个设了该字段的目录配置，否则全工程 Profile。
        /// 刻意不展开占位符——目录配置面向其下所有 prefab、无单一 prefab 上下文（展开会把 {PrefabName} 错解析成配置自身文件名）。
        /// </summary>
        public static (string raw, string source, UnityEngine.Object sourceObj) DirConfigInheritedRaw(
            UICodeGenDirConfig self, GenTargetField field, UICodeGenProfile profile)
        {
            foreach (var cfg in UICodeGenDirConfig.ChainFor(SelfFolder(self)))
            {
                string v = RawFieldValue(cfg, field);
                if (v != null) return (v, $"继承自目录配置 {AssetDatabase.GetAssetPath(cfg)}", cfg);
            }
            return (RawFieldValue(profile, field), "全工程默认 (UICodeGenProfile)", profile);
        }

        /// <summary>目录配置的「父配置」：最近的祖先目录配置；没有则全工程 Profile。供 Inspector 顶部只读跳转。</summary>
        public static UnityEngine.Object DirConfigParent(UICodeGenDirConfig self, UICodeGenProfile profile)
        {
            var chain = UICodeGenDirConfig.ChainFor(SelfFolder(self));
            return chain.Count > 0 ? (UnityEngine.Object)chain[0] : profile;
        }

        // 目录配置所在目录。传给 ChainFor 即从其【父目录】起算 → 严格祖先（排除配置自身所在目录），故拿到的是「父配置」而非自己。
        private static string SelfFolder(UICodeGenDirConfig self)
        {
            string p = AssetDatabase.GetAssetPath(self);
            return string.IsNullOrEmpty(p) ? string.Empty : (System.IO.Path.GetDirectoryName(p) ?? string.Empty).Replace('\\', '/');
        }

        /// <summary>
        /// 展开模板里的占位符：<c>{PrefabName}</c> = prefab 文件名、<c>{DirectoryName}</c> = prefab 所在目录名、
        /// <c>{ParentDirectoryName}</c> = 其父目录名。命名空间字段整体按 <c>.</c> 分段逐段清洗成合法标识符（模板写死的字面量段
        /// 与占位符值一视同仁，故 <c>My-Module.{DirectoryName}</c> 这类也安全；含 <c>.</c> 的目录名自然成多级命名空间，空段丢弃）；
        /// 文件名字段整体清洗成合法标识符（= 类名）；目录字段按字面拼。
        /// </summary>
        public static string ApplyTokens(string template, string prefabPath, GenTargetField field)
        {
            if (string.IsNullOrEmpty(template)) return template;

            string norm = prefabPath?.Replace('\\', '/') ?? string.Empty;
            string prefabName = System.IO.Path.GetFileNameWithoutExtension(norm);
            var segs = norm.Split('/');                                  // [...folders..., file.ext]
            string dirName = segs.Length >= 2 ? segs[segs.Length - 2] : string.Empty;       // 当前目录名
            string parentDirName = segs.Length >= 3 ? segs[segs.Length - 3] : string.Empty; // 父目录名

            string s = template
                .Replace("{PrefabName}", prefabName)
                .Replace("{DirectoryName}", dirName)
                .Replace("{ParentDirectoryName}", parentDirName);

            return field switch
            {
                GenTargetField.Namespace => SanitizeNamespace(s), // 整体按段清洗，含模板字面量段
                GenTargetField.FileName => SanitizeIdentifier(s), // 文件名 = 类名，整体须为合法标识符
                _ => s,                                           // 目录：按字面拼（路径段允许空格/横杠等）
            };
        }

        /// <summary>把任意串清洗成合法命名空间：按 <c>.</c> 分段、逐段 <see cref="SanitizeIdentifier"/>、丢弃空段后用 <c>.</c> 重接。</summary>
        /// <remarks>模板字面量段（如 <c>My-Module</c>）与占位符值同样被清洗；占位符解析为空或连续 <c>.</c> 产生的空段被跳过（避免 <c>Foo._.Bar</c>）；全空则兜底为 <c>_</c>，保证产出非空合法命名空间。</remarks>
        public static string SanitizeNamespace(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "_";
            var sb = new StringBuilder(raw.Length);
            foreach (var seg in raw.Split('.'))
            {
                if (string.IsNullOrEmpty(seg)) continue;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(SanitizeIdentifier(seg));
            }
            return sb.Length == 0 ? "_" : sb.ToString();
        }

        // ───────────── 变体基解析（阶段②）。 ─────────────

        /// <summary>
        /// 若 <paramref name="prefabPath"/> 是预制体变体、且其直接基 prefab 根上有带条目的 <see cref="UIBindingData"/>，
        /// 取出基信息供变体增量生成（子类继承基窗口类、只产出净新增字段）。非变体 / 基无绑定 → 返回 false（按独立窗口生成）。
        /// </summary>
        /// <remarks>
        /// 用 Unity 原生变体关系（<see cref="PrefabUtility.GetCorrespondingObjectFromSource"/> 拿直接基）——多级变体链天然按「对直接基做差集」逐级继承。
        /// 基条目从基 prefab 资产读（只读），与变体自身的活条目分开取，差集才稳定。
        /// </remarks>
        public static bool TryResolveVariantBase(string prefabPath, UICodeGenProfile profile,
            out UIBindingData baseData, out string baseClassName, out string baseNamespace)
        {
            baseData = null; baseClassName = null; baseNamespace = null;

            var variantAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (variantAsset == null || PrefabUtility.GetPrefabAssetType(variantAsset) != PrefabAssetType.Variant)
                return false;

            var baseObj = PrefabUtility.GetCorrespondingObjectFromSource(variantAsset);
            string basePath = baseObj != null ? AssetDatabase.GetAssetPath(baseObj) : null;
            if (string.IsNullOrEmpty(basePath)) return false;

            var data = LoadAssetData(basePath);
            if (data == null || data.Entries == null || data.Entries.Count == 0) return false;

            baseData = data;
            baseClassName = ResolveClassName(basePath, data, profile); // 基类名同样走文件名模板 + 占位符，变体继承引用才对得上
            baseNamespace = ResolveNamespace(basePath, data, profile);
            return true;
        }

        /// <summary>组件类型 → 稳定可解析的 id（<c>FullName, AssemblyName</c>，不含版本/区域/公钥）。</summary>
        public static string TypeId(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;

        // 类型 id → Type 记忆化：徽标/清单每帧多次解析同一批 id，Type.GetType 是程序集级查找、别逐次跑。
        // id→Type 不可变；新类型必经重编译→域重载，静态字典随之重建，故缓存（含 null 结果）始终有效。
        private static readonly Dictionary<string, Type> _typeCache = new();

        /// <summary>id → 类型（编辑器域里类型未加载则返回 null）。结果缓存，避免逐帧重复 <see cref="Type.GetType(string)"/> 的程序集扫描。</summary>
        public static Type ResolveType(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_typeCache.TryGetValue(id, out var t)) return t;
            return _typeCache[id] = Type.GetType(id);
        }

        /// <summary>生成代码里用的类型引用——全限定 + <c>global::</c> 前缀，彻底规避命名空间冲突（含 <c>Game.Framework.System</c> 陷阱）。</summary>
        public static string CSharpTypeName(Type t) => "global::" + t.FullName.Replace('+', '.');

        /// <summary>是否用户自定义脚本（非 Unity/TMPro 内置）——标记节点时这类组件优先级最高。</summary>
        public static bool IsCustomScript(Type t)
        {
            string ns = t.Namespace ?? string.Empty;
            return !(ns == "UnityEngine" || ns.StartsWith("UnityEngine.")
                  || ns == "Unity" || ns.StartsWith("Unity.")
                  || ns == "TMPro" || ns.StartsWith("TMPro.")
                  || ns.StartsWith("UnityEditor"));
        }

        /// <summary>
        /// 标记节点时挑默认组件：① 第一个自定义脚本（最可能是要引用的子 View / 自定义控件）；
        /// ② 否则按内置优先级列表（按 <c>FullName</c> 或简单名匹配）；③ 仍没有则兜底节点自身的 Transform。
        /// </summary>
        public static Component PickDefaultComponent(GameObject node, IReadOnlyList<string> builtinPriority)
        {
            var comps = node.GetComponents<Component>().Where(c => c != null).ToList();

            var custom = comps.FirstOrDefault(c => IsCustomScript(c.GetType()));
            if (custom != null) return custom;

            if (builtinPriority != null)
                foreach (string name in builtinPriority)
                {
                    var hit = comps.FirstOrDefault(c => c.GetType().FullName == name || c.GetType().Name == name);
                    if (hit != null) return hit;
                }

            return node.transform;
        }

        /// <summary>计算 <paramref name="node"/> 相对 <paramref name="root"/> 的路径（root 自身 = 空串）；不在 root 子树下返回 false。</summary>
        public static bool TryGetNodePath(Transform root, Transform node, out string path)
        {
            path = null;
            if (node == root) { path = string.Empty; return true; }

            var segments = new List<string>();
            var t = node;
            while (t != null && t != root)
            {
                segments.Add(t.name);
                t = t.parent;
            }
            if (t != root) return false; // 不是 root 的后代

            segments.Reverse();
            path = string.Join("/", segments);
            return true;
        }

        /// <summary>
        /// 以每条绑定的节点引用（<see cref="UIBindingEntry.Node"/>）为准，把清单对齐到活节点树——「Hierarchy 与 UIBindingData 实时一致」的核心：
        /// <list type="bullet">
        ///   <item>引用非空：节点还在（可能被改名/移动）→ 按引用重算并更新 <see cref="UIBindingEntry.Path"/>，<b>绝不删除</b>。</item>
        ///   <item>引用为空但旧路径仍解析得到：历史条目（引用字段后加）/ 根自身 → 补上引用（迁移），不删。</item>
        ///   <item>引用为空且旧路径也解析不到：节点已被删 → 清理该条失效绑定。</item>
        /// </list>
        /// 只认「引用为空 + 路径无」判删除，故改名（引用仍在）绝不会被误清；引用与 <c>root.Find</c> 都反映当前真实树、非过期快照，
        /// 不会重演 instanceId 锚过期导致的整批误删。返回是否发生改写/清理（调用方据此标脏）。
        /// </summary>
        public static bool ReconcilePaths(UIBindingData data, Transform root)
        {
            if (data == null || root == null) return false;
            bool changed = false;
            for (int i = data.Entries.Count - 1; i >= 0; i--)
            {
                var e = data.Entries[i];
                if (e.Node != null)
                {
                    if (TryGetNodePath(root, e.Node, out string p) && e.Path != p) { e.Path = p; changed = true; } // 改名/移动 → 路径跟随
                }
                else
                {
                    var byPath = string.IsNullOrEmpty(e.Path) ? root : root.Find(e.Path);
                    // 历史条目迁移（引用字段是后加的）/ 根自身：按路径补回引用，仅首次对齐生效（之后 Node 已钉死走上面分支）。
                    // 极端下若原节点已删、另一同路径节点顶替，会绑到新节点——但同一树内路径唯一，且迁移只发生一次，可接受。
                    if (byPath != null) { e.Node = byPath; changed = true; }
                    else { data.Entries.RemoveAt(i); changed = true; } // 引用空 + 路径也解析不到 → 节点确已删，清理失效绑定
                }
            }
            return changed;
        }

        /// <summary>把任意名字清洗成合法 C# 标识符（非法字符换 <c>_</c>，数字开头补前缀 <c>_</c>）。</summary>
        public static string SanitizeIdentifier(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "_";
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        /// <summary>字段基名：取路径最后一段（root 自身取 root 名），清成合法标识符。生成器与 Hierarchy 装饰器共用。</summary>
        public static string DeriveBaseName(string path, string rootName)
        {
            if (string.IsNullOrEmpty(path)) return SanitizeIdentifier(rootName);
            int slash = path.LastIndexOf('/');
            return SanitizeIdentifier(slash >= 0 ? path.Substring(slash + 1) : path);
        }

        // ───────────── 字段命名（模板 + 别名 + 已含省略）。生成器用。 ─────────────

        /// <summary>组件类型 → 别名（Profile 映射表，按简单名/全名匹配；未配退回组件 <c>Name</c>）。</summary>
        public static string ComponentAliasOf(Type t, UICodeGenProfile profile)
        {
            var map = profile.ComponentAliases;
            if (map != null)
                foreach (var a in map)
                    if (a != null && !string.IsNullOrEmpty(a.Alias) && (a.Component == t.Name || a.Component == t.FullName))
                        return a.Alias;
            return t.Name;
        }

        /// <summary>
        /// 按 Profile 模板生成字段名：<paramref name="nodeLeafName"/> = 节点路径最后一段，<paramref name="componentType"/> = 要绑的组件。
        /// 占位符 {node}/{Node}/{Component}/{component}/{alias}/{Alias}/{ALIAS}；开了「已含省略」且节点名已含组件名/别名则组件占位符置空。
        /// 结果去掉占位符置空残留的首尾分隔符后清成合法标识符。
        /// </summary>
        public static string BuildFieldName(string nodeLeafName, Type componentType, UICodeGenProfile profile)
        {
            string node = SanitizeIdentifier(nodeLeafName);
            string comp = componentType.Name;
            string alias = ComponentAliasOf(componentType, profile);

            bool omit = profile.OmitComponentTokenWhenContained &&
                (node.IndexOf(comp, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 node.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0);

            string Tok(string s) => omit ? string.Empty : s;
            string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
            string Low(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

            string raw = (profile.FieldNameTemplate ?? "{node}")
                .Replace("{node}", node)
                .Replace("{Node}", Cap(node))
                .Replace("{Component}", Tok(comp))
                .Replace("{component}", Tok(Low(comp)))
                .Replace("{Alias}", Tok(Cap(alias)))
                .Replace("{alias}", Tok(alias.ToLowerInvariant()))
                .Replace("{ALIAS}", Tok(alias.ToUpperInvariant()))
                .Trim('_', ' ');

            return SanitizeIdentifier(string.IsNullOrEmpty(raw) ? node : raw);
        }

        /// <summary>
        /// 一条绑定的某个组件最终生成的字段名——生成器与 Inspector 展示<b>同一口径</b>（共用本方法，避免「显示的名」与「实际生成的名」漂移）。
        /// 手设了字段名：直接用（多组件时各自加组件名后缀去重）；没设：按节点路径末段 + 模板推导。
        /// </summary>
        public static string EffectiveFieldName(UIBindingEntry entry, Type type, int typeCount, string rootName, UICodeGenProfile profile)
        {
            if (!string.IsNullOrEmpty(entry.FieldName))
            {
                string b = SanitizeIdentifier(entry.FieldName);
                return typeCount > 1 ? b + type.Name : b;
            }
            string path = entry.Path ?? string.Empty;
            string leaf = string.IsNullOrEmpty(path) ? rootName
                : (path.LastIndexOf('/') >= 0 ? path.Substring(path.LastIndexOf('/') + 1) : path);
            return BuildFieldName(leaf, type, profile);
        }

        /// <summary>一条绑定生成的全部字段名（每个组件一个；类型解析不出的占位 <c>?</c>）。给 Inspector 统一展示用。</summary>
        public static List<string> EffectiveFieldNames(UIBindingEntry entry, string rootName, UICodeGenProfile profile)
        {
            var names = new List<string>();
            var types = entry.ComponentTypes;
            int typeCount = types != null ? types.Count : 0;
            if (types != null)
                foreach (var id in types)
                {
                    var type = ResolveType(id);
                    names.Add(type != null ? EffectiveFieldName(entry, type, typeCount, rootName, profile) : "?");
                }
            return names;
        }

        /// <summary>一份绑定清单里「会重名的字段名」集合（≥2 条 entry 生成同名字段）——编辑器据此做重名提示。空集 = 无重名。</summary>
        public static HashSet<string> DuplicateFieldNames(IEnumerable<UIBindingEntry> entries, string rootName, UICodeGenProfile profile)
        {
            var counts = new Dictionary<string, int>();
            foreach (var e in entries)
                foreach (var n in EffectiveFieldNames(e, rootName, profile))
                    counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
            var dups = new HashSet<string>();
            foreach (var kv in counts)
                if (kv.Value > 1) dups.Add(kv.Key);
            return dups;
        }

        /// <summary>类型链上是否有名为 <c>MonoViewBase</c> 的基类（框架 View 基类）——名称判定，避开对运行时程序集的硬引用。</summary>
        public static bool IsViewScript(Type t)
        {
            for (var b = t; b != null && b != typeof(object); b = b.BaseType)
                if (b.Name == "MonoViewBase") return true;
            return false;
        }

        /// <summary>类型链上是否有名为 <c>UGuiWindowBase</c> 的基类（UGUI 窗口基类）——名称判定，避开对 autoReferenced:false 运行时程序集的硬引用。</summary>
        public static bool IsWindowScript(Type t)
        {
            for (var b = t; b != null && b != typeof(object); b = b.BaseType)
                if (b.Name == "UGuiWindowBase") return true;
            return false;
        }

        /// <summary>
        /// 运行实例已无 prefab 连接：靠窗口脚本类名找回它的 prefab 资产路径（生成约定 prefab 文件名 = 窗口类名）。找不到返回 null。
        /// 仅在弹窗打开等低频时机调用——内含 <see cref="AssetDatabase.FindAssets(string)"/>，别放进每帧的 GUI 回调里。
        /// </summary>
        public static string ResolveRuntimePrefabPath(GameObject root)
        {
            if (root == null) return null;
            foreach (var comp in root.GetComponents<Component>())
            {
                if (comp == null || !IsWindowScript(comp.GetType())) continue;
                string name = comp.GetType().Name;
                foreach (var guid in AssetDatabase.FindAssets(name + " t:GameObject"))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (System.IO.Path.GetFileNameWithoutExtension(p) == name) return p;
                }
            }
            return null;
        }

        /// <summary>
        /// <paramref name="node"/> 是否落在某个「子 View 脚本」内部——即 root 与 node 之间存在带 <c>MonoViewBase</c> 的中间祖先。
        /// 用于树状边界校验：父窗口不该跨进子 View 子树抓孙节点，应改为引用那个子 View 本身。
        /// </summary>
        public static bool IsInsideSubView(Transform root, Transform node, out string ownerName)
        {
            ownerName = null;
            var t = node.parent;
            while (t != null && t != root)
            {
                foreach (var c in t.GetComponents<Component>())
                    if (c != null && IsViewScript(c.GetType())) { ownerName = c.GetType().Name; return true; }
                t = t.parent;
            }
            return false;
        }
    }
}
