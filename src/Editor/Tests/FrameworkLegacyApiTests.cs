using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.Compilation;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定仍可编译的迁移入口不会重新扩散到 Framework 生产调用点。</summary>
    public sealed class FrameworkLegacyApiTests
    {
        private static readonly Regex LegacyLoadingReference = new(
            @"(?:\.\s*\b(?<name>ShowLoading|HideLoading)\b|\b(?<name>ShowLoading|HideLoading)\b\s*\()",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        [Test]
        public void ProductionSources_DoNotCallLegacyLoadingPair()
        {
            var violations = new List<string>();
            int scannedSources = 0;

            foreach (UnityEditor.Compilation.Assembly assembly in
                     CompilationPipeline.GetAssemblies(AssembliesType.Player)
                         .Where(item => IsFrameworkProductionAssembly(item.name)))
            {
                foreach (string sourcePath in assembly.sourceFiles ?? Array.Empty<string>())
                {
                    if (!sourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                    FrameworkModuleSourceCatalog.SourceLocation location =
                        FrameworkModuleSourceCatalog.Resolve(sourcePath);
                    scannedSources++;

                    string source = File.ReadAllText(location.PhysicalPath);
                    string codeOnly = StripCommentsAndLiterals(source);
                    foreach (Match match in LegacyLoadingReference.Matches(codeOnly))
                    {
                        int lineStart = source.LastIndexOf('\n', Math.Max(0, match.Index - 1));
                        lineStart = lineStart < 0 ? 0 : lineStart + 1;
                        int lineEnd = source.IndexOf('\n', match.Index);
                        if (lineEnd < 0) lineEnd = source.Length;
                        string sourceLine = source.Substring(lineStart, lineEnd - lineStart).Trim();
                        if (IsAllowedCompatibilityForwarder(location.AssetPath, sourceLine)) continue;

                        int line = 1 + source.Take(match.Index).Count(ch => ch == '\n');
                        violations.Add($"{location.AssetPath}:{line} → {sourceLine}");
                    }
                }
            }

            Assert.That(scannedSources, Is.GreaterThan(0),
                "必须实际扫描 Framework Player 源码，不能把空编译图误判为没有旧调用。 ");
            Assert.That(violations, Is.Empty,
                "ShowLoading/HideLoading 只允许保留在既有 Adapter 的兼容转发中；" +
                "生产调用点必须使用 AcquireLoading + LoadingHandle：\n" + string.Join("\n", violations));
        }

        [Test]
        public void CodeOnlyScanner_IgnoresCommentsAndLiterals_ButKeepsAllCodeReferences()
        {
            const string source =
                "// ui.ShowLoading()\n" +
                "/* HideLoading(); */\n" +
                "var normal = \"ui.ShowLoading()\";\n" +
                "var verbatim = @\"ui.HideLoading()\";\n" +
                "var character = 'x';\n" +
                "var ShowLoading = 1;\n" +
                "var HideLoading = 2;\n" +
                "ShowLoading();\n" +
                "ui.HideLoading();\n";

            string[] references = LegacyLoadingReference.Matches(StripCommentsAndLiterals(source))
                .Cast<Match>()
                .Select(match => match.Groups["name"].Value)
                .ToArray();

            Assert.That(references, Is.EqualTo(new[] { "ShowLoading", "HideLoading" }),
                "门禁必须同时识别限定与无限定方法引用，但不能被说明文字、字符串或同名普通标识符误伤。 ");
        }

        private static bool IsFrameworkProductionAssembly(string assemblyName)
            => !string.IsNullOrEmpty(assemblyName) &&
               (assemblyName == "Game.Framework" ||
                assemblyName.StartsWith("Game.Framework.", StringComparison.Ordinal)) &&
               !assemblyName.Contains(".Test", StringComparison.Ordinal);

        private static bool IsAllowedCompatibilityForwarder(string assetPath, string sourceLine)
        {
            string normalized = (assetPath ?? string.Empty).Replace('\\', '/');
            if (normalized.EndsWith("/UI/IUIUtility.cs", StringComparison.OrdinalIgnoreCase))
                return sourceLine ==
                           "UniTask ShowLoading(string text = null, CancellationToken ct = default);" ||
                       sourceLine == "void HideLoading();";

            if (normalized.EndsWith("/UI/UIUtility.cs", StringComparison.OrdinalIgnoreCase))
                return sourceLine ==
                           "public async UniTask ShowLoading(string text = null, CancellationToken ct = default)" ||
                       sourceLine == "public void HideLoading()";

            bool knownAdapter =
                normalized.EndsWith("/UI.Toolkit/MonoToolkitUI.cs", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("/UI.UGui/MonoUGuiUI.cs", StringComparison.OrdinalIgnoreCase);
            return knownAdapter &&
                   (sourceLine ==
                        "public UniTask ShowLoading(string text = null, CancellationToken ct = default) => Core.ShowLoading(text, ct);" ||
                    sourceLine == "public void HideLoading() => Core.HideLoading();");
        }

        /// <summary>
        /// 保留代码字符与换行位置，屏蔽注释、普通/逐字字符串和字符字面量；这样 Match.Index 仍能还原原始行号。
        /// 插值字符串整体视为表现文本：其中若真的调用旧成员，编译器的 Obsolete 警告仍是第一层门禁。
        /// </summary>
        private static string StripCommentsAndLiterals(string source)
        {
            if (string.IsNullOrEmpty(source)) return source ?? string.Empty;

            var result = new StringBuilder(source);
            LexicalState state = LexicalState.Code;
            for (int i = 0; i < source.Length; i++)
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
                        else if (current == '@' && next == '"')
                        {
                            Mask(result, i);
                            Mask(result, ++i);
                            state = LexicalState.VerbatimString;
                        }
                        else if (current == '$' && next == '@' &&
                                 i + 2 < source.Length && source[i + 2] == '"')
                        {
                            Mask(result, i);
                            Mask(result, ++i);
                            Mask(result, ++i);
                            state = LexicalState.VerbatimString;
                        }
                        else if (current == '@' && next == '$' &&
                                 i + 2 < source.Length && source[i + 2] == '"')
                        {
                            Mask(result, i);
                            Mask(result, ++i);
                            Mask(result, ++i);
                            state = LexicalState.VerbatimString;
                        }
                        else if (current == '$' && next == '"')
                        {
                            Mask(result, i);
                            Mask(result, ++i);
                            state = LexicalState.String;
                        }
                        else if (current == '"')
                        {
                            Mask(result, i);
                            state = LexicalState.String;
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
                        if (current == '\\' && next != '\0')
                        {
                            Mask(result, ++i);
                        }
                        else if ((state == LexicalState.String && current == '"') ||
                                 (state == LexicalState.Character && current == '\''))
                        {
                            state = LexicalState.Code;
                        }
                        break;

                    case LexicalState.VerbatimString:
                        Mask(result, i);
                        if (current != '"') break;
                        if (next == '"')
                        {
                            Mask(result, ++i);
                        }
                        else
                        {
                            state = LexicalState.Code;
                        }
                        break;
                }
            }

            return result.ToString();
        }

        private static void Mask(StringBuilder result, int index)
        {
            if (result[index] != '\r' && result[index] != '\n') result[index] = ' ';
        }

        private enum LexicalState
        {
            Code,
            LineComment,
            BlockComment,
            String,
            VerbatimString,
            Character,
        }
    }
}
