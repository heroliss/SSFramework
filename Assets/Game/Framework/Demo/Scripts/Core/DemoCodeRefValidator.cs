#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 源码跳转（<see cref="CodeRef"/>）的防腐校验器：静态扫描 demo 全部 <c>.cs</c>，抽出每一处
    /// <c>CodeRef.Here(...)</c> / <c>new CodeRef(...)</c> 的路径与锚点，用<b>与跳转同一套</b>规则
    /// （<see cref="CodeNavigator.ResolveAnchor"/>）验证锚点仍能精准命中真实声明——把「锚点在框架改名后
    /// 静默跳第 1 行」这类只有点下去才发现的腐烂，提前到一条菜单命令里机器抓出。
    /// </summary>
    /// <remarks>
    /// 走<b>静态源码扫描</b>而非运行时反射：<c>CodeRef</c> 是在各模块 <c>Build()</c> 里就地构造的，
    /// 不跑 UI 就拿不到实例，只能从源码文本提取。正则要点见 <see cref="CodeRefCall"/>。
    /// </remarks>
    internal static class DemoCodeRefValidator
    {
        // demo 源码根（相对项目根）。CodeRef 目前都在此树下，路径命中失败会在结果里显式报出。
        private const string DemoScriptsRoot = "Assets/Game/Framework/Demo/Scripts";

        // CodeRef 机制自身的文件：它们含 CodeRef.Here("anchor") / new CodeRef("path") 这类<b>示例</b>语法
        // （注释、正则模式串），不是真实跳转，扫到只会是假阳性——按文件名跳过。
        private static readonly HashSet<string> InfraFiles = new()
        {
            "CodeRef.cs",
            "DemoCodeRefValidator.cs",
        };

        // 一个 C# 字符串字面量：允许 \" 转义。
        private const string StringLit = "\"((?:[^\"\\\\]|\\\\.)*)\"";

        // CodeRef.Here("anchor", ...) —— 目标是调用所在文件自身；anchor 可缺省（跳文件头）。
        private static readonly Regex HereCall = new(
            "CodeRef\\.Here\\s*\\(\\s*(?:" + StringLit + ")?", RegexOptions.Compiled | RegexOptions.Singleline);

        // new CodeRef("path", "anchor", ...) —— 第一串是路径，第二串是 anchor（可缺省）。
        private static readonly Regex NewCall = new(
            "new\\s+CodeRef\\s*\\(\\s*" + StringLit + "\\s*(?:,\\s*" + StringLit + ")?",
            RegexOptions.Compiled | RegexOptions.Singleline);

        [MenuItem("SSFramework/诊断/校验 Demo 源码跳转锚点", priority = 300)]
        public static void Validate()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string scanRoot = Path.Combine(projectRoot, DemoScriptsRoot);
            if (!Directory.Exists(scanRoot))
            {
                Debug.LogError($"[CodeRef 校验] 找不到扫描根目录：{DemoScriptsRoot}");
                return;
            }

            // 目标文件内容缓存：一次校验里同一 .cs 可能被多处引用。
            var textCache = new Dictionary<string, string>();
            string ReadTarget(string absPath) =>
                textCache.TryGetValue(absPath, out var t)
                    ? t
                    : textCache[absPath] = File.Exists(absPath) ? File.ReadAllText(absPath) : null;

            int total = 0, ok = 0;
            var problems = new List<string>();

            foreach (string file in Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (InfraFiles.Contains(Path.GetFileName(file))) continue; // 跳过 CodeRef 机制自身（含示例语法）
                string src = File.ReadAllText(file);
                string relFile = Rel(projectRoot, file);

                foreach (var call in ExtractCalls(src))
                {
                    total++;
                    // 目标文件：Here 指向本文件；new CodeRef 指向其显式路径。
                    string targetAbs = call.Path == null ? file : Path.Combine(projectRoot, call.Path);
                    string targetText = call.Path == null ? src : ReadTarget(targetAbs);

                    if (targetText == null)
                    {
                        problems.Add($"路径失效  {relFile}:{LineOf(src, call.Pos)}  → {call.Path}");
                        continue;
                    }

                    CodeNavigator.ResolveAnchor(targetText, call.Anchor, out var verdict);
                    switch (verdict)
                    {
                        case CodeNavigator.AnchorVerdict.Ok:
                        case CodeNavigator.AnchorVerdict.FileTop:
                            ok++;
                            break;
                        default:
                            problems.Add($"{Zh(verdict)}  {relFile}:{LineOf(src, call.Pos)}  " +
                                         $"anchor='{call.Anchor}'" +
                                         (call.Path != null ? $"  target={call.Path}" : ""));
                            break;
                    }
                }
            }

            if (problems.Count == 0)
            {
                Debug.Log($"[CodeRef 校验] 通过：{total} 处跳转全部精准命中（含 {total - ok} 处有意跳文件头）。");
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[CodeRef 校验] {problems.Count}/{total} 处锚点有问题，跳转会落偏（需更新 CodeRef）：");
                foreach (string p in problems) sb.AppendLine("  · " + p);
                Debug.LogError(sb.ToString());
            }
        }

        // 从一段源码里抽出所有 CodeRef 构造点，按出现位置排序。
        private static IEnumerable<CodeRefCall> ExtractCalls(string src)
        {
            var calls = new List<CodeRefCall>();
            foreach (Match m in HereCall.Matches(src))
                calls.Add(new CodeRefCall { Path = null, Anchor = Unescape(m.Groups[1].Value), Pos = m.Index });
            foreach (Match m in NewCall.Matches(src))
                calls.Add(new CodeRefCall
                {
                    Path = Unescape(m.Groups[1].Value),
                    Anchor = m.Groups[2].Success ? Unescape(m.Groups[2].Value) : null,
                    Pos = m.Index,
                });
            calls.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            return calls;
        }

        // 提取到的字面量内容：Path 为 null 表示 CodeRef.Here（目标=本文件）；Anchor 为 null/空表示跳文件头。
        private struct CodeRefCall
        {
            public string Path;
            public string Anchor;
            public int Pos;
        }

        private static string Unescape(string s) =>
            string.IsNullOrEmpty(s) ? s : s.Replace("\\\"", "\"").Replace("\\\\", "\\");

        private static string Rel(string projectRoot, string absPath) =>
            absPath.Substring(projectRoot.Length).TrimStart('\\', '/').Replace('\\', '/');

        private static int LineOf(string content, int idx)
        {
            int line = 1;
            for (int i = 0; i < idx && i < content.Length; i++)
                if (content[i] == '\n') line++;
            return line;
        }

        private static string Zh(CodeNavigator.AnchorVerdict v) => v switch
        {
            CodeNavigator.AnchorVerdict.NoHit => "锚点未命中（跳第1行）",
            CodeNavigator.AnchorVerdict.OnlyLiteral => "只命中自身字面量（跳调用行）",
            CodeNavigator.AnchorVerdict.CommentHit => "命中注释行",
            _ => v.ToString(),
        };
    }
}
#endif
