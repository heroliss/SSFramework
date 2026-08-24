# Demo 章节编写规范

框架功能演示（`DemoModuleBase` 子类）的**章节模板与文案约束**。在本目录写 / 改演示模块时自动加载。
目标：每章点开前，光看左侧小节标题就知道「讲什么、讲到哪」；正文短、有节奏、抓得住重点，同时让读者理解适用边界而不是只会照抄。

成熟样板照着抄：`AudioDemoModule` / `StorageDemoModule` / `FlowDemoModule` / `LocalizationDemoModule`。

## 章节实例与生命周期

- `DemoModuleCatalog` 在根 Context 构建前只发现并构造一批章节 Adapter；同一实例依次执行 `InstallBindings → Initialize → Build / Teardown`，不要在 Shell 或章节里再次反射/new 目录实例。
- `InstallBindings` 只声明容器注册关系，不启动异步工作、不把临时服务藏进字段；Build 需要的运行时依赖从 Context 解析，保持所有权与 View 权限示范清晰。
- 每次 `Build` 与 `Teardown` 成对发生且可重复；切章、UIDocument 重建和销毁都由目录先取消 Host，再 Teardown。模块字段会跨重建保留，临时订阅/资源必须进 `Bag`。

## 章节骨架（每个 `Build()` 按此顺序）

1. **`定位：<一句话>`** —— 开篇小节。**标题本身**说清「是什么 + 关键边界」（不是裸 `演示`）。
   下面一条 `AddNote`（≤2 句）讲「解决什么问题」，行末挂一处框架接口的源码跳转。
2. **N 个功能点小节** —— 每个 `AddSectionTitle` **点名一个具体能力**（不是裸 `说明`）。小节内：
   - 能力点按钮通常保持“一个按钮 = 一个框架操作”，行末跳转指向 **demo 自身的调用点**（`CodeRef.Here(...)`）。端到端工作流章可组合前文已讲过的原语，但标题/文案必须明确它是编排流程，并链接到组合入口。
   - 同步按钮用 `AddActionRow`；异步按钮必须用 `AddAsyncActionRow(async ct => ...)`，默认把 `ct` 透传到底层异步 API。Host 会在执行期禁用按钮防重入、统一记录漏接异常，并在切章 / UI 重建时取消；禁止 `async void`，也不要用同步按钮丢弃 `UniTask` / `Task`（Host 有编译期护栏，门禁还会检查 `.Forget()` / `UniTaskVoid` 包装）。
   - **取消语义按 API 契约判断，不机械透传**：若接口明确规定“调用者取消只离开等待，已提交的物理操作继续”，且多个业务操作靠同一闸门避免与它重叠，则应在提交前 `ct.ThrowIfCancellationRequested()`，提交后用 owner / utility 生命周期令牌等待物理终态，随后再检查 `ct`、不向旧 UI 发布结果。此例外必须在调用点注释为什么不能直接传章节 `ct`。
   - 多个按钮共享同一子 Bag、文件、下载器或其它可变资源时，单按钮防连点不够：用模块实例字段 `DemoOperationGate` 取得租约并覆盖整个异步收尾期；不要用 Build 局部 gate，也不要用迟到 `finally` 可误释放新流程的裸 `bool`。
   - 概念 / 架构章不为凑“可运行”硬塞无意义按钮；改用可点击源码链、对比表和明确的验证路径，让读者仍能核对结论。
   - 一两句 `AddNote` 讲语义 / 边界；「为什么这样、什么坑」下沉到 `AddSubNote`（缩进暗色）或 `AddTip`。
3. **`刻意不做` / `小结`** —— 收尾（酌情）。用 `AddConcept` 列「不做什么 + 为什么」，或 `AddTip` 给一句速记 + 指路 guide/ADR。

首次出现的概念（如 RP / ReadOnlyReactiveProperty）用 `AddConcept` 块单独讲，但排到**功能点之后**，别挡在最小闭环前面。

## 教学四问（渐进披露）

每章整体应回答四个问题，不要求机械地一问一节，也不要全塞进开场：

1. **它解决什么问题，位于哪一层？** 开场先给读者一张局部地图。
2. **最小行为怎么跑起来？** 先让读者操作、看到反馈，再拆解代码接缝。
3. **为什么这样设计，代价是什么？** 至少指出一个关键取舍或限制，避免把约定讲成绝对真理。
4. **什么时候用 / 不用，下一步去哪？** 给出判断标准、最佳实践或相邻章节，不让读者只会复制当前例子。

Demo 是“学习着做”的教程，设计解释只保留理解当前操作所必需的部分；完整 API 契约放 `docs/framework-guide.md`，长期设计决策与替代方案放 `docs/adr/`。这样一处更新不会迫使三份材料重复维护。

## 文案硬约束

- **一条 `AddNote` ≤ 2 句、只讲一个意思**。长了就拆：主干留 `AddNote`，细节 / 坑挪 `AddSubNote` 或 `AddTip`。
- **可枚举的内容用结构化件**，别用逗号长句堆叠：并列职责 / 释义 → `AddConcept`（术语 + 短句）；有序步骤 → `AddStep`（编号徽标）；多项横向对比 → `AddTable`。
- **富文本只认三种标记**（`DemoRichText`）：`` `code` ``（API/类型/路径）、`「术语」`（专名/章节名）、`**强调**`（字重+提亮）。
  ⚠ **禁止 `<b>`/`<i>`/`<color>` 等 HTML**——note/step 文案里的 HTML 标签会被 noparse 当字面量、在界面显示成乱码（`<b>` 只在 `///` XML doc 里合法）。
- **顶部 `Summary` ≤ 160 字且 ≤ 2 句**：说「是什么 + 关键边界」，别把功能点全塞进去（功能点是左侧小节的活）。长度与句数由 `DemoModuleCatalog` 发现目录时 fail-fast 校验。
- **目录元数据是运行期契约**：`Id` 用唯一 kebab-case；`Title` 唯一且非空；`Category` 只能是「入门 / 核心 / 能力 / 进阶 / 规划中」；同 Category 的 `Order` 不得撞号。`DemoModuleCatalog` 会在根 Context 构建期一次列出全部问题。
- 小节间可用 `// ── 小节名 ──` 注释分隔，长模块推荐。

## 源码跳转取向（`CodeRef`）

- **概念 / 原理章**（Overview、DOTS、YooAsset、热更）：可跳框架接缝 / 底层——它们本就为「看设计」。
- **能力章**（音频 / 存储 / 网络 / UI…）：`查看源码` 优先指向 **demo 自身**的调用 / 定义；框架接口至多在「定位 / 注册」处留**一处**「这是完整契约」。
- 同文件用 `CodeRef.Here("锚点")`（路径编译期自动注入、不失效）；跨文件用 `new CodeRef("Assets/...", "锚点")`。
- **锚点取一段够独特的真实代码片段**（如 `struct EarnGoldCommand`、`audio.PlayMusic(_musicA)`），别取太泛的词（`RegisterOwned` 会撞注释）。
- 改完跑菜单 **`SSFramework/诊断/校验 Demo 源码跳转锚点`**——落偏的锚点会 LogError（防腐守护，见 `Core/DemoCodeRefValidator.cs`）。
