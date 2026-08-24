#if UNITY_EDITOR
using System;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// 一份轻量 C# 词法视图：区分真正的代码、字符串/字符字面量与注释，不尝试构建语法树。
    /// </summary>
    /// <remarks>
    /// 插值字符串的文本仍属于字面量，但 <c>{ ... }</c> 插值孔会递归按 C# 代码扫描；因此教程文案不会
    /// 冒充声明，插值孔中的真实调用也不会被误删。普通、verbatim 与常见 raw 插值形式走同一份映射。
    /// </remarks>
    internal sealed class CSharpLexicalMap
    {
        internal enum Region : byte
        {
            Code,
            StringOrChar,
            Comment,
        }

        private readonly string _source;
        private readonly Region[] _regions;

        private CSharpLexicalMap(string source, Region[] regions)
        {
            _source = source;
            _regions = regions;
        }

        internal static CSharpLexicalMap Create(string source)
        {
            source ??= string.Empty;
            var regions = new Region[source.Length]; // Region.Code 为默认值。
            ScanCodeRange(source, regions, 0, source.Length);
            return new CSharpLexicalMap(source, regions);
        }

        internal Region GetRegion(int index) =>
            index >= 0 && index < _regions.Length ? _regions[index] : Region.Code;

        internal bool IsCode(int index) => GetRegion(index) == Region.Code;

        /// <summary>保留长度与换行，只把非代码字符换成空格，便于把正则位置映射回原文件。</summary>
        internal string CreateCodeOnlyText()
        {
            var chars = _source.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (_regions[i] != Region.Code && chars[i] != '\r' && chars[i] != '\n') chars[i] = ' ';
            }
            return new string(chars);
        }

        private static void ScanCodeRange(string source, Region[] regions, int start, int end)
        {
            int i = start;
            while (i < end)
            {
                if (TrySkipComment(source, i, end, out int commentEnd))
                {
                    Mark(regions, i, commentEnd, Region.Comment);
                    i = commentEnd;
                    continue;
                }

                if (TryReadQuotedToken(source, i, end, out var token))
                {
                    Mark(regions, i, token.End, Region.StringOrChar);
                    if (token.Interpolated)
                        RestoreInterpolationCode(source, regions, token);
                    i = token.End;
                    continue;
                }

                i++;
            }
        }

        private static void RestoreInterpolationCode(string source, Region[] regions, QuotedToken token)
        {
            int requiredBraces = token.Raw ? Math.Max(1, token.DollarCount) : 1;
            int i = token.ContentStart;
            while (i < token.ContentEnd)
            {
                if (!token.Raw && !token.Verbatim && source[i] == '\\')
                {
                    i = Math.Min(token.ContentEnd, i + 2);
                    continue;
                }

                if (!token.Raw && source[i] == '{' && i + 1 < token.ContentEnd && source[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }

                if (source[i] != '{')
                {
                    i++;
                    continue;
                }

                int openRun = CountRun(source, i, '{', token.ContentEnd);
                if (openRun < requiredBraces)
                {
                    i += openRun;
                    continue;
                }

                int expressionStart = i + requiredBraces;
                int close = FindInterpolationClose(source, expressionStart, token.ContentEnd, requiredBraces);
                if (close < 0) return; // 非法/未闭合源码：保守地继续视为字符串，避免假代码命中。

                int codeEnd = FindInterpolationFormatStart(source, expressionStart, close);
                Mark(regions, expressionStart, codeEnd, Region.Code);
                ScanCodeRange(source, regions, expressionStart, codeEnd);
                i = close + requiredBraces;
            }
        }

        // 插值孔的顶层 ':' 之后是 format 文本，不是 C#。三元表达式、别名 :: 与嵌套括号内的 ':' 不算。
        private static int FindInterpolationFormatStart(string source, int start, int end)
        {
            int paren = 0;
            int bracket = 0;
            int brace = 0;
            int ternary = 0;
            int i = start;
            while (i < end)
            {
                if (TrySkipComment(source, i, end, out int commentEnd))
                {
                    i = commentEnd;
                    continue;
                }
                if (TryReadQuotedToken(source, i, end, out var token))
                {
                    i = token.End;
                    continue;
                }

                switch (source[i])
                {
                    case '(': paren++; break;
                    case ')': paren--; break;
                    case '[': bracket++; break;
                    case ']': bracket--; break;
                    case '{': brace++; break;
                    case '}': brace--; break;
                    case '?' when paren == 0 && bracket == 0 && brace == 0:
                        if (i + 1 >= end || source[i + 1] != '?' && source[i + 1] != '.' && source[i + 1] != '[')
                            ternary++;
                        break;
                    case ':' when paren == 0 && bracket == 0 && brace == 0:
                        if (i + 1 < end && source[i + 1] == ':' || i > start && source[i - 1] == ':') break;
                        if (ternary > 0) ternary--;
                        else return i;
                        break;
                }
                i++;
            }
            return end;
        }

        private static int FindInterpolationClose(string source, int start, int end, int requiredBraces)
        {
            int nestedBraceDepth = 0;
            int i = start;
            while (i < end)
            {
                if (TrySkipComment(source, i, end, out int commentEnd))
                {
                    i = commentEnd;
                    continue;
                }

                if (TryReadQuotedToken(source, i, end, out var nestedToken))
                {
                    i = nestedToken.End;
                    continue;
                }

                if (source[i] == '{')
                {
                    nestedBraceDepth++;
                    i++;
                    continue;
                }

                if (source[i] == '}')
                {
                    int closeRun = CountRun(source, i, '}', end);
                    if (nestedBraceDepth == 0 && closeRun >= requiredBraces) return i;
                    if (nestedBraceDepth > 0)
                    {
                        nestedBraceDepth--;
                        i++;
                    }
                    else
                    {
                        i += closeRun;
                    }
                    continue;
                }

                i++;
            }
            return -1;
        }

        private static bool TrySkipComment(string source, int start, int limit, out int end)
        {
            end = start;
            if (source[start] != '/' || start + 1 >= limit) return false;
            if (source[start + 1] == '/')
            {
                end = start + 2;
                while (end < limit && source[end] != '\r' && source[end] != '\n') end++;
                return true;
            }
            if (source[start + 1] != '*') return false;

            end = start + 2;
            while (end + 1 < limit && !(source[end] == '*' && source[end + 1] == '/')) end++;
            end = end + 1 < limit ? end + 2 : limit;
            return true;
        }

        private static bool TryReadQuotedToken(string source, int start, int limit, out QuotedToken token)
        {
            token = default;
            if (source[start] == '\'')
            {
                int charEnd = SkipEscapedQuoted(source, start, '\'', limit);
                token = new QuotedToken(charEnd, start + 1, Math.Max(start + 1, charEnd - 1), false, false, false, 0);
                return true;
            }

            int quote = -1;
            int dollarCount = 0;
            bool verbatim = false;
            if (source[start] == '"')
            {
                quote = start;
            }
            else if (source[start] == '@' && start + 1 < limit && source[start + 1] == '"')
            {
                quote = start + 1;
                verbatim = true;
            }
            else if (source[start] == '@' && start + 2 < limit &&
                     source[start + 1] == '$' && source[start + 2] == '"')
            {
                quote = start + 2;
                dollarCount = 1;
                verbatim = true;
            }
            else if (source[start] == '$')
            {
                int cursor = start;
                while (cursor < limit && source[cursor] == '$')
                {
                    dollarCount++;
                    cursor++;
                }
                if (cursor < limit && source[cursor] == '@')
                {
                    verbatim = true;
                    cursor++;
                }
                if (cursor < limit && source[cursor] == '"') quote = cursor;
            }

            if (quote < 0) return false;

            int delimiterLength = CountRun(source, quote, '"', limit);
            bool raw = delimiterLength >= 3;
            int end;
            if (raw)
                end = dollarCount > 0
                    ? SkipInterpolatedRawString(source, quote + delimiterLength, delimiterLength, dollarCount, limit)
                    : SkipRawString(source, quote + delimiterLength, delimiterLength, limit);
            else if (dollarCount > 0)
                end = SkipInterpolatedString(source, quote, verbatim, limit);
            else
                end = verbatim
                    ? SkipVerbatimString(source, quote, limit)
                    : SkipEscapedQuoted(source, quote, '"', limit);

            int closingLength = raw ? delimiterLength : 1;
            int contentEnd = end >= closingLength ? end - closingLength : end;
            token = new QuotedToken(
                end,
                quote + delimiterLength,
                Math.Max(quote + delimiterLength, contentEnd),
                dollarCount > 0,
                verbatim,
                raw,
                dollarCount);
            return true;
        }

        private static int SkipInterpolatedString(string source, int quote, bool verbatim, int limit)
        {
            int holeDepth = 0;
            int i = quote + 1;
            while (i < limit)
            {
                if (holeDepth == 0)
                {
                    if (!verbatim && source[i] == '\\')
                    {
                        i = Math.Min(limit, i + 2);
                        continue;
                    }
                    if (source[i] == '"')
                    {
                        if (verbatim && i + 1 < limit && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }
                        return i + 1;
                    }
                    if (source[i] == '{')
                    {
                        if (i + 1 < limit && source[i + 1] == '{')
                        {
                            i += 2;
                            continue;
                        }
                        holeDepth = 1;
                    }
                    i++;
                    continue;
                }

                if (TrySkipComment(source, i, limit, out int commentEnd))
                {
                    i = commentEnd;
                    continue;
                }
                if (TryReadQuotedToken(source, i, limit, out var nestedToken))
                {
                    i = nestedToken.End;
                    continue;
                }
                if (source[i] == '{') holeDepth++;
                else if (source[i] == '}') holeDepth--;
                i++;
            }
            return limit;
        }

        private static int SkipEscapedQuoted(string source, int quote, char delimiter, int limit)
        {
            int i = quote + 1;
            while (i < limit)
            {
                if (source[i] == '\\')
                {
                    i = Math.Min(limit, i + 2);
                    continue;
                }
                if (source[i] == delimiter) return i + 1;
                i++;
            }
            return limit;
        }

        private static int SkipVerbatimString(string source, int quote, int limit)
        {
            int i = quote + 1;
            while (i < limit)
            {
                if (source[i] != '"')
                {
                    i++;
                    continue;
                }
                if (i + 1 < limit && source[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            return limit;
        }

        private static int SkipRawString(string source, int contentStart, int delimiterLength, int limit)
        {
            int i = contentStart;
            while (i < limit)
            {
                if (source[i] != '"')
                {
                    i++;
                    continue;
                }
                int run = CountRun(source, i, '"', limit);
                if (run >= delimiterLength) return i + delimiterLength;
                i += run;
            }
            return limit;
        }

        private static int SkipInterpolatedRawString(
            string source,
            int contentStart,
            int delimiterLength,
            int dollarCount,
            int limit)
        {
            int i = contentStart;
            while (i < limit)
            {
                if (source[i] == '"')
                {
                    int quoteRun = CountRun(source, i, '"', limit);
                    if (quoteRun >= delimiterLength) return i + delimiterLength;
                    i += quoteRun;
                    continue;
                }

                if (source[i] == '{' && CountRun(source, i, '{', limit) >= dollarCount)
                {
                    int close = FindInterpolationClose(source, i + dollarCount, limit, dollarCount);
                    if (close < 0) return limit;
                    i = close + dollarCount;
                    continue;
                }
                i++;
            }
            return limit;
        }

        private static int CountRun(string source, int start, char value, int limit)
        {
            int count = 0;
            while (start + count < limit && source[start + count] == value) count++;
            return count;
        }

        private static void Mark(Region[] regions, int start, int end, Region region)
        {
            for (int i = start; i < end && i < regions.Length; i++) regions[i] = region;
        }

        private readonly struct QuotedToken
        {
            internal QuotedToken(
                int end,
                int contentStart,
                int contentEnd,
                bool interpolated,
                bool verbatim,
                bool raw,
                int dollarCount)
            {
                End = end;
                ContentStart = contentStart;
                ContentEnd = contentEnd;
                Interpolated = interpolated;
                Verbatim = verbatim;
                Raw = raw;
                DollarCount = dollarCount;
            }

            internal int End { get; }
            internal int ContentStart { get; }
            internal int ContentEnd { get; }
            internal bool Interpolated { get; }
            internal bool Verbatim { get; }
            internal bool Raw { get; }
            internal int DollarCount { get; }
        }
    }
}
#endif
