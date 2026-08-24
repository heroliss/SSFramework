# Framework Module 地图

本文面向框架维护者，记录 `Assets/Game/Framework/` 的程序集边界、依赖方向与删除测试。**`.asmdef` 是编译期真相**；本表解释每个 Module 为什么存在，结构变化时同步更新。

## 设计不变量

1. `Game.Framework` 是运行时内核，只依赖通用异步/响应式/Inspector 基础库，不依赖具体资源、UI 后端、Protobuf 或业务程序集。
2. 可替换能力采用“Interface 在稳定侧，Implementation 在可删除 Module”的 Seam：例如 `IAssetProvider ← YooAssetProvider`、`IUIBackend ← UGUI/Toolkit Adapter`。
3. `Game.Framework.Boot` 是 AOT 薄壳，永不引用 `Game.Framework*` 运行时程序集，否则框架无法作为热更新代码加载。
4. Editor Implementation 与 Runtime 分离；重第三方 Editor 依赖放进独立 Editor Module，不污染通用 `Game.Framework.Editor`。
5. 运行时内核和可热更新 Module 保持 `autoReferenced:false`；消费方通过 asmdef 显式声明依赖。
6. 删除测试：移除一个可选 Module 目录后，只应失去该能力及其直接消费方，不应迫使核心 Module 修改源码。
7. 外部程序集的直接依赖必须在 asmdef 显式可见，即使插件 DLL 的 auto-reference 已让代码偶然编译通过；否则 UPM 声明、删除测试与 AI 导航都看不到真实代价。

## 轻量组合档位与证据口径

菜单 `SSFramework/诊断/模块裁剪审计` 会读取当前目标平台的 Player 编译图，再读取已编译 DLL 的**真实元数据引用**，给出 Core-only、Core + UGUI、Core + Toolkit、全部 Runtime Module 和当前 HybridCLR 热更档位的闭包。它同时机器执行三条删除测试：Core 不带 UI、UGUI 不带 Toolkit/Bridge、Toolkit 不带 UGUI/Bridge。

报告里的大小是链接、AOT、压缩前的原始托管 DLL，只用于发现“一个很小的 Adapter 意外拖入很大的外部依赖”以及比较组合；它不是最终包体承诺。WebGL、小游戏等强体积约束项目仍应对真实目标平台出 Player BuildReport，再决定是否值得增加程序集粒度。

## 程序集地图

| Module | 路径 | 职责 / 边界 | 删除测试 |
|---|---|---|---|
| `Game.Framework` | `Core/` | Context、Container、MVCS 权限、Command/Event、生命周期与通用 Interface；含零第三方实现的 Storage/Audio/Flow/Localization/Logging/Network 等能力。 | 不可删除；其余运行时 Module 的稳定依赖方向指向它。 |
| `Game.Framework.Asset.Yoo` | `Asset.Yoo/` | `IAssetProvider` 的 YooAsset Adapter，YooAsset 接触面集中在这里。 | 删除后仅失去 YooAsset Implementation；内核资源 Interface 仍可编译。 |
| `Game.Framework.Asset.Yoo.Tests` | `Asset.Yoo/Tests/Editor/` | Yoo package 进程级 Reader/Writer、取消、缓存世代、同步快照与后台终态的纯 EditMode 契约。 | 随 Yoo Adapter 删除；不进入玩家构建，也不让通用 Core Test 反向依赖可选 Adapter。 |
| `Game.Framework.Config` | `Config/` | 配置运行时编排与 `IConfigUtility<TTables>`；不依赖 Luban。 | 删除后失去配置表 Module，Core 不改。 |
| `Game.Framework.Config.Editor` | `Config/Editor/` | Luban CLI/Profile/生成与配置总览入口。 | 可与 Config 一起删除；不向 Runtime 泄漏 Editor 依赖。 |
| `Game.Framework.Fonts` | `Fonts/` | TMP/Toolkit 多语言 fallback 链；TMP 依赖收口。 | 删除后仅失去自动字体链，本地化 Interface 仍可用。 |
| `Game.Framework.Fonts.Editor` | `Fonts/Editor/` | 常用字集扫描与生成。 | 可独立删除，不影响运行时字体链。 |
| `Game.Framework.Network.Proto` | `Network.Proto/` | Google.Protobuf Adapter，把生成的 `IMessage` 接到内核网络 Seam。 | 删除后 JSON 与内核手写 Protobuf 仍可用。 |
| `Game.Framework.Network.Proto.Editor` | `Network.Proto/Editor/` | protoc Profile、代码生成与总览入口。 | 可与 Proto Runtime 一起删除，Core 不改。 |
| `Game.Framework.UI` | `UI/` | 渲染中立窗口编排、层级/栈/模态/过渡及后端 Interface；当前也承载 ObservableCollections 增量列表引擎，因此该第三方依赖会随 UI Core 进入托管闭包。 | 删除后失去窗口框架与列表绑定引擎，但 Core MVCS 仍可用。 |
| `Game.Framework.UI.UGui` | `UI.UGui/` | UGUI Window/View Adapter，含 `Transform → GameObject` 的 `Bag.BindList` Adapter。 | 删除后 Toolkit 后端与 UI Core 仍可编译。 |
| `Game.Framework.UI.UGui.Editor` | `UI.UGui/Editor/` | UGUI 节点绑定生成等 Editor 工具。 | 可独立删除，不影响 UGUI Runtime 手写接线。 |
| `Game.Framework.UI.UGui.Editor.Tests` | `UI.UGui/Editor/Tests/` | UGUI 绑定 Inspector、Popup 与 Overlay 的 EditMode 布局/偏好契约。 | 随 UGUI Editor Module 删除；不进入玩家构建。 |
| `Game.Framework.UI.Toolkit` | `UI.Toolkit/` | UI Toolkit Window/View Adapter、`VisualElement` 的 `Bag.BindList` Adapter 与 RenderTexture 显示原语。 | 删除后 UGUI 后端与 UI Core 仍可编译。 |
| `Game.Framework.UI.Bridge` | `UI.Bridge/` | UGUI/相机内容嵌入 Toolkit 的 RenderTexture Adapter。 | 删除后两套独立 UI 后端仍可用。 |
| `Game.Framework.Boot` | `Boot/` | HybridCLR/YooAsset 热更启动 AOT 薄壳。 | 可在无热更项目删除；不得反向依赖 Framework Runtime。 |
| `Game.Framework.Editor` | `Editor/` | Core 通用 Drawer、诊断窗口、菜单与配置总览。 | 玩家构建不包含；可替换 Editor 体验而不动 Runtime API。 |
| `Game.Framework.Editor.Tests` | `Editor/Tests/` | 通用 Editor 工具的 EditMode 契约；覆盖 AI PlayMode 预检的无弹窗保存与未命名场景拒绝。 | 随 Editor Module 删除；不进入玩家构建。 |
| `Game.Framework.Build.Editor` | `Build/Editor/` | YooAsset/HybridCLR 构建管线与 Profile。 | 无资源/热更构建需求时可删，不污染通用 Editor。 |
| `Game.Framework.Demo` | `Demo/` | 32 个可运行教学章节，是所有 Module 的消费方与集成样板；Catalog 集中拥有章节 Adapter、生命周期与 Host 教学语义校验。 | 可整体删除；`UNITY_EDITOR` define 保证不进玩家包。 |
| `Game.Framework.Demo.Tests` | `Demo/Tests/` | Demo 专属 EditMode 门禁：章节生命周期/回滚、教学形态与结构化降级契约、内嵌服务器、关键示例行为及全部 CodeRef 防腐。 | 随 Demo 一起删除；不让 Demo 专属依赖反向进入通用 Test Module。 |
| `Game.Framework.Demo.PlayMode.Tests` | `Demo/Tests/PlayMode/` | 加载真实 DemoScene，穿过 Context、Catalog 与 Shell 逐章 Build 32 个 Adapter，并验证真实缺依赖降级页。 | 随 Demo 一起删除；不进入玩家构建，也不把场景集成依赖塞回纯 EditMode 门禁。 |
| `Game.Framework.Test` | `Test/Scripts/` | Framework PlayMode/EditMode 契约和回归测试；`Test/Res/SuspendedSceneProbe` 是无业务 Awake 的 Yoo 场景激活门 fixture。 | 产品运行不依赖；开发/CI 不应删除。 |

## 维护检查清单

- 新增程序集前先问：它是否拥有独立的变化原因、第三方依赖或删除边界？如果没有，优先放进现有 Module。
- 新增 Interface 前做删除测试：去掉某个 Implementation 后，调用方是否仍能以同一抽象工作？只有真实 Seam 才值得抽象。
- 新增第三方库时，先放入 Adapter Module；不要为了“以后也许替换”把每个类都拆成一对 Interface/Implementation。
- 修改 asmdef 引用后，运行完整 Unity 测试，并检查 Boot、Core、两个 UI 后端与可选 Module 的依赖方向。
- 运行 `SSFramework/诊断/模块裁剪审计`；隐式外部引用应为 0，三条删除测试应通过。报告变大只作为调查信号，不以原始 DLL 字节直接宣称最终包体回归。
- 本文与实际 `.asmdef` 不一致时，以 `.asmdef` 为准并立即修正文档。
