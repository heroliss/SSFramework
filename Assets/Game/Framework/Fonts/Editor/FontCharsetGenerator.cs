using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Game.Framework.Editor;
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

        /// <summary>
        /// 按 profile 生成 charset 文件并返回唯一字符数。路径或文件读取失败时抛
        /// <see cref="InvalidOperationException"/>；交互入口应优先使用 <see cref="TryGenerate"/> 展示可恢复错误。
        /// </summary>
        public static int Generate(FontCharsetProfile profile)
        {
            var (ok, message, count) = TryGenerate(profile);
            if (!ok) throw new System.InvalidOperationException(message);
            return count;
        }

        /// <summary>
        /// 尝试生成 charset。扫描目录必须留在工程内，输出文件必须位于 <c>Assets</c> 子目录；失败不抛出常见
        /// 路径或 IO 异常，而是返回可供工作台展示的原因。成功后已经刷新 AssetDatabase。
        /// </summary>
        public static (bool ok, string message, int count) TryGenerate(FontCharsetProfile profile)
        {
            if (profile == null) return (false, "Font Charset Profile 不能为空。", 0);
            if (!FrameworkProjectPath.TryResolveAssetsFile(
                    profile.OutputPath, ".txt", out string outputAssetPath, out string outputPath, out string pathError))
                return (false, "字集输出路径无效：" + pathError, 0);

            var scanDirectories = new List<(string configured, string absolute)>();
            foreach (string configuredDirectory in profile.ScanDirs ?? System.Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(configuredDirectory)) continue;
                if (!FrameworkProjectPath.TryResolve(
                        configuredDirectory, out _, out string absoluteDirectory, out string scanError))
                    return (false, "字集扫描目录无效：" + scanError, 0);
                scanDirectories.Add((configuredDirectory, absoluteDirectory));
            }

            try
            {
                int count = GenerateCore(profile, scanDirectories, outputPath);
                AssetDatabase.ImportAsset(outputAssetPath);
                return (true, $"已写入 {count} 个唯一字符：{outputAssetPath}", count);
            }
            catch (System.Exception exception) when (
                exception is System.ArgumentException or IOException or System.UnauthorizedAccessException or
                InvalidDataException or XmlException)
            {
                return (false, $"读取文本源或写入字集失败：{exception.Message}", 0);
            }
        }

        private static int GenerateCore(
            FontCharsetProfile profile,
            IReadOnlyList<(string configured, string absolute)> scanDirectories,
            string outputPath)
        {
            var codepoints = new SortedSet<int>();

            if (profile.IncludeAsciiPrintable)
                for (int c = 0x20; c <= 0x7E; c++)
                    codepoints.Add(c);

            AddText(codepoints, profile.ExtraChars);

            foreach (var (configured, absolute) in scanDirectories)
            {
                if (!Directory.Exists(absolute))
                {
                    Debug.LogWarning($"[FontCharset] 扫描目录不存在，跳过：{configured}");
                    continue;
                }
                foreach (var pattern in profile.FilePatterns ?? System.Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    foreach (var file in Directory.GetFiles(absolute, pattern, SearchOption.AllDirectories))
                    {
                        // 默认输出位于默认扫描根 Assets 内；不排除自己会让已删除字符被旧产物永久带回，字集只能增不能减。
                        if (FrameworkProjectPath.PathsEqual(file, outputPath)) continue;
                        AddFile(codepoints, file);
                    }
                }
            }

            var sb = new StringBuilder(codepoints.Count + 16);
            foreach (var cp in codepoints)
                sb.Append(char.ConvertFromUtf32(cp));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
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
