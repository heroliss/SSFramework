using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Fonts.Editor
{
    /// <summary>
    /// 常用字集生成器（ADR-0025 §5）：按 <see cref="FontCharsetProfile"/> 扫描文本源，
    /// 去重出码点集合（正确处理代理对），按码点升序写出 charset 文件。
    /// 产物喂 TMP Font Asset Creator（Characters from File）手动烘焙——v1 不自动化烘焙。
    /// </summary>
    /// <remarks>
    /// 提取规则按扩展名三分：<c>.cs</c> 只取字符串字面量（正则匹配普通 / 逐字字符串——注释、标识符是给人看的，
    /// 不上屏，进字集只会虚增体积）；<c>.xlsx</c> 读 zip 内 <c>xl/sharedStrings.xml</c> 的全部文本节点
    /// （Excel 所有共享字符串单元格，Luban 源表直配）；其余扩展名全文并入。
    /// 控制字符与空白一律剔除（空格经「ASCII 可打印区」开关进入）。
    /// </remarks>
    public static class FontCharsetGenerator
    {
        // 普通字符串 "..."（\ 转义）与逐字字符串 @"..."（"" 转义）；插值字符串的引号体也被普通规则覆盖。
        // 正则不是完整 C# 词法（如 '"' 字符字面量里的引号会误配一小段），charset 场景多收个别字符无害。
        private static readonly Regex CsStringLiteral = new(
            "@\"(?:[^\"]|\"\")*\"|\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"",
            RegexOptions.Compiled);

        /// <summary>按 profile 生成 charset 文件；返回收进的唯一字符数。产物路径见 <see cref="FontCharsetProfile.OutputPath"/>。</summary>
        public static int Generate(FontCharsetProfile profile)
        {
            var codepoints = new SortedSet<int>();

            if (profile.IncludeAsciiPrintable)
                for (int c = 0x20; c <= 0x7E; c++)
                    codepoints.Add(c);

            AddText(codepoints, profile.ExtraChars);

            int fileCount = 0;
            foreach (var dir in profile.ScanDirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                if (!Directory.Exists(dir))
                {
                    Debug.LogWarning($"[FontCharset] 扫描目录不存在，跳过：{dir}");
                    continue;
                }
                foreach (var pattern in profile.FilePatterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    foreach (var file in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
                    {
                        AddFile(codepoints, file);
                        fileCount++;
                    }
                }
            }

            var sb = new StringBuilder(codepoints.Count + 16);
            foreach (var cp in codepoints)
                sb.Append(char.ConvertFromUtf32(cp));

            var outputPath = profile.OutputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
            AssetDatabase.Refresh();

            Debug.Log($"[FontCharset] 扫描 {fileCount} 个文件，收进 {codepoints.Count} 个唯一字符 → {outputPath}\n" +
                      "下一步：TMP Font Asset Creator（Window/TextMeshPro/Font Asset Creator）选主字体 ttf，" +
                      "Character Set 选 Characters from File 引用该文件，烘焙 static atlas。");
            return codepoints.Count;
        }

        private static void AddFile(SortedSet<int> codepoints, string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".cs":
                    foreach (Match m in CsStringLiteral.Matches(File.ReadAllText(path)))
                        AddText(codepoints, m.Value);
                    break;
                case ".xlsx":
                    AddXlsxSharedStrings(codepoints, path);
                    break;
                default:
                    AddText(codepoints, File.ReadAllText(path));
                    break;
            }
        }

        /// <summary>xlsx 是 zip：全部文本单元格集中在 xl/sharedStrings.xml，读它的文本节点即拿到 Excel 所有字符串。</summary>
        private static void AddXlsxSharedStrings(SortedSet<int> codepoints, string path)
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return; // 无字符串单元格的表没有此 entry

            using var reader = XmlReader.Create(entry.Open());
            while (reader.Read())
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    AddText(codepoints, reader.Value);
        }

        private static void AddText(SortedSet<int> codepoints, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                int cp;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(c, text[i + 1]);
                    i++; // 代理对占两个 char
                }
                else if (char.IsSurrogate(c))
                {
                    continue; // 孤立代理（源文件本身含非法 UTF-16 序列时才会出现）：跳过不炸
                }
                else
                {
                    cp = c;
                }

                // 控制字符：C0(0x00-0x1F) + DEL(0x7F) + C1(0x80-0x9F，mojibake 源里的不可见字符）
                if (cp < 0x20 || cp == 0x7F || (cp >= 0x80 && cp <= 0x9F)) continue;
                if (char.IsWhiteSpace(c)) continue;      // 空白不进字集（空格经 ASCII 开关进入）
                codepoints.Add(cp);
            }
        }
    }
}
