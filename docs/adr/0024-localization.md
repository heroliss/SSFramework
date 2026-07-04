# ADR-0024：本地化 —— ILocalizationUtility：响应式 locale + key 查询 + 组合既有原语

**Status:** Accepted（2026-07-04）

## Context

roadmap 中期新模块第四项：本地化——需求普适（出海即刚需），roadmap 圈定的思路是「表驱动（吃现成配置表）+ 资源按 locale 分包（吃现成多 package），基本是组合既有原语」。与中期⑤字体策略（ADR-0025）一起设计：字体本身也按 locale 切换，切换信号由本模块提供。

盘点已有原语后，真正缺的核心很小：**「当前语言」这个全局状态 + key → 当前语言文本的查询 + 换语言时已显示 UI 跟着变**。其余都是组合：

- **文本数据**：业务的 Luban 配置表天然多列多语言（Excel 一行一 key、一列一语言），`Game.Framework.Config` 已管加载——框架不需要再发明表格式。
- **per-locale 资源**：YooAsset 多 package / location 命名约定已覆盖「图片音频按语言分包」。
- **响应式**：`RP<T>`（R3）+ 既有 `Bag.Subscribe` / `BindText` 就是「换语言 UI 自动刷新」的机制。
- **持久化**：语言选择存业务设置数据（`IStorageUtility` 整存整取，音量持久化同一先例）。

既有约束与先例：内核零第三方依赖（R3/UniTask 除外）；`Game.Framework` 不能引用 `Game.Framework.Config`（Config 是独立模块 asmdef，且表 schema 是业务定义的）——文本来源必须是接缝，不能是依赖。

## Decision

### 1. API 形态：小内核 + 单方法文本源接缝

```csharp
public interface ILocalizationUtility : IUtility
{
    ReadOnlyReactiveProperty<string> Locale { get; }   // 响应式当前语言；SetLocale 推送
    void SetLocale(string locale);
    string Get(string key);                            // 当前语言文本；缺 key 见 §2
    string Get(string key, params object[] args);      // string.Format 包装（UI 文案频率，装箱可接受）
}

public interface ILocalizedTextSource
{
    bool TryGet(string locale, string key, out string text);
}
```

- **文本源经构造注入**（`new LocalizationUtility(source, initialLocale, fallbackLocale?)`，存储的 provider 先例）：业务写 ~10 行 adapter 包自己的 Luban 表；框架内置 `DictionaryLocalizedTextSource`（字典源——测试 / demo / 小游戏直接用，也是第二实现，接缝不空转）。
- locale code 是**开放字符串 + 业务常量**（`"zh-CN"` / `"en"`……与音频组、存储 key 同一「常量管理字符串契约」姿势）；语言列表是业务常量数组，接口不提供枚举（源不一定能枚举，如懒加载分包文本）。
- 初始 locale 由业务传入（读存档或 `Application.systemLanguage` 映射后）；**持久化归业务**（设置数据走 `IStorageUtility`，启动回灌——音量先例）。`SystemLanguage` → 业务 code 的映射表也是业务的（框架不知道业务定义了哪些 code）。

### 2. 缺 key 语义：返回 key 本身 + Editor/Dev 警告（可见性优先）

- 查询失败链：当前 locale → `fallbackLocale`（构造可选，一级回退，如 zh-TW → zh-CN）→ **返回 key 本身** + Editor/Dev `LogWarning`。
- 不抛异常（文案缺失不该炸游戏，宽容对齐音频而非存储）也不返回空串（静默丢文案最难发现）——**屏幕上直接显示裸 key 就是最好的缺失报告**，QA 一眼看见。

### 3. 响应式换语言：Locale 是 RP，推送即一切

- `SetLocale` 推送 `Locale`，所有绑定自动重取文本——**不做「需重启生效」机制**（表驱动 + 响应式绑定下没有理由重启）。
- 绑定扩展只落 **UI.Toolkit**（有 `BindText` 先例；经 bag 新增的公开 `Context` 访问器解析 utility——与 `Bag.Load` 同心智）。UGUI 侧用 `Bag.Subscribe(loc.Locale, _ => tmp.text = loc.Get(key))` 一行组合——UGui asmdef 刻意不引 R3，不为一个便捷方法加依赖：
  ```csharp
  Bag.BindLocalizedText(label, "menu/start");                  // 换语言自动刷新
  Bag.BindLocalizedText(label, "lobby/welcome", playerName);   // 带静态格式化参数
  ```
- 动态参数（文案里嵌响应式数值）不做专门 API：业务 `Bag.Subscribe(model.Gold, g => label.text = loc.Get("shop/gold", g))` 一行组合；换语言 + 动态值双源要联动的，R3 `CombineLatest` 就是答案。

### 4. per-locale 资源：刻意零 API，文档化两个组合模式

- **按 locale 分包**：YooAsset collector 按语言建 package（`L10N_zh` / `L10N_en`），业务 `Bag.Load<T>(业务映射(locale), location)`——多 package 是现成能力。
- **location 后缀约定**：小体量项目 `$"{location}_{locale}"` 命名即可。
- 换语言时的图切换 = `Bag.Subscribe(loc.Locale, l => ...重新 Load...)` 响应式组合。命名/分包约定各项目不同，框架提供 helper 反而强加约定。

### 5. 刻意不做

- **复数 / 性别 / CLDR 规则**：ICU 级复杂度，绝大多数游戏文案用「{0} 个」直译绕开；真需要的项目接专门库，`Get` 的输出可以再包一层。
- **翻译工作流 / 导出导入工具**：Luban 的 Excel 本身就是翻译工作流（一列一语言发给翻译）。
- **场景静态文本自动收集**：本框架 UI 全代码驱动（窗口 = View 类），文本入口天然收敛在 `BindLocalizedText`，没有「散落场景里的 Text 组件」问题。
- **字体切换**：归 ADR-0025——字体模块订阅 `Locale` RP，本模块只出信号。

## Consequences

- 内核 `Core/Localization/` 纯 C#（依赖 R3 的 RP），字典源下测试全程无场景、batchmode 零风险。
- 业务接入路径：定义 locale 常量 → 写 Luban 表 adapter（或用字典源）→ `RegisterOwned` → UI 全用 `BindLocalizedText` → 设置页 `SetLocale` + 存档回灌。
- demo 章做中英双语活样板：文本源用 **Luban 表 adapter 实物**（`TbL10N` + `l10n.xlsx`，源吃配置服务所以走 `RegisterFactory` 解依赖顺序）+ 语言切换 / 绑定 / 格式化 / 缺 key（英文列留空走 fallback、表里没有走裸 key）+ **图片与音频 per-locale 实操**（`l10n-banner_<locale>` 子 Bag 重载、`l10n-voice_<locale>` 播放时取）。
- 风险：`params object[]` 每次 Get 分配——UI 文案频率无感；热路径（每帧刷的计分文本）业务本就该缓存格式串。
