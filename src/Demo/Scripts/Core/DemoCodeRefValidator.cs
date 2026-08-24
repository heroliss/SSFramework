#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 源码跳转（<see cref="CodeRef"/>）的防腐校验器：扫描所有真实构造点，并用跳转时同一套
    /// <see cref="CodeNavigator.ResolveAnchor"/> 规则验证路径与锚点。
    /// </summary>
    /// <remarks>
    /// 菜单只负责展示结果；源码提取与校验报告都有结构化接口供 EditMode 门禁直接断言，避免“日志显示成功，
    /// 实际却漏扫一种写法”。提取器理解注释/字符串边界、文件内 <c>const string</c> 和字符串拼接，
    /// 覆盖 <c>CodeRef.Here</c>、显式/全限定构造，以及变量、属性和返回表达式中的常见
    /// target-typed <c>new(...)</c>。
    /// </remarks>
    internal static class DemoCodeRefValidator
    {
        private const string DemoScriptsRoot = "Assets/Game/Framework/Demo/Scripts";

        // 机制自身包含真实的工厂构造和示例语法，但它们不是教程 UI 上的跳转链接。
        private static readonly HashSet<string> InfraFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "CodeRef.cs",
            "DemoCodeRefValidator.cs",
        };

        [MenuItem("SSFramework/诊断/校验 Demo 源码跳转锚点", priority = 300)]
        public static void Validate()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var report = ValidateProject(projectRoot);
            if (report.Problems.Count == 0)
            {
                Debug.Log($"[CodeRef 校验] 通过：{report.Total} 处跳转全部精准命中（含 {report.FileTop} 处有意跳文件头）。");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[CodeRef 校验] {report.Problems.Count}/{report.Total} 处跳转有问题（需更新 CodeRef）：");
            foreach (string problem in report.Problems) sb.AppendLine("  · " + problem);
            Debug.LogError(sb.ToString());
        }

        internal static DemoCodeRefValidationReport ValidateProject(string projectRoot)
        {
            string scanRoot = Path.Combine(projectRoot, DemoScriptsRoot);
            if (!Directory.Exists(scanRoot))
            {
                return new DemoCodeRefValidationReport(
                    0,
                    0,
                    0,
                    new[] { $"找不到扫描根目录：{DemoScriptsRoot}" });
            }

            var textCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string ReadTarget(string assetPath) =>
                textCache.TryGetValue(assetPath, out string text)
                    ? text
                    : textCache[assetPath] = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath)?.text;

            int total = 0;
            int precise = 0;
            int fileTop = 0;
            var problems = new List<string>();

            foreach (string file in Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (InfraFiles.Contains(Path.GetFileName(file))) continue;

                string source = File.ReadAllText(file);
                string relativeFile = Rel(projectRoot, file);
                var scan = DemoCodeRefSourceScanner.Scan(source);

                foreach (var issue in scan.Issues)
                {
                    total++;
                    problems.Add($"无法解析  {relativeFile}:{LineOf(source, issue.Position)}  {issue.Message}");
                }

                foreach (var call in scan.Calls)
                {
                    total++;
                    string targetText = call.Path == null ? source : ReadTarget(call.Path);
                    if (targetText == null)
                    {
                        problems.Add($"源码不可打开  {relativeFile}:{LineOf(source, call.Position)}  → {call.Path}" +
                                     "（CodeNavigator 只支持 Assets 下可导入的 MonoScript）");
                        continue;
                    }

                    CodeNavigator.ResolveAnchor(targetText, call.Anchor, out var verdict);
                    switch (verdict)
                    {
                        case CodeNavigator.AnchorVerdict.Ok:
                            precise++;
                            break;
                        case CodeNavigator.AnchorVerdict.FileTop:
                            fileTop++;
                            break;
                        default:
                            problems.Add($"{Zh(verdict)}  {relativeFile}:{LineOf(source, call.Position)}  " +
                                         $"anchor='{call.Anchor}'" +
                                         (call.Path != null ? $"  target={call.Path}" : string.Empty));
                            break;
                    }
                }
            }

            return new DemoCodeRefValidationReport(total, precise, fileTop, problems);
        }

        private static string Rel(string projectRoot, string absolutePath) =>
            absolutePath.Substring(projectRoot.Length).TrimStart('\\', '/').Replace('\\', '/');

        private static int LineOf(string content, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < content.Length; i++)
                if (content[i] == '\n') line++;
            return line;
        }

        private static string Zh(CodeNavigator.AnchorVerdict verdict) => verdict switch
        {
            CodeNavigator.AnchorVerdict.NoHit => "锚点未命中（跳第1行）",
            CodeNavigator.AnchorVerdict.OnlyLiteral => "锚点只命中字符串/字符字面量",
            CodeNavigator.AnchorVerdict.CommentHit => "锚点只命中注释",
            CodeNavigator.AnchorVerdict.Ambiguous => "锚点命中多处真实代码（需取更独特片段）",
            _ => verdict.ToString(),
        };
    }

    /// <summary>一次项目级 CodeRef 校验的结构化结果。</summary>
    internal sealed class DemoCodeRefValidationReport
    {
        internal DemoCodeRefValidationReport(int total, int precise, int fileTop, IReadOnlyList<string> problems)
        {
            Total = total;
            Precise = precise;
            FileTop = fileTop;
            Problems = problems;
        }

        internal int Total { get; }
        internal int Precise { get; }
        internal int FileTop { get; }
        internal IReadOnlyList<string> Problems { get; }
    }

    /// <summary>
    /// 从单个 C# 文件提取 CodeRef 构造点。它不是完整编译器，只实现 Demo 约定允许的窄语法面；
    /// 无法静态求值时会产出显式 issue，而不是静默漏过。
    /// </summary>
    internal static class DemoCodeRefSourceScanner
    {
        private static readonly Regex HereCall = new(
            @"\bCodeRef\s*\.\s*Here\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex ExplicitNew = new(
            @"\bnew\s+(?:(?:global::)?(?:@?[A-Za-z_]\w*\.)*)CodeRef\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex TargetTypedVariable = new(
            @"\bCodeRef\s+@?[A-Za-z_]\w*\s*=",
            RegexOptions.Compiled);

        private static readonly Regex TargetTypedAutoProperty = new(
            @"\bCodeRef\s+@?[A-Za-z_]\w*\s*\{[^{}]*\}\s*=",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex TargetTypedExpressionBody = new(
            @"\bCodeRef\s+@?[A-Za-z_]\w*\s*(?:\([^;{}]*\))?\s*=>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex CodeRefReturningBlock = new(
            @"\bCodeRef\s+@?[A-Za-z_]\w*\s*\([^;{}]*\)\s*\{",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex CodeRefReturningPropertyBlock = new(
            @"\bCodeRef\s+@?[A-Za-z_]\w*\s*\{",
            RegexOptions.Compiled);

        private static readonly Regex TargetTypedNewToken = new(
            @"\bnew\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex ReturnToken = new(
            @"\breturn\b",
            RegexOptions.Compiled);

        private static readonly Regex ConstString = new(
            @"\bconst\s+string\s+(?<name>@?[A-Za-z_]\w*)\s*=",
            RegexOptions.Compiled);

        internal static CodeRefSourceScanResult Scan(string source)
        {
            source ??= string.Empty;
            var lexicalMap = CSharpLexicalMap.Create(source);
            string codeOnly = lexicalMap.CreateCodeOnlyText();
            var constants = ExtractStringConstants(source, codeOnly, lexicalMap);
            var sites = new List<CallSite>();

            AddSites(sites, HereCall, codeOnly, CallKind.Here);
            AddSites(sites, ExplicitNew, codeOnly, CallKind.ExplicitNew);
            AddTargetTypedSites(sites, codeOnly, lexicalMap);
            sites.Sort((left, right) => left.Position.CompareTo(right.Position));
            for (int i = sites.Count - 1; i > 0; i--)
                if (sites[i].OpenParen == sites[i - 1].OpenParen) sites.RemoveAt(i);

            var calls = new List<CodeRefCall>(sites.Count);
            var issues = new List<CodeRefScanIssue>();
            foreach (var site in sites)
            {
                if (!TryReadArguments(source, lexicalMap, site.OpenParen, out var arguments))
                {
                    issues.Add(new CodeRefScanIssue(site.Position, "构造调用缺少配对的右括号。"));
                    continue;
                }

                if (site.Kind == CallKind.Here)
                {
                    if (arguments.Count >= 3)
                    {
                        issues.Add(new CodeRefScanIssue(site.Position,
                            "CodeRef.Here 不应显式传 callerFilePath；这会绕过调用文件自动定位。"));
                        continue;
                    }

                    if (arguments.Count == 0)
                    {
                        calls.Add(new CodeRefCall(null, null, site.Position));
                        continue;
                    }

                    if (!TryEvaluateString(source, lexicalMap, arguments[0], constants, out string anchor))
                    {
                        issues.Add(new CodeRefScanIssue(site.Position, "无法静态求值 CodeRef.Here 的 anchor。"));
                        continue;
                    }
                    calls.Add(new CodeRefCall(null, anchor, site.Position));
                    continue;
                }

                if (arguments.Count == 0 ||
                    !TryEvaluateString(source, lexicalMap, arguments[0], constants, out string path) ||
                    string.IsNullOrEmpty(path))
                {
                    issues.Add(new CodeRefScanIssue(site.Position, "无法静态求值 CodeRef 的 path。"));
                    continue;
                }

                string explicitAnchor = null;
                if (arguments.Count >= 2 &&
                    !TryEvaluateString(source, lexicalMap, arguments[1], constants, out explicitAnchor))
                {
                    issues.Add(new CodeRefScanIssue(site.Position, "无法静态求值 CodeRef 的 anchor。"));
                    continue;
                }
                calls.Add(new CodeRefCall(path, explicitAnchor, site.Position));
            }

            return new CodeRefSourceScanResult(calls, issues);
        }

        private static void AddSites(List<CallSite> sites, Regex regex, string codeOnly, CallKind kind)
        {
            foreach (Match match in regex.Matches(codeOnly))
            {
                int openParen = codeOnly.IndexOf('(', match.Index, match.Length);
                if (openParen >= 0) sites.Add(new CallSite(kind, match.Index, openParen));
            }
        }

        private static void AddTargetTypedSites(
            List<CallSite> sites,
            string codeOnly,
            CSharpLexicalMap lexicalMap)
        {
            AddTargetTypedInitializerSites(sites, TargetTypedVariable, codeOnly, lexicalMap);
            AddTargetTypedInitializerSites(sites, TargetTypedAutoProperty, codeOnly, lexicalMap);
            AddTargetTypedInitializerSites(sites, TargetTypedExpressionBody, codeOnly, lexicalMap);

            AddTargetTypedReturnSites(sites, CodeRefReturningBlock, codeOnly, lexicalMap);
            AddTargetTypedReturnSites(sites, CodeRefReturningPropertyBlock, codeOnly, lexicalMap);
        }

        private static void AddTargetTypedReturnSites(
            List<CallSite> sites,
            Regex declaration,
            string codeOnly,
            CSharpLexicalMap lexicalMap)
        {
            foreach (Match match in declaration.Matches(codeOnly))
            {
                int openBrace = codeOnly.IndexOf('{', match.Index, match.Length);
                int closeBrace = FindMatchingBrace(codeOnly, lexicalMap, openBrace);
                if (openBrace < 0 || closeBrace < 0) continue;

                Match returnMatch = ReturnToken.Match(codeOnly, openBrace + 1);
                while (returnMatch.Success && returnMatch.Index < closeBrace)
                {
                    int expressionStart = returnMatch.Index + returnMatch.Length;
                    int expressionEnd = FindStatementEnd(codeOnly, lexicalMap, expressionStart);
                    if (expressionEnd > expressionStart && expressionEnd < closeBrace)
                        AddTargetTypedNewSitesInRange(sites, codeOnly, expressionStart, expressionEnd);
                    returnMatch = returnMatch.NextMatch();
                }
            }
        }

        private static void AddTargetTypedInitializerSites(
            List<CallSite> sites,
            Regex declaration,
            string codeOnly,
            CSharpLexicalMap lexicalMap)
        {
            foreach (Match match in declaration.Matches(codeOnly))
            {
                int expressionStart = match.Index + match.Length;
                int expressionEnd = FindStatementEnd(codeOnly, lexicalMap, expressionStart);
                if (expressionEnd > expressionStart)
                    AddTargetTypedNewSitesInRange(sites, codeOnly, expressionStart, expressionEnd);
            }
        }

        private static void AddTargetTypedNewSitesInRange(
            List<CallSite> sites,
            string codeOnly,
            int start,
            int end)
        {
            Match match = TargetTypedNewToken.Match(codeOnly, start);
            while (match.Success && match.Index < end)
            {
                int openParen = codeOnly.IndexOf('(', match.Index, match.Length);
                if (openParen >= 0) sites.Add(new CallSite(CallKind.TargetTypedNew, match.Index, openParen));
                match = match.NextMatch();
            }
        }

        private static int FindMatchingBrace(string codeOnly, CSharpLexicalMap lexicalMap, int openBrace)
        {
            if (openBrace < 0) return -1;
            int depth = 0;
            for (int i = openBrace; i < codeOnly.Length; i++)
            {
                if (!lexicalMap.IsCode(i)) continue;
                if (codeOnly[i] == '{') depth++;
                else if (codeOnly[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        private static Dictionary<string, string> ExtractStringConstants(
            string source,
            string codeOnly,
            CSharpLexicalMap lexicalMap)
        {
            var pending = new List<ConstantDeclaration>();
            foreach (Match match in ConstString.Matches(codeOnly))
            {
                int expressionStart = match.Index + match.Length;
                int expressionEnd = FindStatementEnd(codeOnly, lexicalMap, expressionStart);
                if (expressionEnd >= 0)
                    pending.Add(new ConstantDeclaration(
                        NormalizeIdentifier(match.Groups["name"].Value),
                        new TextRange(expressionStart, expressionEnd)));
            }

            // 轻量扫描器不建完整作用域树：同名 const 可能来自不同类型/局部块。宁可不解析并让调用点报 issue，
            // 也不能让后出现的声明静默覆盖前者、把错误路径判成有效。
            var duplicateNames = new HashSet<string>(
                pending.GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key),
                StringComparer.Ordinal);
            pending.RemoveAll(declaration => duplicateNames.Contains(declaration.Name));

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool progressed;
            do
            {
                progressed = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var declaration = pending[i];
                    if (!TryEvaluateString(source, lexicalMap, declaration.Expression, values, out string value)) continue;
                    values[declaration.Name] = value;
                    pending.RemoveAt(i);
                    progressed = true;
                }
            } while (progressed && pending.Count > 0);

            return values;
        }

        private static int FindStatementEnd(string codeOnly, CSharpLexicalMap lexicalMap, int start)
        {
            int paren = 0;
            int bracket = 0;
            int brace = 0;
            for (int i = start; i < codeOnly.Length; i++)
            {
                if (!lexicalMap.IsCode(i)) continue;
                switch (codeOnly[i])
                {
                    case '(': paren++; break;
                    case ')': paren--; break;
                    case '[': bracket++; break;
                    case ']': bracket--; break;
                    case '{': brace++; break;
                    case '}': brace--; break;
                    case ';' when paren == 0 && bracket == 0 && brace == 0: return i;
                }
            }
            return -1;
        }

        private static bool TryReadArguments(
            string source,
            CSharpLexicalMap lexicalMap,
            int openParen,
            out List<TextRange> arguments)
        {
            arguments = new List<TextRange>();
            int start = openParen + 1;
            int paren = 0;
            int bracket = 0;
            int brace = 0;
            for (int i = start; i < source.Length; i++)
            {
                if (!lexicalMap.IsCode(i)) continue;
                switch (source[i])
                {
                    case '(':
                        paren++;
                        break;
                    case ')':
                        if (paren > 0)
                        {
                            paren--;
                            break;
                        }
                        if (bracket != 0 || brace != 0) break;
                        if (!string.IsNullOrWhiteSpace(source.Substring(start, i - start)))
                            arguments.Add(new TextRange(start, i));
                        return true;
                    case '[':
                        bracket++;
                        break;
                    case ']':
                        bracket--;
                        break;
                    case '{':
                        brace++;
                        break;
                    case '}':
                        brace--;
                        break;
                    case ',' when paren == 0 && bracket == 0 && brace == 0:
                        arguments.Add(new TextRange(start, i));
                        start = i + 1;
                        break;
                }
            }
            return false;
        }

        private static bool TryEvaluateString(
            string source,
            CSharpLexicalMap lexicalMap,
            TextRange expression,
            IReadOnlyDictionary<string, string> constants,
            out string value)
        {
            expression = Trim(source, expression);
            if (expression.Length == 0)
            {
                value = null;
                return false;
            }

            if (TryStripOuterParentheses(source, lexicalMap, expression, out var inner))
                return TryEvaluateString(source, lexicalMap, inner, constants, out value);

            int split = FindTopLevelPlus(source, lexicalMap, expression);
            if (split >= 0)
            {
                if (!TryEvaluateString(source, lexicalMap, new TextRange(expression.Start, split), constants, out string left) ||
                    !TryEvaluateString(source, lexicalMap, new TextRange(split + 1, expression.End), constants, out string right) ||
                    left == null || right == null)
                {
                    value = null;
                    return false;
                }
                value = left + right;
                return true;
            }

            string token = source.Substring(expression.Start, expression.Length).Trim();
            if (token == "null")
            {
                value = null;
                return true;
            }
            if (TryParseStringLiteral(token, out value)) return true;
            return constants.TryGetValue(NormalizeIdentifier(token), out value);
        }

        private static string NormalizeIdentifier(string identifier)
        {
            return identifier.Length > 0 && identifier[0] == '@'
                ? identifier.Substring(1)
                : identifier;
        }

        private static int FindTopLevelPlus(string source, CSharpLexicalMap lexicalMap, TextRange expression)
        {
            int paren = 0;
            for (int i = expression.Start; i < expression.End; i++)
            {
                if (!lexicalMap.IsCode(i)) continue;
                if (source[i] == '(') paren++;
                else if (source[i] == ')') paren--;
                else if (source[i] == '+' && paren == 0) return i;
            }
            return -1;
        }

        private static bool TryStripOuterParentheses(
            string source,
            CSharpLexicalMap lexicalMap,
            TextRange expression,
            out TextRange inner)
        {
            inner = default;
            if (source[expression.Start] != '(' || source[expression.End - 1] != ')') return false;
            int depth = 0;
            for (int i = expression.Start; i < expression.End; i++)
            {
                if (!lexicalMap.IsCode(i)) continue;
                if (source[i] == '(') depth++;
                else if (source[i] == ')' && --depth == 0 && i != expression.End - 1) return false;
            }
            if (depth != 0) return false;
            inner = new TextRange(expression.Start + 1, expression.End - 1);
            return true;
        }

        private static bool TryParseStringLiteral(string token, out string value)
        {
            value = null;
            if (token.Length >= 6 && token[0] == '"' && token[1] == '"' && token[2] == '"')
            {
                int delimiterLength = 3;
                while (delimiterLength < token.Length && token[delimiterLength] == '"') delimiterLength++;
                if (token.Length < delimiterLength * 2 ||
                    token.Substring(token.Length - delimiterLength) != new string('"', delimiterLength)) return false;
                string raw = token.Substring(delimiterLength, token.Length - delimiterLength * 2);
                if (raw.IndexOfAny(new[] { '\r', '\n' }) >= 0) return false; // 多行 raw 的缩进裁剪交给真正语法树处理。
                value = raw;
                return true;
            }

            if (token.Length >= 3 && token[0] == '@' && token[1] == '"' && token[token.Length - 1] == '"')
            {
                value = token.Substring(2, token.Length - 3).Replace("\"\"", "\"");
                return true;
            }
            return token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"' &&
                   TryDecodeRegularString(token.Substring(1, token.Length - 2), out value);
        }

        private static bool TryDecodeRegularString(string encoded, out string value)
        {
            var decoded = new StringBuilder(encoded.Length);
            for (int i = 0; i < encoded.Length; i++)
            {
                char c = encoded[i];
                if (c != '\\')
                {
                    decoded.Append(c);
                    continue;
                }
                if (++i >= encoded.Length)
                {
                    value = null;
                    return false;
                }

                c = encoded[i];
                switch (c)
                {
                    case '\'': decoded.Append('\''); break;
                    case '"': decoded.Append('"'); break;
                    case '\\': decoded.Append('\\'); break;
                    case '0': decoded.Append('\0'); break;
                    case 'a': decoded.Append('\a'); break;
                    case 'b': decoded.Append('\b'); break;
                    case 'f': decoded.Append('\f'); break;
                    case 'n': decoded.Append('\n'); break;
                    case 'r': decoded.Append('\r'); break;
                    case 't': decoded.Append('\t'); break;
                    case 'v': decoded.Append('\v'); break;
                    case 'u':
                        if (!TryReadHex(encoded, i + 1, 4, 4, out int uValue, out int uDigits))
                        {
                            value = null;
                            return false;
                        }
                        decoded.Append((char)uValue);
                        i += uDigits;
                        break;
                    case 'U':
                        if (!TryReadHex(encoded, i + 1, 8, 8, out int bigValue, out int bigDigits) ||
                            bigValue < 0 || bigValue > 0x10FFFF ||
                            bigValue >= 0xD800 && bigValue <= 0xDFFF)
                        {
                            value = null;
                            return false;
                        }
                        decoded.Append(char.ConvertFromUtf32(bigValue));
                        i += bigDigits;
                        break;
                    case 'x':
                        if (!TryReadHex(encoded, i + 1, 1, 4, out int xValue, out int xDigits))
                        {
                            value = null;
                            return false;
                        }
                        decoded.Append((char)xValue);
                        i += xDigits;
                        break;
                    default:
                        value = null;
                        return false;
                }
            }
            value = decoded.ToString();
            return true;
        }

        private static bool TryReadHex(
            string text,
            int start,
            int minDigits,
            int maxDigits,
            out int value,
            out int digits)
        {
            value = 0;
            digits = 0;
            while (start + digits < text.Length && digits < maxDigits)
            {
                int nibble = HexValue(text[start + digits]);
                if (nibble < 0) break;
                value = (value << 4) | nibble;
                digits++;
            }
            return digits >= minDigits;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static TextRange Trim(string source, TextRange range)
        {
            int start = range.Start;
            int end = range.End;
            while (start < end && char.IsWhiteSpace(source[start])) start++;
            while (end > start && char.IsWhiteSpace(source[end - 1])) end--;
            return new TextRange(start, end);
        }

        private enum CallKind
        {
            Here,
            ExplicitNew,
            TargetTypedNew,
        }

        private readonly struct CallSite
        {
            internal CallSite(CallKind kind, int position, int openParen)
            {
                Kind = kind;
                Position = position;
                OpenParen = openParen;
            }

            internal CallKind Kind { get; }
            internal int Position { get; }
            internal int OpenParen { get; }
        }

        private readonly struct ConstantDeclaration
        {
            internal ConstantDeclaration(string name, TextRange expression)
            {
                Name = name;
                Expression = expression;
            }

            internal string Name { get; }
            internal TextRange Expression { get; }
        }

        private readonly struct TextRange
        {
            internal TextRange(int start, int end)
            {
                Start = start;
                End = end;
            }

            internal int Start { get; }
            internal int End { get; }
            internal int Length => End - Start;
        }
    }

    internal sealed class CodeRefSourceScanResult
    {
        internal CodeRefSourceScanResult(IReadOnlyList<CodeRefCall> calls, IReadOnlyList<CodeRefScanIssue> issues)
        {
            Calls = calls;
            Issues = issues;
        }

        internal IReadOnlyList<CodeRefCall> Calls { get; }
        internal IReadOnlyList<CodeRefScanIssue> Issues { get; }
        internal int SiteCount => Calls.Count + Issues.Count;
    }

    internal readonly struct CodeRefCall
    {
        internal CodeRefCall(string path, string anchor, int position)
        {
            Path = path;
            Anchor = anchor;
            Position = position;
        }

        internal string Path { get; }
        internal string Anchor { get; }
        internal int Position { get; }
    }

    internal readonly struct CodeRefScanIssue
    {
        internal CodeRefScanIssue(int position, string message)
        {
            Position = position;
            Message = message;
        }

        internal int Position { get; }
        internal string Message { get; }
    }
}
#endif
