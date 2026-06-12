using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.Compilation;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// 热更程序集引用图工具——ADR-0008 引用纪律的机器执行部分：
    /// <list type="bullet">
    ///   <item><see cref="Validate"/>：校验「AOT 程序集不得引用热更程序集」（热更集合须对被引用关系向上封闭），
    ///         违规逐条指出元凶与修法，不靠人脑记纪律。</item>
    ///   <item><see cref="SortByDependency"/>：按引用图对热更集合拓扑排序（被依赖的在前），
    ///         供清单生成加载顺序——人不维护顺序，也就不会排错。</item>
    /// </list>
    ///
    /// 图取自 <see cref="CompilationPipeline.GetAssemblies(AssembliesType)"/> 的 <b>Player</b> 视图：
    /// 编辑器专用与测试程序集天然不在内（不进玩家包，怎么引用都不受热更约束）；
    /// 预编译 DLL（Odin / R3 核心等）没有 asmdef、不在图中——它们也进不了热更列表，无需校验。
    /// </summary>
    internal static class HotUpdateAssemblyGraph
    {
        // 引导自举：Boot 要先跑起来才能加载别人，永远不允许出现在热更列表。
        private const string BootAssemblyName = "Game.Framework.Boot";

        /// <summary>
        /// 校验热更集合合法性。空列表合法（纯 AOT 档位）。
        /// 返回 (是否通过, 人类可读摘要)；通过时摘要附拓扑排序后的加载顺序，便于一眼确认。
        /// </summary>
        internal static (bool ok, string summary) Validate(IReadOnlyCollection<string> hotNames)
        {
            var sb = new StringBuilder();
            var hotSet = new HashSet<string>(hotNames, StringComparer.Ordinal);
            bool ok = true;

            if (hotSet.Count == 0)
                return (true, "热更列表为空：纯 AOT 档位（合法；代码包将只含空清单）。");

            if (hotSet.Contains(BootAssemblyName))
            {
                ok = false;
                sb.AppendLine($"✗ {BootAssemblyName} 不可热更：它是加载热更代码的引导器，必须先于一切热更代码存在（鸡生蛋）。");
            }

            // GetAssemblies(Player) 会把「UNITY_INCLUDE_TESTS / UNITY_EDITOR 约束」的程序集也算进来（编辑器语境两宏恒在），
            // 但真实玩家包没有它们——从图里剔除，否则测试程序集 / 编辑器约束程序集（如 Demo）引用热更内核会被误报违规。
            var player = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                                            .Where(a => !IsEditorConstrained(a.name))
                                            .ToList();
            var playerNames = new HashSet<string>(player.Select(a => a.name), StringComparer.Ordinal);

            foreach (var name in hotSet.Where(n => !playerNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal))
            {
                ok = false;
                sb.AppendLine($"✗ '{name}' 不在玩家编译图中：编辑器专用 / 测试程序集进不了包，谈不上热更；或者程序集名写错。");
            }

            // 铁律检查：每个 AOT（不在热更集合）玩家程序集，不得引用热更集合内的程序集。
            var violations = new List<string>();
            foreach (var asm in player)
            {
                if (hotSet.Contains(asm.name)) continue;
                foreach (var dep in asm.assemblyReferences)
                    if (hotSet.Contains(dep.name))
                        violations.Add($"{asm.name}（AOT）→ {dep.name}（热更）");
            }

            if (violations.Count > 0)
            {
                ok = false;
                sb.AppendLine($"✗ AOT 引用热更（{violations.Count} 处）——把引用方也加入热更列表，或把被引用方移出：");
                foreach (var v in violations.OrderBy(v => v, StringComparer.Ordinal))
                    sb.AppendLine("    " + v);
                // Assembly-CSharp 的引用边几乎都是「autoReferenced 隐式引用」而非真实使用——
                // 散落脚本 / 工具生成的无 asmdef 代码会自动引用一切 autoReferenced:true 的程序集。
                if (violations.Any(v => v.StartsWith("Assembly-CSharp", StringComparison.Ordinal)))
                    sb.AppendLine("    ↳ Assembly-CSharp 的引用通常来自散落脚本/生成文件的隐式自动引用：" +
                                  "把被引用的热更程序集 asmdef 设 autoReferenced:false（热更程序集应一律如此），" +
                                  "真要在散落脚本里用框架则把脚本收进引用了框架的 asmdef。");
            }

            if (ok)
                sb.AppendLine($"✓ 校验通过：{hotSet.Count} 个热更程序集，加载顺序（自动拓扑排序）：{string.Join(" → ", SortByDependency(hotSet))}");
            return (ok, sb.ToString().TrimEnd());
        }

        /// <summary>
        /// 把热更集合按引用图拓扑排序（被依赖的在前），即引导器的 <c>Assembly.Load</c> 顺序。
        /// 同层级按字母序保证结果确定（构建可复现）。不在玩家编译图中的名字原样附在末尾
        /// （<see cref="Validate"/> 会把它们报为错误，这里只保证「输入全集都在输出里」不丢条目）。
        /// </summary>
        internal static List<string> SortByDependency(IReadOnlyCollection<string> hotNames)
        {
            var hotSet = new HashSet<string>(hotNames, StringComparer.Ordinal);
            var asms = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                                          .Where(a => hotSet.Contains(a.name))
                                          .ToList();

            // depCount[X] = X 依赖的热更程序集数；dependents[Y] = 依赖 Y 的热更程序集（Y 先加载，随后才能解锁它们）。
            var depCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var a in asms)
            {
                depCount[a.name] = 0;
                dependents[a.name] = new List<string>();
            }
            foreach (var a in asms)
            {
                foreach (var dep in a.assemblyReferences)
                {
                    if (!depCount.ContainsKey(dep.name) || dep.name == a.name) continue;
                    depCount[a.name]++;
                    dependents[dep.name].Add(a.name);
                }
            }

            var ready = new SortedSet<string>(
                depCount.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
            var result = new List<string>(asms.Count);
            while (ready.Count > 0)
            {
                var name = ready.Min;
                ready.Remove(name);
                result.Add(name);
                foreach (var dependent in dependents[name])
                    if (--depCount[dependent] == 0)
                        ready.Add(dependent);
            }

            // Unity 禁止 asmdef 循环引用，正常走不到「有剩余」；不在编译图中的名字兜底附加，保证不丢条目。
            foreach (var name in hotNames)
                if (!result.Contains(name))
                    result.Add(name);
            return result;
        }

        // 带 UNITY_INCLUDE_TESTS（测试）或 UNITY_EDITOR（编辑器专用，如 Demo）约束的程序集进不了真实玩家包。
        // 注意编辑器专用程序集应当用 defineConstraints 而非 includePlatforms:["Editor"]——后者会把场景里挂的
        // MonoBehaviour 在 Play 模式下直接剔成 missing（编辑器平台程序集不参与场景运行时序列化）。
        // Assembly 对象不暴露 defineConstraints，回到 asmdef JSON 解析（找不到 asmdef 的如 Assembly-CSharp 视为无约束）。
        private static bool IsEditorConstrained(string assemblyName)
        {
            string path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                var dto = JsonUtility.FromJson<AsmdefConstraintsJson>(File.ReadAllText(path));
                if (dto?.defineConstraints == null) return false;
                return dto.defineConstraints.Contains("UNITY_INCLUDE_TESTS") ||
                       dto.defineConstraints.Contains("UNITY_EDITOR");
            }
            catch (Exception)
            {
                return false; // JSON 异常按无约束处理：宁可多校验，不漏校验。
            }
        }

        [Serializable]
        private sealed class AsmdefConstraintsJson
        {
            public string[] defineConstraints;
        }
    }
}
