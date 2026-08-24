using Game.Framework.Utility;
using R3;

namespace Game.Framework.Localization
{
    /// <summary>
    /// 本地化服务。框架只管三件小事：「当前语言」全局状态（响应式）、key → 当前语言文本的查询、
    /// 换语言时让已显示 UI 跟着变（<see cref="Locale"/> 推送驱动重绑）。
    /// 文本数据来自 <see cref="ILocalizedTextSource"/> 接缝（业务包自己的配置表，或用内置字典源）。ADR-0024。
    /// </summary>
    /// <remarks>
    /// <b>注册：</b>已有 source 时用 <c>builder.RegisterOwned(new LocalizationUtility(source, initialLocale, fallbackLocale), typeof(ILocalizationUtility))</c>；
    /// source 需从容器解析其他服务时改用 <c>RegisterOwnedFactory</c>，不要用不接管生命周期的普通 Factory；
    /// 文本源经构造注入（同存储 provider 姿势）。<br/>
    /// <b>locale code 是开放字符串 + 业务常量</b>（"zh-CN" / "en"……与音频组、存储 key 同一「常量管理字符串契约」姿势）；
    /// 语言列表、<c>SystemLanguage</c> → code 映射、语言选择持久化（设置数据走 <c>IStorageUtility</c>，启动回灌）都归业务。<br/>
    /// <b>缺 key 语义：</b>当前 locale → fallbackLocale（构造可选）→ <b>返回 key 本身</b> + Editor/Dev 警告——
    /// 屏幕上直接显示裸 key 就是最好的缺失报告，不抛异常（文案缺失不炸游戏）也不给空串（静默丢文案最难发现）。<br/>
    /// <b>per-locale 资源</b>（图 / 音频按语言分包）不在本接口：用资源系统多 package / location 命名约定 +
    /// <c>Bag.Subscribe(Locale, ...)</c> 响应式组合。<br/>
    /// <b>线程：</b>主线程独占（框架统一契约）。
    /// </remarks>
    public interface ILocalizationUtility : IUtility
    {
        /// <summary>当前语言（响应式）。UI 绑定订阅它，<see cref="SetLocale"/> 推送即全量刷新；同值设置不推送。</summary>
        ReadOnlyReactiveProperty<string> Locale { get; }

        /// <summary>切换当前语言并推送 <see cref="Locale"/>。code 为空抛参数异常；与当前相同 = no-op（不推送）。</summary>
        void SetLocale(string locale);

        /// <summary>
        /// 取 key 在当前语言下的文本。缺失时依次回退 fallbackLocale → 返回 key 本身（Editor/Dev 警告，同一缺失只警告一次）。
        /// </summary>
        string Get(string key);

        /// <summary>
        /// 取文本并 <c>string.Format</c> 格式化。模板格式非法时 Editor/Dev 警告并返回未格式化模板（文案错不炸游戏）。
        /// 动态参数要跟着变的场景不用本重载——业务 <c>Bag.Subscribe</c> / R3 <c>CombineLatest</c> 组合。
        /// </summary>
        string Get(string key, params object[] args);
    }

    /// <summary>
    /// 本地化文本源接缝：locale + key → 文本。业务典型实现是 ~10 行包自己的 Luban 配置表；
    /// 测试 / demo / 小游戏用内置 <see cref="DictionaryLocalizedTextSource"/>。
    /// 实现约定：查不到返回 false 即可（回退与警告由 <see cref="LocalizationUtility"/> 统一处理），不要抛异常。
    /// </summary>
    public interface ILocalizedTextSource
    {
        bool TryGet(string locale, string key, out string text);
    }
}
