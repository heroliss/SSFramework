using System;
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
#endif
using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.Logging;
using Game.Framework.Utility;
using TMPro;
using UnityEngine;
using TextCoreFontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Game.Framework.Fonts
{
    /// <summary>
    /// 按 locale 切换字体 fallback 链的接线组件（ADR-0025）：挂在根 Context 子节点上，Inspector 配置
    /// 主字体 + 各 locale 档案，订阅 <c>ILocalizationUtility.Locale</c>——换语言时把「②locale 补充字体 + ③OS 兜底」
    /// 写进各主字体的 fallback 表。业务代码零调用：文本渲染自动逐层找字形。
    /// </summary>
    /// <remarks>
    /// <b>前置：</b>同 Context（或父级）需已注册 <see cref="ILocalizationUtility"/>（locale 信号源，ADR-0024）。<br/>
    /// <b>全局生效：</b>链条写在字体<b>资产</b>上，作用范围是「所有用到这些主字体的文本」而非本 Context 子树——
    /// 全工程挂一份（根 Context）即可；同一主字体被两份组件接管会互相覆盖快照，不要多挂。<br/>
    /// <b>生命周期：</b>Start 快照原始表并首次应用（订阅即得当前 locale）；OnDestroy 还原原始表、
    /// 销毁运行时创建的 OS 字体资产——Editor Play 会话不污染共享字体资产。<br/>
    /// 核心逻辑在 <see cref="LocaleFontChain"/>（纯 C#，可脱离场景单测）。
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MonoLocaleFonts : MonoUtilityBase
    {
        [SerializeField, Tooltip("① 主字体（TMP / UGUI 侧）：链条写到这些资产的 fallback 表上。\n通常是精简常用字集烘焙的 static atlas（正文 / 标题各一）；用了未列出字体的文本不受链条保护。")]
        private TMP_FontAsset[] _tmpMainFonts = Array.Empty<TMP_FontAsset>();

        [SerializeField, Tooltip("① 主字体（TextCore / UI Toolkit 侧）。与 TMP 栏是两套互不相认的资产类型；单后端项目另一栏留空。")]
        private TextCoreFontAsset[] _toolkitMainFonts = Array.Empty<TextCoreFontAsset>();

        [SerializeField, Tooltip("各 locale 的字体档案（②补充字体 + ③OS 字体候选）。\n未配置的 locale 降级为仅主字体原始表（警告一次，不炸）。")]
        private LocaleFontProfile[] _locales = Array.Empty<LocaleFontProfile>();

        private LocaleFontChain _chain;

#if UNITY_EDITOR
        /// <summary>Editor Header 读取的当前 fallback 链；不进入 Player 编译结果。</summary>
        internal IReadOnlyList<string> EditorDiagnostics
        {
            get
            {
                var lines = new List<string>();
                foreach (var main in _tmpMainFonts)
                    if (main != null) lines.Add(FormatChain("[TMP] ", main.name, main.fallbackFontAssetTable));
                foreach (var main in _toolkitMainFonts)
                    if (main != null) lines.Add(FormatChain("[Toolkit] ", main.name, main.fallbackFontAssetTable));
                return lines;
            }
        }
#endif

        private void Start()
        {
            // Start 而非 Awake：Locale 信号源可能由工厂注册 / 同优先级脚本注册，Start 时已全部就绪（AGENTS #3）。
            if (_tmpMainFonts.Length == 0 && _toolkitMainFonts.Length == 0)
            {
                Log.Warning(
                    "未配置任何主字体——组件无事可做。在 Inspector 里配置 TMP / Toolkit 主字体列表。",
                    category: nameof(MonoLocaleFonts),
                    context: this);
                return;
            }

            var loc = this.GetUtility<ILocalizationUtility>(); // 未注册即抛：locale 信号源是本组件的硬前置
            _chain = new LocaleFontChain(_tmpMainFonts, _toolkitMainFonts, _locales);
            Bag.Subscribe(loc.Locale, l => _chain.Apply(l)); // 订阅即得当前值 = 首次应用；随 Bag 退订
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // 先释放 Bag（退订 Locale），再还原字体表——不会有退订后的推送再写表
            _chain?.Dispose();
            _chain = null;
        }

#if UNITY_EDITOR
        private static string FormatChain<T>(string prefix, string mainName, List<T> table)
            where T : UnityEngine.Object
        {
            var result = new StringBuilder(prefix).Append(mainName).Append(" → ");
            if (table == null || table.Count == 0) return result.Append("（空）").ToString();
            for (int i = 0; i < table.Count; i++)
            {
                if (i > 0) result.Append(", ");
                result.Append(table[i] != null ? table[i].name : "null");
            }
            return result.ToString();
        }
#endif
    }
}
