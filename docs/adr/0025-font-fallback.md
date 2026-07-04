# ADR-0025：字体策略 —— 精简字集随包 + fallback 链 + OS 字体运行时兜底

**Status:** Proposed（2026-07-04，草案——与 ADR-0024 一起设计，待 0024 落地后实施细化）

## Context

CJK 全量字库体积大（单字体 15~30MB，多语言更甚），全量随包不现实；但砍了字库，用户名 / 聊天 / UGC 这类**不可预知文本**又会显示豆腐块。roadmap 中期⑤给定策略方向：精简常用字集随包 + TMP fallback 链兜生僻字 + 运行时 OS 系统字体作最后兜底。字体按 locale 切换，信号来自 ADR-0024 的 `ILocalizationUtility.Locale`。

技术面：UGUI 侧文本是 TMP（Unity 6 并入 `com.unity.ugui` 2.0，UI.UGui 代码生成已支持）；UI Toolkit 侧是 TextCore `FontAsset`（PanelSettings → PanelTextSettings）。两套 fallback 机制独立，字体模块要双后端各配一份（与 UI 框架双 backend 同姿势）。

## Decision（方向性，实施时细化）

### 1. 三层字体策略

| 层 | 内容 | 覆盖 |
|---|---|---|
| ① 随包主字体 | 精简常用字集烘焙的 static atlas（TMP_FontAsset / TextCore FontAsset） | 已知 UI 文案与配置表文本（99% 显示量） |
| ② fallback 字体 | per-locale 配置的补充字体资产（动态 atlas），可放 locale 分包按需下载 | 生僻字 / 特定语言扩展区 |
| ③ OS 字体兜底 | 运行时 `Font.CreateDynamicFontFromOSFont` → 动态 `TMP_FontAsset.CreateFontAsset`，挂 fallback 链尾 | 用户名 / 聊天等不可预知文本 |

层①②是资产，层③是运行时生成——三层都在 fallback 链上，文本渲染自动逐层找字形，业务代码零感知。

### 2. 载体：Mono 配置组件订阅 Locale

- `MonoLocaleFonts`（Inspector 配置：locale → 主字体 / fallback 列表 / OS 字体名候选，UGUI 与 Toolkit 两栏）——字体映射是资产引用，天然 Inspector 事；挂 Context 子节点。
- 订阅 `ILocalizationUtility.Locale`：切语言时更新 TMP 全局 fallback（`TMP_Settings.fallbackFontAssets`）与 PanelTextSettings fallback 列表；共享主字体（Latin / 数字 / 符号各语言通用）不换，换的是链上的语言层。
- OS 字体名候选按平台配列表（如 `"Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC"`），逐个 `CreateDynamicFontFromOSFont` 试到成功；全失败 = 只有①②（degrade，不炸）。

### 3. Editor 工具：常用字集生成

- 菜单「生成常用字集」：扫描配置表文本列 + 代码内字符串字面量 → 去重输出 charset 文件 → 喂 TMP Font Asset Creator 烘焙 static atlas（v1 手动烘焙，自动化烘焙观察需求再做）。

### 4. 刻意不做

- **全字库随包 / 每 locale 独立完整字体**：fallback 链的意义就是共享通用字形、语言层只补差集。
- **运行时字形卸载 / atlas 压缩调优**：动态 atlas 的内存策略交 TMP/TextCore 默认，量化出问题再调。
- **Web 字体 / 远程字体下发协议**：locale 分包已覆盖「字体资产按需下载」。

## Consequences

- 依赖 TMP / TextCore Editor+Runtime API，载体放独立模块（asmdef 待定：`Game.Framework.Font` 或并入 UI 模块），**不进零依赖内核**。
- batchmode 无字体渲染可言：测试只能覆盖「配置解析 / locale 切换驱动 / OS 字体名候选逻辑」，渲染效果靠 demo 人工验证。
- 实施排在 ADR-0024 落地之后（依赖 Locale 信号）；届时补充实测过的 API 细节再转 Accepted。
