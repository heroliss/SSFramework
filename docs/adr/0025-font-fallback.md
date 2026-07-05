# ADR-0025：字体策略 —— 精简字集随包 + 主字体 fallback 链 + OS 字体运行时兜底

**Status:** Accepted（2026-07-05；2026-07-04 草案随 ADR-0024 一起设计，落地时按 Unity 6000.3 实测 API 细化）

## Context

CJK 全量字库体积大（单字体 15~30MB，多语言更甚），全量随包不现实；但砍了字库，用户名 / 聊天 / UGC 这类**不可预知文本**又会显示豆腐块。roadmap 中期⑤给定策略方向：精简常用字集随包 + fallback 链兜生僻字 + 运行时 OS 系统字体作最后兜底。字体按 locale 切换，信号来自 ADR-0024 的 `ILocalizationUtility.Locale`。

技术面（Unity 6000.3 实测）：UGUI 侧文本是 TMP（并入 `com.unity.ugui` 2.0，程序集 `Unity.TextMeshPro`）；UI Toolkit 侧是 TextCore `FontAsset`。两套 fallback 机制独立，但**双后端都有 public 可写的 per-font fallback 表**（`TMP_FontAsset.fallbackFontAssetTable` / `FontAsset.fallbackFontAssetTable`）和**按 OS 字体族名直接建动态字体资产**的对称 API（`CreateFontAsset(familyName, styleName, pointSize)`，找不到返回 null、只打 info 日志）。

⚠ 草案设想的「TMP 全局 fallback + PanelTextSettings fallback」路线实测**不可行了一半**：6000.3 里 `PanelSettings.textSettings` 已被移除、`PanelTextSettings.defaultPanelTextSettings` 是 internal-only——Toolkit 侧的全局路径只剩反射（脆弱、随版本漂移）。

⚠ 实测另一关键差异：**Toolkit 文本引擎在 TextCore `TextSettings` 层内建 OS 字形兜底**（internal `fallbackOSFontAssets`，缺字自动查系统字体）——Toolkit 侧缺字**不豆腐，但字形随平台走**（Windows 雅黑 / macOS 苹方）；**TMP 无此机制，缺字即豆腐块**。因此 ②③ 在 TMP 侧是刚需；在 Toolkit 侧 ② 的价值是「字形归属可控」（链上品牌字体优先于引擎 OS 兜底，各平台排版一致），③ 提供的是候选次序可控（引擎兜底选谁由 OS 决定）。

## Decision

### 1. 三层字体策略（不变）

| 层 | 内容 | 覆盖 |
|---|---|---|
| ① 随包主字体 | 精简常用字集烘焙的 static atlas（`TMP_FontAsset` / TextCore `FontAsset`） | 已知 UI 文案与配置表文本（99% 显示量） |
| ② locale 补充字体 | per-locale 配置的补充字体资产（动态 atlas，如 NotoSansSC），链上只补当前语言的差集 | 生僻字 / 特定语言扩展区 |
| ③ OS 字体兜底 | 运行时按族名候选创建动态字体资产，挂链尾 | 用户名 / 聊天等不可预知文本 |

层①②是资产，层③是运行时生成——三层都在 fallback 链上，文本渲染自动逐层找字形，业务代码零感知。

### 2. 机制：写主字体的 fallback 表（不碰全局 settings）

**链条应用点 = 每个「主字体资产」的 `fallbackFontAssetTable`**：`表 = 原始表 + ②当前 locale 补充 + ③OS 兜底`。不用 TMP 全局 `TMP_Settings.fallbackFontAssets`、不用 Toolkit 的 PanelTextSettings：

- **双后端对称**：per-font 表在两侧都是 public 可写；全局路径 Toolkit 侧 6000.3 已收进 internal。
- **不碰共享资产**：`TMP_Settings` 是全工程共享的 settings 资产，Editor Play 期间改它会留残留；主字体表我们**快照原始值、OnDestroy 还原**，Play 会话不污染资产。
- **语义贴合设计**：共享主字体（Latin / 数字 / 符号各语言通用）不换，换的只是它链上的语言层——正是 per-font 表的形状。
- 代价：主字体要**显式列出**（一个项目通常 1~3 个：正文 / 标题各一）。用了没列出的字体的文本不受链条保护——配置显式化可接受。

### 3. 载体：`MonoLocaleFonts`（Mono 配置组件，订阅 Locale）

- 挂根 Context 子节点（`MonoUtilityBase`，注册具体类型防同 Context 重复挂），Inspector 配置：
  - **主字体列表**（TMP / Toolkit 两栏）——链条写到这些资产上；
  - **per-locale 档案**：locale → ② 补充字体（TMP / Toolkit 两栏）+ ③ OS 字体族名候选（如 `"Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC"`）。
- `Start` 快照各主字体原始表 → `Bag.Subscribe(loc.Locale, Apply)`（订阅即得当前值，随 Bag 退订）；`OnDestroy` 还原原始表 + 销毁运行时创建的 OS 字体资产（atlas 纹理 / 材质一并销毁，子 Context 反复建销不泄漏）。
- 未配置当前 locale 的档案 → 只保留主字体原始表（degrade，不炸）+ 一次性警告；locale 没配 ②/③ 某一项 → 该层跳过。
- ③ 创建姿势：按序 `CreateFontAsset(族名, null, 90)`（null 样式 = 默认 face）试到第一个成功，按族名缓存——**失败也缓存**（每次失败引擎都打日志，缓存避免每次切语言重试刷屏）；全失败 = 只有①②（警告一次，不炸）。族名要用**英文名**（「微软雅黑」这类本地化名查不到）；刻意不用 `GetOSInstalledFontNames()` 预过滤——它返回的显示名与引擎查找用的族名可能不一致，误滤可用候选。
- 应用 / 还原后对使用主字体的存活 TMP 文本 `ForceMeshUpdate(true, true)` 强刷（TMP 有字形解析缓存）；Toolkit 文本随 locale 切换本就重设文本（`BindLocalizedText`），固定文本 + 链条变化的罕见场景由业务重设 text 触发重排（demo 有样板）。

### 4. asmdef：`Game.Framework.Fonts`（独立模块，不进内核）

- `Framework/Fonts/`，引用 `Game.Framework` + `R3` + `Unity.TextMeshPro`（TextCore / UIElements 是引擎模块自动可用）；`autoReferenced:false`，进热更列表。命名空间取复数 `Fonts`——单数 `Font` 段会就近劫持 `UnityEngine.Font` 类型引用（同 `Systems` 先例，AGENTS #6）。
- 内核不需要接口（ports & adapters 服务于「内核调模块」，本模块是纯接线组件、没人调它）——先例 `MonoUIBackKeyDriver`。顺带移除内核 asmdef 里无使用者的 `Unity.TextMeshPro` 引用（TMP 依赖归本模块）。

### 5. Editor 工具：常用字集生成（`Framework/Fonts/Editor/`）

- `FontCharsetProfile`（单例 profile，首次使用自动创建；按「配置 Profile 约定」进菜单 + 配置总览 hub）：扫描目录 + 文件通配 + 额外字符 + 输出路径。
- 菜单 `SSFramework/字体/生成常用字集`：扫 `.json` / `.txt` / `.cs`（只取字符串字面量）/ `.xlsx`（读 sharedStrings.xml，Luban 源表直配）→ 去重出码点（正确处理代理对）→ 排序输出 charset 文件 → 喂 TMP Font Asset Creator 烘焙 static atlas（v1 手动烘焙，自动化烘焙观察需求再做）。

### 6. 刻意不做

- **全字库随包 / 每 locale 独立完整字体**：fallback 链的意义就是共享通用字形、语言层只补差集。
- **运行时字形卸载 / atlas 压缩调优**：动态 atlas 的内存策略交 TMP/TextCore 默认，量化出问题再调。
- **Web 字体 / 远程字体下发协议**：字体资产就是普通资源，locale 分包（ADR-0024 §4 的多 package 组合）已覆盖「按需下载」。
- **UGUI 旧版 `Text`（非 TMP）**：旧版动态 Font 引擎自带 OS 回退，无需框架介入；新文本一律 TMP。
- **每文本粒度换字体**：链条挂在主字体上全局生效；个别文本要专属字体直接在 UI 上指定（那本来就不是「兜底」问题）。

## Consequences

- 业务接入路径：烘焙①主字体（常用字集菜单 → TMP Font Asset Creator）→ 场景挂 `MonoLocaleFonts` 配主字体 + 各 locale 档案 → 换语言由 ADR-0024 的 `SetLocale` 一并驱动，字体零调用。
- batchmode 无字体渲染可言：测试覆盖「链条写入 / locale 切换驱动 / 未配置降级 / OnDestroy 还原 / OS 候选择取」（运行时创建字体资产在 PlayMode 测试可行，Windows 机器用 Arial 做候选）；渲染效果靠 demo 人工验证。
- 风险：`fallbackFontAssetTable` 是 Unity 未承诺稳定的运行时可写口（6000.3 实测 OK）；若未来版本收紧，机制退路是「主字体资产预烘焙时就配好 per-locale 链、切 locale 换主字体引用」——配置组件形状不变，业务无感。
