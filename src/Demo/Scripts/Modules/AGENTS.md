# Demo 章节编写规范

本文件约束 `DemoModuleBase` 教学章节；完整 API 放 `docs/framework-guide.md`，长期设计放 `docs/adr/`。

## 实例与生命周期

- `DemoModuleCatalog` 在根 Context 构建前只发现/构造一批 Adapter；同一实例执行 `InstallBindings → Initialize → Build / Teardown`，Shell 和章节不得再次反射/new 目录实例。
- `InstallBindings` 只声明注册，不启动异步工作或把临时服务存进字段；Build 依赖从 Context 解析。
- 每次 Build/Teardown 成对且可重复。目录切章/重建/销毁时先取消 Host 再 Teardown；模块字段跨重建保留，临时订阅与资源必须进 Bag。

## 教学形态与机器契约

- 默认 `Capability`（至少一个可操作入口）；纯心智模型用 `Concept`（至少两处步骤/概念/表格 + 真实源码引用）；接入/运营链路用 `Workflow`（至少两步 + 一个实际入口）。
- 正常章先且只调用一次 `AddPositioning`，真实 Build 至少三个小节、两处解释。Catalog 校验 Host 的实际语义，不扫描源码 token。
- 缺场景资产、Adapter 或 Utility 而提前结束时，第一个教学调用必须是 `AddUnavailable(reason, recovery, continuation, setupCode)`：说清原因、可执行恢复、仍可学习的下一站与接线源码，禁止一条 Note 后静默 return。

## `Build()` 结构

- 开头用一次 `AddPositioning` 交代“是什么 + 关键边界”，随后先给最小可操作闭环，再解释 Implementation/Seam、代价、适用范围与刻意不做；深入内容指向 guide/ADR。
- 全局阅读路线只在「框架总览」说明一次，不在 Shell 或每章重复注入导览/图例。顺读主线的相邻章节应在正文中自然承接：先唤起已学内容，再引出新问题，并就地说明联系、易混点、取舍、边界与下一站；“新手友好”来自知识组织，不来自重复外壳。
- 通常“一按钮 = 一框架操作”，源码链接指向 demo 调用点；端到端 Workflow 可组合已学原语，但须明确是编排入口。
- 同步用 `AddActionRow`；异步用 `AddAsyncActionRow(async ct => ...)` 并透传 token。禁止 `async void`、丢弃 UniTask/Task、`.Forget()` 或 `UniTaskVoid` 包装。
- “取消只离开等待”的物理操作：提交前检查章节 token，提交后由 owner token 等终态，再检查章节 token 且不发布旧 UI；调用点解释原因。
- 多按钮共享可变资源时，用模块字段 `DemoOperationGate` 覆盖整个收尾期；不用 Build 局部 gate 或迟到 finally 会误释放的裸 bool。
- Concept 不为凑操作硬塞按钮；主干用短 `AddNote`，次级原因放 `AddSubNote`，心智模型 / 口诀 / 延伸阅读放 `AddTip`，忽略后会导致错误、泄漏或误判的非实验边界放 `AddCaution`。

### 故意失败与副作用实验

- Warning/Error/Exception、数据破坏或共享副作用实验，先 `AddExperimentNotice(impact, expectedEvidence, recovery)`，再用 Experiment Action；三项分别写清作用域/持久性、UI + Console 证据、幂等恢复。普通成功路径不伪装成负向实验。
- 预期异常由章节本地捕获并显示；Host 的 `DemoAction failed` 只表示真实缺陷。不要引入自动吞异常的通用执行器，各 Module 保留真实控制流。

每章整体回答：解决什么且位于哪层；最小行为如何产生可见结果；为什么这样设计及代价；何时用/不用、下一步去哪。不要求机械地一问一节。

## 文案与结构

- 每条 `AddNote` 不超过两句且只讲一个意思；并列释义用 `AddConcept`，有序步骤用 `AddStep`，横向比较用 `AddTable`。
- 专业词首次出现优先写“中文白话（English / API 原名）”；关键定义必须直接可见，tooltip 只做鼠标用户的补充，不能成为唯一解释。
- 富文本只用 `` `code` ``、`「术语」`、`**强调**`；禁止 `<b>/<i>/<color>` 等 HTML（会作为字面量显示）。
- `Summary` 不超过 160 字、两句，只写“是什么 + 关键边界”。
- 目录元数据是运行时契约：`Id` 唯一 kebab-case，`Title` 唯一非空，`Category` 仅「入门/核心/能力/进阶/规划中」，同分类 `Order` 不重复。
- 长模块可用 `// ── 小节名 ──` 分隔。

## 源码跳转（`CodeRef`）

- Concept/原理章可跳框架 Seam；Capability 优先跳 demo 自身调用/定义，框架 Interface 至多在定位/注册处留一处完整契约。
- 同文件用 `CodeRef.Here("锚点")`，跨文件用 `new CodeRef("Assets/...", "锚点")`；锚点选唯一的真实代码片段，不用 `RegisterOwned` 这类泛词。
- 改完打开 `SSFramework/Demo 教学/维护与校验`，运行“校验全部 Demo CodeRef”；偏移或失效会 LogError。
