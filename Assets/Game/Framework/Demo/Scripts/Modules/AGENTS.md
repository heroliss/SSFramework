# Demo 章节编写规范

框架功能演示（`DemoModuleBase` 子类）的**章节模板与文案约束**。在本目录写 / 改演示模块时自动加载。
目标：每章点开前，光看左侧小节标题就知道「讲什么、讲到哪」；正文短、有节奏、抓得住重点。

成熟样板照着抄：`AudioDemoModule` / `StorageDemoModule` / `FlowDemoModule` / `LocalizationDemoModule`。

## 章节骨架（每个 `Build()` 按此顺序）

1. **`定位：<一句话>`** —— 开篇小节。**标题本身**说清「是什么 + 关键边界」（不是裸 `演示`）。
   下面一条 `AddNote`（≤2 句）讲「解决什么问题」，行末挂一处框架接口的源码跳转。
2. **N 个功能点小节** —— 每个 `AddSectionTitle` **点名一个具体能力**（不是裸 `说明`）。小节内：
   - 原子按钮：一个按钮 = 一个框架操作，行末跳转指向 **demo 自身的调用点**（`CodeRef.Here(...)`）。
   - 一两句 `AddNote` 讲语义 / 边界；「为什么这样、什么坑」下沉到 `AddSubNote`（缩进暗色）或 `AddTip`。
3. **`刻意不做` / `小结`** —— 收尾（酌情）。用 `AddConcept` 列「不做什么 + 为什么」，或 `AddTip` 给一句速记 + 指路 guide/ADR。

首次出现的概念（如 RP / ReadOnlyReactiveProperty）用 `AddConcept` 块单独讲，但排到**功能点之后**，别挡在最小闭环前面。

## 文案硬约束

- **一条 `AddNote` ≤ 2 句、只讲一个意思**。长了就拆：主干留 `AddNote`，细节 / 坑挪 `AddSubNote` 或 `AddTip`。
- **可枚举的内容用结构化件**，别用逗号长句堆叠：并列职责 / 释义 → `AddConcept`（术语 + 短句）；有序步骤 → `AddStep`（编号徽标）；多项横向对比 → `AddTable`。
- **富文本只认三种标记**（`DemoRichText`）：`` `code` ``（API/类型/路径）、`「术语」`（专名/章节名）、`**强调**`（字重+提亮）。
  ⚠ **禁止 `<b>`/`<i>`/`<color>` 等 HTML**——note/step 文案里的 HTML 标签会被 noparse 当字面量、在界面显示成乱码（`<b>` 只在 `///` XML doc 里合法）。
- **顶部 `Summary` ≤ 2 句**：说「是什么 + 关键边界」，别把功能点全塞进去（功能点是左侧小节的活）。
- 小节间可用 `// ── 小节名 ──` 注释分隔，长模块推荐。

## 源码跳转取向（`CodeRef`）

- **概念 / 原理章**（Overview、DOTS、YooAsset、热更）：可跳框架接缝 / 底层——它们本就为「看设计」。
- **能力章**（音频 / 存储 / 网络 / UI…）：`查看源码` 优先指向 **demo 自身**的调用 / 定义；框架接口至多在「定位 / 注册」处留**一处**「这是完整契约」。
- 同文件用 `CodeRef.Here("锚点")`（路径编译期自动注入、不失效）；跨文件用 `new CodeRef("Assets/...", "锚点")`。
- **锚点取一段够独特的真实代码片段**（如 `struct EarnGoldCommand`、`audio.PlayMusic(_musicA)`），别取太泛的词（`RegisterOwned` 会撞注释）。
- 改完跑菜单 **`SSFramework/诊断/校验 Demo 源码跳转锚点`**——落偏的锚点会 LogError（防腐守护，见 `Core/DemoCodeRefValidator.cs`）。
