using System;
using System.Collections.Generic;
using Game.Framework.Logging;
using R3;

namespace Game.Framework.Localization
{
    /// <summary>
    /// <see cref="ILocalizationUtility"/> 的默认实现：<see cref="RP{T}"/> 分别承载当前语言与文本修订，
    /// 集中执行「源可用性 → 当前 locale → fallbackLocale → 裸 key」查询链。纯 C#、除 R3 外零依赖。
    /// </summary>
    /// <remarks>
    /// <b>注册：</b>已有 source 时用 <c>builder.RegisterOwnedUtility(new LocalizationUtility(source, "zh-CN", fallbackLocale: "en"))</c>；
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
        private readonly RP<int> _textRevision = new(0);
        private readonly IDisposable _sourceInvalidationSubscription;

        // 与 _locale.Value 同步的普通字段：Get 不读 RP（Dispose 后仍可安全查询）。
        private string _current;
        private bool _disposed;

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
            _sourceInvalidationSubscription = (_source.Invalidated
                ?? throw new ArgumentException("文本源 Invalidated 不能为 null。", nameof(source)))
                .Subscribe(_ => BumpTextRevision());
        }

        public ReadOnlyReactiveProperty<string> Locale => _locale;
        public ReadOnlyReactiveProperty<int> TextRevision => _textRevision;

        public void SetLocale(string locale)
        {
            if (string.IsNullOrEmpty(locale))
                throw new ArgumentException("locale 不能为空。", nameof(locale));
            if (_disposed)
                throw new ObjectDisposedException(nameof(LocalizationUtility), "本地化服务已随 Context 释放——检查是否持有了过期引用。");
            if (_current == locale) return;
            _current = locale;
            _locale.Value = locale;
            BumpTextRevision();
        }

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("本地化 key 不能为空。", nameof(key));

            var currentStatus = Lookup(_current, key, out var text);
            if (currentStatus == LocalizedTextLookupStatus.Found) return text;
            if (currentStatus == LocalizedTextLookupStatus.Unavailable) return key;

            if (_fallbackLocale != null && _fallbackLocale != _current)
            {
                var fallbackStatus = Lookup(_fallbackLocale, key, out text);
                if (fallbackStatus == LocalizedTextLookupStatus.Found) return text;
                if (fallbackStatus == LocalizedTextLookupStatus.Unavailable) return key;
            }

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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceInvalidationSubscription.Dispose();
            _textRevision.Dispose();
            _locale.Dispose();
        }

        private LocalizedTextLookupStatus Lookup(string locale, string key, out string text)
        {
            var status = _source.Lookup(locale, key, out text);
            if (status == LocalizedTextLookupStatus.Found && text == null)
                throw new InvalidOperationException(
                    $"本地化文本源违反契约：Lookup('{locale}', '{key}') 返回 Found，但 text 为 null。");
            if (status is < LocalizedTextLookupStatus.Unavailable or > LocalizedTextLookupStatus.Found)
                throw new InvalidOperationException(
                    $"本地化文本源返回了未知查询状态 {(int)status}（locale='{locale}', key='{key}'）。");
            return status;
        }

        private void BumpTextRevision()
        {
            if (_disposed) return;
            _textRevision.Value = unchecked(_textRevision.Value + 1);
        }
    }

    /// <summary>
    /// 字典文本源：<see cref="ILocalizedTextSource"/> 的内置实现。测试 / demo / 文案量小的游戏直接用；
    /// 也让接缝天然有第二实现（业务的配置表 adapter 是第一实现）。链式 <see cref="Add"/> / <see cref="AddLocale"/> 填充。
    /// </summary>
    public sealed class DictionaryLocalizedTextSource : ILocalizedTextSource
    {
        private readonly Dictionary<string, Dictionary<string, string>> _byLocale = new();
        private readonly Subject<Unit> _invalidated = new();

        /// <summary>字典内容实际变化时推送；在 Utility 建好后继续 Add，也会让既有文本绑定自动重取。</summary>
        public Observable<Unit> Invalidated => _invalidated;

        /// <summary>添加一条文本（同 locale + key 后写覆盖先写）。</summary>
        public DictionaryLocalizedTextSource Add(string locale, string key, string text)
        {
            if (string.IsNullOrEmpty(locale)) throw new ArgumentException("locale 不能为空。", nameof(locale));
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key 不能为空。", nameof(key));
            if (text == null) throw new ArgumentNullException(nameof(text));
            var dict = GetOrCreate(locale);
            if (dict.TryGetValue(key, out var existing) && existing == text) return this;
            dict[key] = text;
            _invalidated.OnNext(Unit.Default);
            return this;
        }

        /// <summary>批量添加某语言的文本（与既有条目合并，同 key 覆盖）。</summary>
        public DictionaryLocalizedTextSource AddLocale(string locale, IEnumerable<KeyValuePair<string, string>> entries)
        {
            if (string.IsNullOrEmpty(locale)) throw new ArgumentException("locale 不能为空。", nameof(locale));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var validatedEntries = new List<KeyValuePair<string, string>>();
            foreach (var kv in entries)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    throw new ArgumentException("文本 key 不能为空。", nameof(entries));
                if (kv.Value == null)
                    throw new ArgumentException($"文本 '{kv.Key}' 的值不能为 null。", nameof(entries));
                validatedEntries.Add(kv);
            }

            var dict = GetOrCreate(locale);
            bool changed = false;
            foreach (var kv in validatedEntries)
            {
                if (dict.TryGetValue(kv.Key, out var existing) && existing == kv.Value) continue;
                dict[kv.Key] = kv.Value;
                changed = true;
            }
            if (changed) _invalidated.OnNext(Unit.Default);
            return this;
        }

        public LocalizedTextLookupStatus Lookup(string locale, string key, out string text)
        {
            text = null;
            return _byLocale.TryGetValue(locale, out var dict) && dict.TryGetValue(key, out text)
                ? LocalizedTextLookupStatus.Found
                : LocalizedTextLookupStatus.Missing;
        }

        private Dictionary<string, string> GetOrCreate(string locale)
        {
            if (!_byLocale.TryGetValue(locale, out var dict))
                _byLocale[locale] = dict = new Dictionary<string, string>();
            return dict;
        }
    }
}
