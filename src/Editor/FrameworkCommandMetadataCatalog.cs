using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Framework.Systems;
using UnityEditor;
using UnityEditor.Compilation;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 把命令流水中的稳定类型身份还原为人类可读描述和源码位置。
    /// </summary>
    /// <remarks>
    /// 命令记录仍不包含 payload；说明只读标准 <see cref="DescriptionAttribute"/>，源码只在用户
    /// 显式双击时按命令所属程序集搜索。路径还原统一经 <see cref="FrameworkModuleSourceCatalog"/>，
    /// 因此 Project Assets、嵌入包和 Package Cache 不需要各写一套分支。
    /// </remarks>
    internal static class FrameworkCommandMetadataCatalog
    {
        internal readonly struct SourceReference
        {
            internal SourceReference(string assetPath, int line, string issue)
            {
                AssetPath = assetPath ?? string.Empty;
                Line = line;
                Issue = issue ?? string.Empty;
            }

            internal string AssetPath { get; }
            internal int Line { get; }
            internal string Issue { get; }
            internal bool Found => AssetPath.Length > 0 && Line > 0;
        }

        private readonly struct DeclarationCandidate
        {
            internal DeclarationCandidate(string assetPath, int line, bool namespaceMatches)
            {
                AssetPath = assetPath;
                Line = line;
                NamespaceMatches = namespaceMatches;
            }

            internal string AssetPath { get; }
            internal int Line { get; }
            internal bool NamespaceMatches { get; }
        }

        private enum LexicalState
        {
            Code,
            LineComment,
            BlockComment,
            String,
            VerbatimString,
            Character,
            RawString,
        }

        private static readonly Regex NamespaceDeclaration = new(
            @"\bnamespace\s+([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*(?:;|\{)",
            RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, Type> TypeCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> DescriptionCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, SourceReference> SourceCache = new(StringComparer.Ordinal);

        static FrameworkCommandMetadataCatalog()
        {
            CompilationPipeline.compilationFinished += _ => ClearCaches();
        }

        internal static string GetDescription(in LoggingCommandSystem.Entry entry) =>
            GetDescription(entry.CommandTypeId);

        internal static string GetDescription(string commandTypeId)
        {
            if (string.IsNullOrWhiteSpace(commandTypeId)) return string.Empty;
            if (DescriptionCache.TryGetValue(commandTypeId, out string cached)) return cached;

            Type type = ResolveType(commandTypeId);
            string description = type?
                .GetCustomAttribute<DescriptionAttribute>(inherit: false)?
                .Description?
                .Trim() ?? string.Empty;
            DescriptionCache[commandTypeId] = description;
            return description;
        }

        internal static SourceReference FindSource(in LoggingCommandSystem.Entry entry) =>
            FindSource(entry.CommandTypeId);

        internal static SourceReference FindSource(string commandTypeId)
        {
            if (string.IsNullOrWhiteSpace(commandTypeId))
                return new SourceReference(string.Empty, 0, "这条旧流水没有可解析的命令类型身份。");
            if (SourceCache.TryGetValue(commandTypeId, out SourceReference cached)) return cached;

            Type type = ResolveType(commandTypeId);
            SourceReference resolved = type == null
                ? new SourceReference(string.Empty, 0, "命令类型已无法从当前 AppDomain 解析，可能是刚编译前的旧流水。")
                : FindSource(type);
            SourceCache[commandTypeId] = resolved;
            return resolved;
        }

        internal static bool TryOpenSource(
            in LoggingCommandSystem.Entry entry,
            out string message)
        {
            SourceReference source = FindSource(entry);
            if (!source.Found)
            {
                message = source.Issue;
                return false;
            }

            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(source.AssetPath);
            if (script == null)
            {
                message = $"已找到声明，但 Unity 无法把源码路径还原为 MonoScript：{source.AssetPath}";
                return false;
            }

            if (!AssetDatabase.OpenAsset(script, source.Line))
            {
                message = $"源码打开失败：{source.AssetPath}:{source.Line}";
                return false;
            }

            message = $"已打开 {source.AssetPath}:{source.Line}";
            return true;
        }

        /// <summary>
        /// 在一段 C# 源码里定位类型声明。保留该窄入口用于锁定“注释/字符串不应误命中”的契约。
        /// </summary>
        internal static int FindDeclarationLine(string source, string typeName)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrWhiteSpace(typeName)) return 0;
            string codeOnly = CreateCodeOnlyText(source);
            Match match = CreateTypeDeclarationRegex(typeName).Match(codeOnly);
            return match.Success ? CountLine(source, match.Index) : 0;
        }

        private static SourceReference FindSource(Type type)
        {
            string assemblyName = type.Assembly.GetName().Name;
            UnityEditor.Compilation.Assembly assembly = CompilationPipeline.GetAssemblies()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.name,
                    assemblyName,
                    StringComparison.Ordinal));
            if (assembly == null)
                return new SourceReference(
                    string.Empty,
                    0,
                    $"当前 Unity 编译图中找不到程序集 {assemblyName}。");
            if (assembly.sourceFiles == null || assembly.sourceFiles.Length == 0)
                return new SourceReference(
                    string.Empty,
                    0,
                    $"程序集 {assemblyName} 没有可读源文件，它可能只以预编译 DLL 存在。");

            FrameworkModuleSourceCatalog.SourceLocation[] sources;
            try
            {
                sources = FrameworkModuleSourceCatalog.ResolveKnownAssetPaths(assembly.sourceFiles);
            }
            catch (Exception error)
            {
                return new SourceReference(
                    string.Empty,
                    0,
                    $"命令所属程序集的源码证据不完整：{error.Message}");
            }

            string simpleName = StripGenericArity(type.Name);
            Regex declaration = CreateTypeDeclarationRegex(simpleName);
            var candidates = new List<DeclarationCandidate>();
            foreach (FrameworkModuleSourceCatalog.SourceLocation location in sources)
            {
                string source;
                try
                {
                    source = File.ReadAllText(location.PhysicalPath);
                }
                catch (Exception error)
                {
                    return new SourceReference(
                        string.Empty,
                        0,
                        $"无法读取 {location.AssetPath}：{error.Message}");
                }

                string codeOnly = CreateCodeOnlyText(source);
                foreach (Match match in declaration.Matches(codeOnly))
                {
                    string sourceNamespace = FindNamespaceAt(codeOnly, match.Index);
                    candidates.Add(new DeclarationCandidate(
                        location.AssetPath,
                        CountLine(source, match.Index),
                        string.Equals(sourceNamespace, type.Namespace ?? string.Empty, StringComparison.Ordinal)));
                }
            }

            DeclarationCandidate[] exact = candidates.Where(candidate => candidate.NamespaceMatches).ToArray();
            DeclarationCandidate[] usable = exact.Length > 0 ? exact : candidates.ToArray();
            if (usable.Length == 1)
                return new SourceReference(usable[0].AssetPath, usable[0].Line, string.Empty);
            if (usable.Length == 0)
                return new SourceReference(
                    string.Empty,
                    0,
                    $"在程序集 {assemblyName} 的源码中找不到 {type.FullName} 的类型声明。");

            string locations = string.Join(", ", usable
                .Select(candidate => $"{candidate.AssetPath}:{candidate.Line}")
                .OrderBy(value => value, StringComparer.Ordinal));
            return new SourceReference(
                string.Empty,
                0,
                $"找到多个同名类型声明，为避免跳错位置已停止：{locations}");
        }

        private static Type ResolveType(string commandTypeId)
        {
            if (TypeCache.TryGetValue(commandTypeId, out Type cached)) return cached;
            Type resolved = null;
            try
            {
                resolved = Type.GetType(commandTypeId, throwOnError: false);
            }
            catch (Exception)
            {
                // 类型身份来自上一次编译的流水时可能已经过期；UI 降级为无描述/无跳转。
            }

            TypeCache[commandTypeId] = resolved;
            return resolved;
        }

        private static Regex CreateTypeDeclarationRegex(string typeName) => new(
            @"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+@?" +
            Regex.Escape(StripGenericArity(typeName)) + @"\b",
            RegexOptions.CultureInvariant);

        private static string StripGenericArity(string typeName)
        {
            int marker = typeName.IndexOf('`');
            return marker >= 0 ? typeName.Substring(0, marker) : typeName;
        }

        private static string FindNamespaceAt(string codeOnly, int declarationIndex)
        {
            string result = string.Empty;
            foreach (Match match in NamespaceDeclaration.Matches(codeOnly))
            {
                if (match.Index >= declarationIndex) break;
                result = match.Groups[1].Value;
            }
            return result;
        }

        private static int CountLine(string source, int index)
        {
            int line = 1;
            int limit = Math.Min(index, source.Length);
            for (var i = 0; i < limit; i++)
                if (source[i] == '\n') line++;
            return line;
        }

        /// <summary>
        /// 用等长空格遮掉注释与字面量，但保留换行和真实代码索引，以便源码行号不偏移。
        /// </summary>
        private static string CreateCodeOnlyText(string source)
        {
            char[] result = source.ToCharArray();
            LexicalState state = LexicalState.Code;
            int rawQuoteCount = 0;

            for (var i = 0; i < source.Length; i++)
            {
                char current = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';
                switch (state)
                {
                    case LexicalState.Code:
                        if (current == '/' && next == '/')
                        {
                            Mask(result, i);
                            Mask(result, ++i);
                            state = LexicalState.LineComment;
                        }
                        else if (current == '/' && next == '*')
                        {
                            Mask(result, i);
                            Mask(result, ++i);
                            state = LexicalState.BlockComment;
                        }
                        else if (current == '\"')
                        {
                            rawQuoteCount = CountConsecutive(source, i, '\"');
                            bool isRaw = rawQuoteCount >= 3;
                            bool isVerbatim = i > 0 && source[i - 1] == '@';
                            int quotesToMask = isRaw ? rawQuoteCount : 1;
                            for (var quote = 0; quote < quotesToMask; quote++) Mask(result, i + quote);
                            i += quotesToMask - 1;
                            state = isRaw
                                ? LexicalState.RawString
                                : isVerbatim ? LexicalState.VerbatimString : LexicalState.String;
                        }
                        else if (current == '\'')
                        {
                            Mask(result, i);
                            state = LexicalState.Character;
                        }
                        break;

                    case LexicalState.LineComment:
                        Mask(result, i);
                        if (current == '\n') state = LexicalState.Code;
                        break;

                    case LexicalState.BlockComment:
                        Mask(result, i);
                        if (current == '*' && next == '/')
                        {
                            Mask(result, ++i);
                            state = LexicalState.Code;
                        }
                        break;

                    case LexicalState.String:
                    case LexicalState.Character:
                        Mask(result, i);
                        if (current == '\\' && i + 1 < source.Length)
                        {
                            Mask(result, ++i);
                        }
                        else if ((state == LexicalState.String && current == '\"') ||
                                 (state == LexicalState.Character && current == '\''))
                        {
                            state = LexicalState.Code;
                        }
                        break;

                    case LexicalState.VerbatimString:
                        Mask(result, i);
                        if (current != '\"') break;
                        if (next == '\"')
                        {
                            Mask(result, ++i);
                        }
                        else
                        {
                            state = LexicalState.Code;
                        }
                        break;

                    case LexicalState.RawString:
                        int quoteCount = current == '\"' ? CountConsecutive(source, i, '\"') : 0;
                        if (quoteCount >= rawQuoteCount)
                        {
                            for (var quote = 0; quote < rawQuoteCount; quote++) Mask(result, i + quote);
                            i += rawQuoteCount - 1;
                            state = LexicalState.Code;
                        }
                        else
                        {
                            Mask(result, i);
                        }
                        break;
                }
            }

            return new string(result);
        }

        private static int CountConsecutive(string source, int start, char value)
        {
            int count = 0;
            while (start + count < source.Length && source[start + count] == value) count++;
            return count;
        }

        private static void Mask(char[] text, int index)
        {
            if (text[index] != '\r' && text[index] != '\n') text[index] = ' ';
        }

        private static void ClearCaches()
        {
            TypeCache.Clear();
            DescriptionCache.Clear();
            SourceCache.Clear();
        }
    }
}
