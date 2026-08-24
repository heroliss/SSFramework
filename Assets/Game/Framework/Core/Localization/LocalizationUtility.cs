using System;
using System.Collections.Generic;
using Game.Framework.Logging;
using R3;

namespace Game.Framework.Localization
{
    /// <summary>
    /// <see cref="ILocalizationUtility"/> 的默认实现：<see cref="RP{T}"/> 承载当前语言（推送驱动 UI 重绑）+
    /// 「当前 locale → fallbackLocale → 裸 key」查询链。纯 C#、除 R3 外零依赖。
    /// </summary>
    /// <remarks>
    /// <b>注册：</b>已有 source 时用 <c>builder.RegisterOwned(new LocalizationUtility(source, "zh-CN", fallbackLocale: "en"), typeof(ILocalizationUtility))</c>；
    /// source 需从容器解析其他服务时用 <c>RegisterOwnedFactory</c>，既延迟接线又保留 Context 所有权。<br/>
    /// <b>缺 key 警告去重</b>：同一 (locale, key) 只警告一次——绑定标签每次推送都会重查，不去重会刷屏。<br/>
    /// <b>Dispose</b>：完结 <see cref="Locale"/> 订阅（随宿主 Context 释放）；之后 <see cref="Get(string)"/> 仍可安全调用
    /// （读普通字段），<see cref="SetLocale"/> 抛 <see cref="ObjectDisposedException"/>（R3 行为，换语言是明确的用户操作，过期引用应暴露）。
    /// </remarks>
    public sealed class LocalizationUtility : ILocalizationUtility, IDisposable
    {
        private readonly ILocalizedTextSource _source;
        private readonly string _fallbackLocale;
        private readonly RP<string> _locale;

        // 与 _locale.Value 同步的普通字段：Get 不读 RP（Dispose 后仍可安全查询）。
        private string _current;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private readonly HashSet<string> _warnedMissing = new();
#endif

        /// <param name="source">文本源（业务的配置表 adapter 或 <see cref="DictionaryLocalizedTextSource"/>）。</param>
        /// <param name="initialLocale">初始语言（业务读存档或按 <c>Application.systemLanguage</c> 映射后传入）。</param>
        /// <param name="fallbackLocale">可选一级回退语言（如 zh-TW → zh-CN）；null = 缺 key 直接裸 key。</param>
        public LocalizationUtility(ILocalizedTextSource source, string initialLocale, string fallbackLocale = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrEmpty(initialLocale))
                throw new ArgumentException("initialLocale 不能为空。", nameof(initialLocale));
            _current = initialLocale;
            _fallbackLocale = fallbackLocale;
            _locale = new RP<string>(initialLocale);
        }

        public ReadOnlyReactiveProperty<string> Locale => _locale;

        public void SetLocale(string locale)
        {
            if (string.IsNullOrEmpty(locale))
                throw new ArgumentException("locale 不能为空。", nameof(locale));
            _current = locale;
            _locale.Value = locale; // R3 同值不推送（幂等）；已 Dispose 时在此抛出
        }

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("本地化 key 不能为空。", nameof(key));

            if (_source.TryGet(_current, key, out var text))
                return text;
            if (_fallbackLocale != null && _fallbackLocale != _current && _source.TryGet(_fallbackLocale, key, out text))
                return text;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_warnedMissing.Add($"{_current}\n{key}"))
                Log.Warning(
                    $"缺文案：locale '{_current}' 无 key '{key}'"
                    + (_fallbackLocale != null ? $"（fallback '{_fallbackLocale}' 也未命中）" : string.Empty)
                    + "——已用裸 key 上屏。",
                    "Localization");
#endif
            return key; // 裸 key 上屏 = 最好的缺失报告
        }

        public string Get(string key, params object[] args)
        {
            var template = Get(key);
            try
            {
                return string.Format(template, args);
            }
            catch (FormatException)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Log.Warning($"key '{key}' 的模板格式非法（\"{template}\"）——已返回未格式化模板。", "Localization");
#endif
                return template; // 文案错不炸游戏
            }
        }

        public void Dispose() => _locale.Dispose();
    }

    /// <summary>
    /// 字典文本源：<see cref="ILocalizedTextSource"/> 的内置实现。测试 / demo / 文案量小的游戏直接用；
    /// 也让接缝天然有第二实现（业务的配置表 adapter 是第一实现）。链式 <see cref="Add"/> / <see cref="AddLocale"/> 填充。
    /// </summary>
    public sealed class DictionaryLocalizedTextSource : ILocalizedTextSource
    {
        private readonly Dictionary<string, Dictionary<string, string>> _byLocale = new();

        /// <summary>添加一条文本（同 locale + key 后写覆盖先写）。</summary>
        public DictionaryLocalizedTextSource Add(string locale, string key, string text)
        {
            if (string.IsNullOrEmpty(locale)) throw new ArgumentException("locale 不能为空。", nameof(locale));
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key 不能为空。", nameof(key));
            GetOrCreate(locale)[key] = text;
            return this;
        }

        /// <summary>批量添加某语言的文本（与既有条目合并，同 key 覆盖）。</summary>
        public DictionaryLocalizedTextSource AddLocale(string locale, IEnumerable<KeyValuePair<string, string>> entries)
        {
            if (string.IsNullOrEmpty(locale)) throw new ArgumentException("locale 不能为空。", nameof(locale));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var dict = GetOrCreate(locale);
            foreach (var kv in entries)
                dict[kv.Key] = kv.Value;
            return this;
        }

        public bool TryGet(string locale, string key, out string text)
        {
            text = null;
            return _byLocale.TryGetValue(locale, out var dict) && dict.TryGetValue(key, out text);
        }

        private Dictionary<string, string> GetOrCreate(string locale)
        {
            if (!_byLocale.TryGetValue(locale, out var dict))
                _byLocale[locale] = dict = new Dictionary<string, string>();
            return dict;
        }
    }
}
