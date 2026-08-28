using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 代码生成器共用的 C# 名称语法 Module：验证显式配置能否通过词法解析，并把派生展示名清洗成安全标识符；
    /// 不替各业务 Module 判断自己的命名约定。
    /// </summary>
    public static class FrameworkCSharpSyntax
    {
        private static readonly HashSet<string> ReservedKeywords = new()
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
            "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
            "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
            "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
            "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref",
            "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
            "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
            "virtual", "void", "volatile", "while",
        };

        /// <summary>
        /// 验证非空、点号分隔的 C# 命名空间。保留关键字必须使用逐字标识符形式（如 <c>@class</c>）；
        /// <c>var</c> 等上下文关键字可直接使用。
        /// </summary>
        public static bool TryValidateNamespace(string value, out string error)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "命名空间不能为空。";
                return false;
            }

            string[] segments = value.Trim().Split('.');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (segment.Length == 0)
                {
                    error = $"第 {i + 1} 段为空；不能以点号开头/结尾，也不能包含连续点号。";
                    return false;
                }

                bool escaped = segment[0] == '@';
                string identifier = escaped ? segment[1..] : segment;
                if (identifier.Length == 0 || !IsIdentifierStart(identifier[0]))
                {
                    error = $"第 {i + 1} 段“{segment}”不是合法 C# 标识符：首字符必须是字母或下划线。";
                    return false;
                }

                for (int characterIndex = 1; characterIndex < identifier.Length; characterIndex++)
                {
                    if (IsIdentifierPart(identifier[characterIndex])) continue;
                    error = $"第 {i + 1} 段“{segment}”包含非法字符“{identifier[characterIndex]}”。";
                    return false;
                }

                if (!escaped && ReservedKeywords.Contains(identifier))
                {
                    error = $"第 {i + 1} 段“{segment}”是 C# 保留关键字；请改名或写成 @{segment}。";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// 把任意展示名稳定转换为可直接写进生成源码的 C# 标识符：非法字符替换为下划线，
        /// 数字或其它仅能出现在后续位置的字符会先补下划线，保留关键字也会加下划线前缀。
        /// 原始业务值不应使用本方法改写；它只用于派生类名、字段名或常量名。
        /// </summary>
        public static string SanitizeIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value)) return "_";

            var builder = new StringBuilder(value.Length + 1);
            foreach (char character in value)
            {
                if (builder.Length == 0)
                {
                    if (IsIdentifierStart(character))
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        builder.Append('_');
                        if (IsIdentifierPart(character)) builder.Append(character);
                    }
                    continue;
                }

                builder.Append(IsIdentifierPart(character) ? character : '_');
            }

            string identifier = builder.ToString();
            return ReservedKeywords.Contains(identifier) ? "_" + identifier : identifier;
        }

        private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value) ||
            char.GetUnicodeCategory(value) == UnicodeCategory.LetterNumber;

        private static bool IsIdentifierPart(char value)
        {
            if (IsIdentifierStart(value) || char.IsDigit(value)) return true;
            return char.GetUnicodeCategory(value) is
                UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.ConnectorPunctuation or
                UnicodeCategory.Format;
        }
    }
}
