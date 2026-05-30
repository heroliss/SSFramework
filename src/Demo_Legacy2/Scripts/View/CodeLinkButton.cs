using System;
using UnityEngine;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// Demo 演示按钮旁的"跳转到源码"附属按钮。点击后在 Editor 内打开对应 .cs 文件并定位到锚点字符串所在行。
    /// </summary>
    /// <remarks>
    /// <b>设计要点：</b>
    /// <list type="bullet">
    ///   <item>用<b>锚点字符串</b>而不是行号——代码改动行号会失效；独特字符串片段（如 <c>Bag.Subscribe&lt;GoldChangedEvent&gt;(</c>）稳定得多。</item>
    ///   <item>用 <c>UnityEditor.MonoScript</c>——Unity 原生类型，Inspector 直接拖 .cs 文件，自动跟踪文件移动/重命名。</item>
    ///   <item><b>Build 下自动隐藏</b>——<c>Awake</c> 中 <c>SetActive(false)</c>。组件序列化字段全部 <c>#if UNITY_EDITOR</c>，Build 下不占空间。</item>
    ///   <item>同一个演示按钮旁可以挂多个，跳到不同代码位置（Command 定义 / View 调用 / Token 消费 等）。</item>
    /// </list>
    /// </remarks>
    public sealed class CodeLinkButton : MonoBehaviour
    {
#if UNITY_EDITOR
        [Tooltip("拖拽 .cs 文件作为跳转目标。Build 下此字段不存在。")]
        [SerializeField] private UnityEditor.MonoScript _script;
#endif

        [Tooltip("锚点字符串：跳转到包含此字符串的第一行。比行号稳定（代码改动后只要保留这个独特片段就能找到）。")]
        [SerializeField] private string _anchor;

        [Tooltip("Tooltip 文字，鼠标悬停时展示。如「跳转到：Command 定义」。")]
        [SerializeField] private string _tooltip;

        /// <summary>Tooltip 文字（供 UI 组件读取）。</summary>
        public string Tooltip => _tooltip;

        private void Awake()
        {
#if !UNITY_EDITOR
            gameObject.SetActive(false);
#endif
        }

        /// <summary>绑定到 Button.onClick 的入口。Build 下是 no-op（Awake 已隐藏 GameObject）。</summary>
        public void Open()
        {
#if UNITY_EDITOR
            if (_script == null)
            {
                Debug.LogWarning($"[CodeLinkButton] '{name}': Script 字段未配置。", this);
                return;
            }
            int line = FindLineByAnchor(_script.text, _anchor);
            UnityEditor.AssetDatabase.OpenAsset(_script, line);
#endif
        }

#if UNITY_EDITOR
        /// <summary>在源代码全文中找到包含锚点字符串的第一行（1-based）。锚点为空或未找到时返回 1。</summary>
        private static int FindLineByAnchor(string content, string anchor)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(anchor)) return 1;
            int idx = content.IndexOf(anchor, StringComparison.Ordinal);
            if (idx < 0) return 1;

            int line = 1;
            for (int i = 0; i < idx; i++)
                if (content[i] == '\n') line++;
            return line;
        }
#endif
    }
}
