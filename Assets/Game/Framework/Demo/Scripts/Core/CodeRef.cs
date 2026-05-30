using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// 一处源码引用——演示动作旁"跳转到源码"用的数据。
    /// </summary>
    /// <remarks>
    /// 用<b>锚点字符串</b>而不是行号定位：代码增删行后行号会失效，而一段独特的字符串片段
    /// （如 <c>void Execute(ICommandContext</c>）稳定得多。<see cref="Anchor"/> 为空则跳到文件开头。
    /// 路径是相对项目根的资源路径（<c>Assets/...</c>）；文件被移动后需更新——demo 是随框架维护的活文档。
    /// </remarks>
    public readonly struct CodeRef
    {
        /// <summary>相对项目根的 .cs 资源路径，如 <c>Assets/Game/Framework/Scripts/Command/ICommand.cs</c>。</summary>
        public readonly string Path;

        /// <summary>跳转锚点：定位到源码中第一处<b>真正的代码声明</b>（会跳过锚点串作为 CodeRef 字符串实参自身出现的那行）。为空则跳到第 1 行。</summary>
        public readonly string Anchor;

        /// <summary>链接显示名，如 "Command 定义" / "View 调用"。为空时显示 "源码"。</summary>
        public readonly string Label;

        public CodeRef(string path, string anchor = null, string label = null)
        {
            Path = path;
            Anchor = anchor;
            Label = label;
        }

        /// <summary>是否指向了一个有效路径（可用于决定要不要显示跳转链接）。</summary>
        public bool HasTarget => !string.IsNullOrEmpty(Path);

        /// <summary>
        /// 指向"调用所在的这个源文件"——路径由编译器经 <see cref="CallerFilePathAttribute"/> 自动注入。
        /// </summary>
        /// <remarks>
        /// demo 里"被演示的类型和它的 CodeRef 写在同一文件"是最常见情形，用它最省心：
        /// 不必手写一长串资源路径，文件改名/移动后重新编译会自动跟随，<b>不会失效</b>。<br/>
        /// 跨文件引用（如跳到框架源码 <c>ICommand.cs</c>）仍用 <c>new CodeRef(path, ...)</c> 显式给路径。<br/>
        /// 可靠性：<c>CallerFilePath</c> 给的是<b>编译机</b>上的绝对路径，而源码跳转本就只在编译机的 Editor 里发生，二者一致。
        /// </remarks>
        /// <param name="anchor">见 <see cref="Anchor"/>：定位到的代码声明片段。</param>
        /// <param name="label">见 <see cref="Label"/>：链接显示名。</param>
        /// <param name="callerFilePath">编译器自动填充，<b>调用方不要传</b>。</param>
        public static CodeRef Here(string anchor = null, string label = null, [CallerFilePath] string callerFilePath = null)
            => new CodeRef(ToAssetsRelativePath(callerFilePath), anchor, label);

        // 把编译期注入的绝对源文件路径转成项目资源路径（Assets/...）：定位 "/Assets/" 段起头截取并统一为正斜杠。
        // 找不到 Assets 段则原样返回（退化为绝对路径，AssetDatabase 找不到时 CodeNavigator 会给出可定位的警告）。
        private static string ToAssetsRelativePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return absolutePath;
            string normalized = absolutePath.Replace('\\', '/');
            int i = normalized.IndexOf("/Assets/", StringComparison.Ordinal);
            if (i >= 0) return normalized.Substring(i + 1); // 去前导 '/'，从 "Assets/" 开始
            return normalized;
        }
    }

    /// <summary>
    /// 打开 <see cref="CodeRef"/> 指向的源码并定位到锚点行。仅 Editor 内有效，Build 下是 no-op。
    /// </summary>
    /// <remarks>
    /// 用 <c>AssetDatabase.LoadAssetAtPath&lt;MonoScript&gt;</c> 读取脚本资源，在全文里找锚点字符串所在行，
    /// 再 <c>AssetDatabase.OpenAsset(script, line)</c> 用默认 IDE 打开。所有 Editor 调用都包在
    /// <c>#if UNITY_EDITOR</c> 内，发布包里不残留编辑器依赖。
    /// </remarks>
    public static class CodeNavigator
    {
        /// <summary>当前环境是否能跳转源码（仅 Editor 为 true）。UI 据此决定是否显示跳转链接。</summary>
        public static bool IsAvailable =>
#if UNITY_EDITOR
            true;
#else
            false;
#endif

        public static void Open(CodeRef code)
        {
#if UNITY_EDITOR
            if (!code.HasTarget) return;
            var script = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.MonoScript>(code.Path);
            if (script == null)
            {
                Debug.LogWarning($"[CodeNavigator] 找不到源码资源：{code.Path}（文件可能被移动/重命名，需更新 CodeRef）。");
                return;
            }
            int line = FindLineByAnchor(script.text, code.Anchor);
            UnityEditor.AssetDatabase.OpenAsset(script, line);
#endif
        }

#if UNITY_EDITOR
        // 找锚点字符串所在行（1-based）。锚点为空或未命中返回 1。
        // 关键细节：demo 常把类型和它的 CodeRef 写在同一文件里，于是锚点串会先以 CodeRef
        // 的字符串实参形态（"anchor"）出现在调用点——位置还在真正声明之前。朴素 IndexOf 会
        // 命中那处字面量、跳到调用行而非声明行。所以跳过"被双引号紧邻包裹"的命中（即 CodeRef
        // 实参自身），定位到第一处真正的代码声明。
        private static int FindLineByAnchor(string content, string anchor)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(anchor)) return 1;

            int searchFrom = 0;
            int firstHit = -1;
            while (true)
            {
                int idx = content.IndexOf(anchor, searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;
                if (firstHit < 0) firstHit = idx; // 兜底：万一全是字面量，退回首个命中（至少落在同一文件）

                bool quotedBefore = idx > 0 && content[idx - 1] == '"';
                int end = idx + anchor.Length;
                bool quotedAfter = end < content.Length && content[end] == '"';
                if (!(quotedBefore && quotedAfter)) // 非 "anchor" 字面量 → 真正的声明行
                    return LineOf(content, idx);

                searchFrom = end;
            }
            return firstHit < 0 ? 1 : LineOf(content, firstHit);
        }

        // 计算字符偏移所在的 1-based 行号。
        private static int LineOf(string content, int idx)
        {
            int line = 1;
            for (int i = 0; i < idx; i++)
                if (content[i] == '\n') line++;
            return line;
        }
#endif
    }
}
