using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using TextCoreFontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Game.Framework.Fonts
{
    /// <summary>
    /// 单个 locale 的字体档案（ADR-0025 三层策略里的 ②locale 补充字体 + ③OS 字体候选）：
    /// 切到该 locale 时，<see cref="LocaleFontChain"/> 把「补充字体 + 首个可用的 OS 字体」追加到各主字体的 fallback 表上。
    /// 通常在 <see cref="MonoLocaleFonts"/> 的 Inspector 里配置；纯 C# / 测试用构造函数创建。
    /// </summary>
    /// <remarks>
    /// TMP（UGUI 侧）与 TextCore <c>FontAsset</c>（UI Toolkit 侧）是两套互不相认的字体资产，所以补充字体分两栏；
    /// 项目只用一种 UI 后端时另一栏留空即可。OS 字体族名两侧共用（各自创建对应类型的动态资产）。
    /// </remarks>
    [Serializable]
    public sealed class LocaleFontProfile
    {
        [SerializeField, Tooltip("locale code，与 ILocalizationUtility.SetLocale 的值一致（如 zh-CN / en）。")]
        private string _locale;

        [SerializeField, Tooltip("② 本语言的补充字体（TMP / UGUI 侧），如 NotoSansSC 的动态 atlas 资产。\n只补主字体没有的差集字形，链上按序查找。")]
        private TMP_FontAsset[] _tmpFonts = Array.Empty<TMP_FontAsset>();

        [SerializeField, Tooltip("② 本语言的补充字体（TextCore / UI Toolkit 侧）。\n与 TMP 栏是两套互不相认的资产类型，各配各的。")]
        private TextCoreFontAsset[] _toolkitFonts = Array.Empty<TextCoreFontAsset>();

        [SerializeField, Tooltip("③ OS 字体族名候选，按序尝试到第一个可用（如 Microsoft YaHei / PingFang SC / Noto Sans CJK SC）。\n⚠ 用英文族名——本地化名（如「微软雅黑」）在字体引擎里查不到。全部不可用只警告不炸（降级为只有①②）。")]
        private string[] _osFontNames = Array.Empty<string>();

        /// <summary>Unity 序列化需要的无参构造。</summary>
        public LocaleFontProfile() { }

        /// <summary>纯 C# / 测试路径的构造；实例只保存传入资产引用，不取得字体资产所有权。</summary>
        /// <param name="locale">与 <see cref="Game.Framework.Localization.ILocalizationUtility"/> 使用同一命名契约的 locale code。</param>
        /// <param name="tmpFonts">TMP 补充字体；null 按空数组处理。</param>
        /// <param name="toolkitFonts">TextCore 补充字体；null 按空数组处理。</param>
        /// <param name="osFontNames">按顺序尝试的英文 OS 字体族名；null 按空数组处理。</param>
        /// <exception cref="ArgumentException"><paramref name="locale"/> 为 null 或空字符串。</exception>
        public LocaleFontProfile(string locale, TMP_FontAsset[] tmpFonts = null, TextCoreFontAsset[] toolkitFonts = null, string[] osFontNames = null)
        {
            if (string.IsNullOrEmpty(locale))
                throw new ArgumentException("locale 不能为空。", nameof(locale));
            _locale = locale;
            _tmpFonts = tmpFonts ?? Array.Empty<TMP_FontAsset>();
            _toolkitFonts = toolkitFonts ?? Array.Empty<TextCoreFontAsset>();
            _osFontNames = osFontNames ?? Array.Empty<string>();
        }

        /// <summary>本档案匹配的 locale code。</summary>
        public string Locale => _locale;

        /// <summary>TMP（UGUI）侧按顺序追加的补充字体；只读查看，不转移资产所有权。</summary>
        public IReadOnlyList<TMP_FontAsset> TmpFonts => _tmpFonts;

        /// <summary>TextCore（UI Toolkit）侧按顺序追加的补充字体；只读查看，不转移资产所有权。</summary>
        public IReadOnlyList<TextCoreFontAsset> ToolkitFonts => _toolkitFonts;

        /// <summary>按顺序尝试的英文 OS 字体族名；首个可用项成为动态兜底字体。</summary>
        public IReadOnlyList<string> OsFontNames => _osFontNames;
    }
}
