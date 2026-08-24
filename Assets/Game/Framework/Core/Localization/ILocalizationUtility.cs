using Game.Framework.Utility;
using R3;

namespace Game.Framework.Localization
{
    /// <summary>
    /// 本地化服务。框架只管三件小事：「当前语言」全局状态、key → 当前语言文本的查询、
    /// 语言或文本源内容变化时让已显示 UI 跟着变（<see cref="TextRevision"/> 推送驱动重绑）。
    /// 文本数据来自 <see cref="ILocalizedTextSource"/> 接缝（业务包自己的配置表，或用内置字典源）。ADR-0024。
    /// </summary>
    /// <remarks>
    /// <b>注册：</b>已有 source 时用 <c>builder.RegisterOwned(new LocalizationUtility(source, initialLocale, fallbackLocale), typeof(ILocalizationUtility))</c>；
    /// source 需从容器解析其他服务时改用 <c>RegisterOwnedFactory</c>，不要用不接管生命周期的普通 Factory；
    /// 文本源经构造注入（同存储 provider 姿势）。<br/>
    /// <b>locale code 是开放字符串 + 业务常量</b>（"zh-CN" / "en"……与音频组、存储 key 同一「常量管理字符串契约」姿势）；
    /// 语言列表、<c>SystemLanguage</c> → code 映射、语言选择持久化（设置数据走 <c>IStorageUtility</c>，启动回灌）都归业务。<br/>
    /// <b>查询语义：</b>源暂不可查时返回 key 本身但不报“缺文案”，等源失效信号触发重取；只有源明确报告缺失时才走
    /// 当前 locale → fallbackLocale（构造可选）→ <b>返回 key 本身</b> + Editor/Dev 警告——
    /// 屏幕上直接显示裸 key 就是最好的缺失报告，不抛异常（文案缺失不炸游戏）也不给空串（静默丢文案最难发现）。<br/>
    /// <b>per-locale 资源</b>（图 / 音频按语言分包）不在本接口：用资源系统多 package / location 命名约定 +
    /// <c>Bag.Subscribe(Locale, ...)</c> 响应式组合。<br/>
    /// <b>线程：</b>主线程独占（框架统一契约）。
    /// </remarks>
    public interface ILocalizationUtility : IUtility
    {
        /// <summary>
        /// 当前语言（响应式）。字体链、按语言换图/音频等只关心 locale 的逻辑订阅它；文本 UI 应订
        /// <see cref="TextRevision"/>，否则延迟文本源就绪时不会重取。
        /// </summary>
        ReadOnlyReactiveProperty<string> Locale { get; }

        /// <summary>
        /// 文本查询结果的修订号：订阅即得当前值；语言切换或文本源发出 <see cref="ILocalizedTextSource.Invalidated"/>
        /// 时递增。数值本身没有业务含义，只用作“重新调用 <see cref="Get(string)"/>”的失效信号。
        /// </summary>
        ReadOnlyReactiveProperty<int> TextRevision { get; }

        /// <summary>
        /// 切换当前语言并推送 <see cref="Locale"/> 与 <see cref="TextRevision"/>。code 为空抛参数异常；
        /// 与当前相同 = no-op（两个信号都不推送）。
        /// </summary>
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
    /// 一次文本源查询的结果。把“现在还不能回答”和“已经确认缺失”分开，避免异步配置加载期产生假缺失警告。
    /// </summary>
    public enum LocalizedTextLookupStatus
    {
        /// <summary>当前快照暂不可查询（尚未加载或源失败）；返回 key 占位、不 fallback、不报缺失，等待源失效信号。</summary>
        Unavailable,

        /// <summary>源当前可查询，且已确认该 locale + key 不存在；LocalizationUtility 可继续 fallback / 缺失报告。</summary>
        Missing,

        /// <summary>命中有效文本；此时 out text 必须非 null。</summary>
        Found
    }

    /// <summary>
    /// 本地化文本源接缝：locale + key → 带可用性语义的结果，并在既有查询答案可能变化时发出失效信号。
    /// 业务典型实现是包自己的 Luban 配置表 Adapter；测试 / demo / 小游戏用内置
    /// <see cref="DictionaryLocalizedTextSource"/>。
    /// </summary>
    /// <remarks>
    /// <see cref="Invalidated"/> 的每次推送（包括实现可能在订阅时立即给出的当前快照）都表示调用方应重新查询；
    /// 它不等同于 locale 变化。实现不要抛异常，也不要用 <see cref="LocalizedTextLookupStatus.Missing"/> 表示加载中。
    /// Source 必须至少与消费它的 <see cref="LocalizationUtility"/> 同寿；Utility 只拥有订阅，不拥有 Source 本身。
    /// </remarks>
    public interface ILocalizedTextSource
    {
        /// <summary>查询答案可能变化时推送。永远不要返回 null；静态源可返回一个从不推送的 Observable。</summary>
        Observable<Unit> Invalidated { get; }

        /// <summary>查询指定语言文本。返回 <see cref="LocalizedTextLookupStatus.Found"/> 时 <paramref name="text"/> 必须非 null。</summary>
        LocalizedTextLookupStatus Lookup(string locale, string key, out string text);
    }
}
