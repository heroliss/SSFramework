# ADR-0024：本地化 —— 语言身份、文本失效与三态 Source 接缝

**Status:** Accepted（2026-07-04；v2：2026-08-24；2026-08-31 补强终态与空白字符串边界）

## Context

roadmap 中期新模块第四项：本地化——需求普适（出海即刚需），roadmap 圈定的思路是「表驱动（吃现成配置表）+ 资源按 locale 分包（吃现成多 package），基本是组合既有原语」。与中期⑤字体策略（ADR-0025）一起设计：字体本身也按 locale 切换，切换信号由本模块提供。

盘点已有原语后，真正缺的核心很小：**当前语言身份 + key → 当前语言文本的查询 + 查询答案变化时让已显示 UI 重取**。其余都是组合：

- **文本数据**：业务的 Luban 配置表天然多列多语言（Excel 一行一 key、一列一语言），`Game.Framework.Config` 已管加载——框架不需要再发明表格式。
- **per-locale 资源**：YooAsset 多 package / location 命名约定已覆盖「图片音频按语言分包」。
- **响应式**：`RP<T>`（R3）+ 既有 `Bag.Subscribe` / `BindText` 就是刷新 UI 的机制。
- **持久化**：语言选择存业务设置数据（`IStorageUtility` 整存整取，音量持久化同一先例）。

既有约束与先例：内核零第三方依赖（R3/UniTask 除外）；`Game.Framework` 不能引用 `Game.Framework.Config`（Config 是独立 Module，且表 schema 是业务定义的）——文本来源必须是 Seam，不能是依赖。

v1 只有 `TryGet(...): bool`，并把 `Locale` 当作文本绑定的唯一刷新信号。Outpost 与 Demo 的真实异步配置接入暴露了两个被混在一起的状态：配置 Loading 时“现在不能回答”，表 Ready 后“已经确认缺 key”。二者都返回 `false` 会制造假 missing / fallback；同时配置从 Loading → Ready 而语言不变时，既有绑定不会重取。业务只好让 `BootState` 硬等配置，泄漏了本应由 Localization Module 收口的加载时序。

## Decision

### 1. API：小内核 + 三态文本源 + 独立失效信号

```csharp
public interface ILocalizationUtility : IUtility
{
    ReadOnlyReactiveProperty<string> Locale { get; }
    ReadOnlyReactiveProperty<int> TextRevision { get; }
    void SetLocale(string locale);
    string Get(string key);
    string Get(string key, params object[] args);
}

public enum LocalizedTextLookupStatus
{
    Unavailable, // 当前还不能回答；等待 Invalidated
    Missing,     // 当前快照已确认缺失；允许 fallback / 警告
    Found        // 命中；out text 必须非 null
}

public interface ILocalizedTextSource
{
    Observable<Unit> Invalidated { get; }
    LocalizedTextLookupStatus Lookup(string locale, string key, out string text);
}
```

- **文本源经构造注入**（`new LocalizationUtility(source, initialLocale, fallbackLocale?)`，存储的 Provider 先例）：业务写一个 Adapter 包自己的 Luban 表；框架内置 `DictionaryLocalizedTextSource`（测试 / Demo / 小游戏直接用，也是第二实现，Seam 不空转）。
- Source 的查询答案可能变化时发 `Invalidated`；静态源可返回永不推送的 Observable。Source 必须至少与 Utility 同寿，Utility 只拥有订阅、不拥有 Source 本身。
- `TextRevision` 是不透明的重取信号：语言变化或 Source 失效时递增，数值没有业务含义。选择 revision 而不是暴露 Source Observable，避免 UI 依赖 Adapter，也让初始订阅立即获得当前快照。
- locale code 是**开放字符串 + 业务常量**（`"zh-CN"` / `"en"`……）；语言列表、`SystemLanguage` → code 映射和持久化都归业务。

### 2. 查询语义：不可用与缺失分流

- `Unavailable`：当前 Source 尚未加载、失败或暂不能回答。`Get` 返回 key 作为临时占位，但**不查 fallback、不报缺失**；等待 Source 后续 `Invalidated` 触发重取。
- `Missing`：Source 当前可查询，且确认 locale + key 不存在。查询链继续走当前 locale → `fallbackLocale`（构造可选）→ 返回 key 本身 + Editor/Dev 一次性警告。
- `Found`：直接返回文本；若 Adapter 违反契约返回 `null`，Utility fail-fast 抛出 `InvalidOperationException`，避免无声污染 UI。

不因真缺文案抛异常（宽容对齐音频而非存储），也不返回空串（静默丢文案最难发现）——屏幕上的裸 key 是可见的缺失报告。加载期也可以显示裸 key，但它只是占位，不是缺失结论。

### 3. `Locale` 与 `TextRevision` 各表达一个变化轴

- `SetLocale` 同值 no-op；实际变化时先更新 `Locale`，再推进 `TextRevision`。
- **文本 UI** 订 `TextRevision`：UI Toolkit 用 `Bag.BindLocalizedText`；UGUI / TMP 用 `Bag.Subscribe(loc.TextRevision, _ => tmp.text = loc.Get(key))`；动态参数把业务值与 `TextRevision` `CombineLatest`。
- **只关心语言身份的消费方**订 `Locale`：字体链、按语言图片/音频重载、设置页的选中态与持久化。Source Ready 不应伪装成语言变化并连带重载这些资源。
- 不提供 `Refresh()`，也不允许 `SetLocale(CurrentValue)` 充当刷新；Source 的 `Invalidated` 才是内容变化的正确 Seam。

### 4. per-locale 资源：刻意零 API，文档化两个组合模式

- **按 locale 分包**：YooAsset collector 按语言建 package（`L10N_zh` / `L10N_en`），业务 `Bag.Load<T>(业务映射(locale), location)`——多 package 是现成能力。
- **location 后缀约定**：小体量项目 `$"{location}_{locale}"` 命名即可。
- 换语言时的图切换 = `Bag.Subscribe(loc.Locale, l => ...重新 Load...)`。命名/分包约定各项目不同，框架提供 helper 反而强加约定。

### 5. 刻意不做

- **复数 / 性别 / CLDR 规则**：ICU 级复杂度；真需要的项目接专门库，`Get` 输出可以再包一层。
- **翻译工作流 / 导出导入工具**：Luban 的 Excel 本身就是翻译工作流（一列一语言发给翻译）。
- **场景静态文本自动收集**：本框架 UI 全代码驱动，文本入口天然收敛在绑定处。
- **字体切换**：归 ADR-0025——字体模块订 `Locale`，本模块只提供语言身份信号。

## Consequences

- Localization Module 现在拥有更深的异步 Source Seam：业务 Adapter 负责把自己的状态映射为 `Unavailable / Missing / Found + Invalidated`，UI 与 Flow 不再知道配置加载时序。
- Outpost 删除 `BootState` 对本地化配置 Ready 的硬等待；标题可先建立绑定，配置后到会在同一语言下原地重取。真正依赖战斗配置的系统仍在自己的初始化入口等待。
- 所有文本消费方必须从 `Locale` 迁到 `TextRevision`；字体和 per-locale 资源继续只订 `Locale`。这是一次有意的公共接口升级，不保留旧 `TryGet`，让自定义 Adapter 在编译期暴露并迁移。
- `LocalizationUtility` 随 Context 释放时退订 Source；Source 生命周期仍由其所属 Module / Container 管理。字典源每次实际内容变化都会发失效信号。
- Demo 增加可操作的 Unavailable → Found 实验，证明不切语言也会刷新、且不产生假 missing；契约测试覆盖延迟源、Toolkit 实际标签、信号隔离、fallback 和释放退订。
- `params object[]` 每次 `Get` 仍会分配——UI 文案频率无感；每帧热路径应缓存格式串或降低更新频率。

## 2026-08-31 修订（借用终态与字符串契约）

- `LocalizationUtility` 不再声称 Dispose 后仍可查询。`Get` 虽不读取响应容器，却仍会调用只保证“至少与 Utility 同寿”的外部 Source；Context 结束后继续调用它没有安全依据。现在释放会退订 Source、完结已经交付的两个响应流，此后重新访问响应属性、查询或切换语言统一抛 `ObjectDisposedException`。
- 该选择把“缺文案”的宽容策略和“使用过期服务”的生命周期错误分开：有效生命周期内 Missing 仍回退裸 key，不炸游戏；生命周期已经结束则 fail-fast，不用一份可能也已释放的 Source 伪造陈旧文本。
- locale code、fallback code、文本 key 与内置字典源的写入 key 统一拒绝纯空白。它们仍是开放字符串，不引入注册表或枚举；这里只阻止肉眼不可辨识、无法可靠诊断的无效契约。
