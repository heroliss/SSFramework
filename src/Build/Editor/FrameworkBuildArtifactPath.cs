using System;
using System.IO;
using System.Linq;
using Game.Framework.Editor;

namespace Game.Framework.Build
{
    /// <summary>
    /// 验证会同时成为磁盘目录与 CDN URL 段的包名 / 版本号。Profile 与 CI 参数都不可信任为路径；
    /// 本类型把它们限制为单一、跨平台可移植的叶子名，并在递归清理前再次证明目标仍是指定根目录的直接子项。
    /// </summary>
    public static class FrameworkBuildArtifactPath
    {
        private static readonly char[] ForbiddenCharacters = { '/', '\\', '<', '>', ':', '"', '|', '?', '*', '#', '%' };
        private static readonly string[] WindowsDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// 规范化并验证一个包名或版本号。允许字母、数字、Unicode 及常见的 <c>._-+@~</c> 等叶子字符；
        /// 拒绝空白、路径分隔符、URL 结构字符、控制字符、<c>.</c>/<c>..</c> 与 Windows 设备名。
        /// </summary>
        public static bool TryNormalizeSegment(string value, string label, out string normalized, out string error)
        {
            normalized = value?.Trim() ?? string.Empty;
            label = string.IsNullOrWhiteSpace(label) ? "名称" : label.Trim();
            if (normalized.Length == 0)
            {
                error = label + "不能为空。";
                return false;
            }
            if (normalized is "." or "..")
            {
                error = $"{label}不能是 {normalized}。";
                return false;
            }
            if (normalized.EndsWith(".", StringComparison.Ordinal) ||
                normalized.Any(char.IsWhiteSpace) ||
                normalized.Any(char.IsControl) ||
                normalized.IndexOfAny(ForbiddenCharacters) >= 0)
            {
                error = $"{label}必须是单一、可移植的目录名，不能含空白、路径分隔符或 <>:\"|?*#%：{value}";
                return false;
            }

            string deviceStem = normalized.Split('.')[0];
            foreach (string reserved in WindowsDeviceNames)
                if (deviceStem.Equals(reserved, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"{label}不能使用 Windows 保留设备名：{value}";
                    return false;
                }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// 验证 <paramref name="segment"/> 后解析为 <paramref name="rootDirectory"/> 的直接子目录。
        /// 失败不创建或删除目录；成功返回规范化绝对路径，供构建与清理共同使用。
        /// </summary>
        public static bool TryResolveChildDirectory(
            string rootDirectory,
            string segment,
            string label,
            out string absoluteDirectory,
            out string error)
        {
            absoluteDirectory = string.Empty;
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                error = "输出根目录不能为空。";
                return false;
            }
            if (!TryNormalizeSegment(segment, label, out string normalized, out error)) return false;

            try
            {
                string root = Path.GetFullPath(rootDirectory);
                string child = Path.GetFullPath(Path.Combine(root, normalized));
                string parent = Directory.GetParent(child)?.FullName;
                if (parent == null || !FrameworkProjectPath.PathsEqual(parent, root))
                {
                    error = $"{label}解析后不在输出根目录的直接下一层：{segment}";
                    return false;
                }
                absoluteDirectory = child;
                error = string.Empty;
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"{label}无法解析为输出目录：{exception.Message}";
                return false;
            }
        }
    }
}
