# Game.Framework 内部编码规则

本文件记录框架层（`Assets/Game/Framework/`）**源码内部**的编码约束。AI Agent 编辑此目录文件时会自动加载本文件（目录就近性）。

框架 **API 使用规则**（在 `Assets/Game/` 任意目录编写业务代码时适用）见 `Assets/Game/AGENTS.md`。

## 注释标准

框架源码是给业务层长期复用的公共基础设施，注释需要覆盖“维护者读代码时最容易误判”的位置：

- 公共接口、公共类、`protected` 成员：说明职责、使用方式、生命周期和边界，而不是只翻译名字。
- 异步、取消、释放、缓存、反射、初始化顺序、第三方库适配：必须写清楚设计原因和失败/边界行为。
- 关键私有方法或逻辑块：当实现依赖非显然约束时，用短注释解释原理。
- 简单字段、普通属性、直观分支不强行注释；避免“获取 X”“设置 Y”这类无信息注释。

注释语言保持通俗直接，优先帮助后来者理解“为什么这样设计、用错会怎样、框架替调用方兜住了什么”。

## 程序集结构与复用边界

框架目标是**多项目可复用**，第一阶段留在 `Assets/Game/Framework`，但用 asmdef 边界做成自洽模块（未来可一键抽成 UPM 包，见 `docs/adr/`）。

| 程序集 | 路径 | 内容 |
|---|---|---|
| `Game.Framework`（运行时） | `Framework/Scripts/` | 全部运行时代码 + `RP<T>`（`Scripts/Reactive/RP.cs`） |
| `Game.Framework.Editor` | `Framework/Editor/` | 通用编辑器代码：`RPDrawer` / `AssetReferenceDrawer` / 菜单。`includePlatforms:["Editor"]` |
| `Game.Framework.Build.Editor` | `Framework/Build/Editor/` | 资源构建管线（`FrameworkAssetBuilder` / 统一构建菜单 / 构建配置 SO），引用 `YooAsset.Editor`。独立子程序集把 YooAsset.Editor 依赖隔离在此，不污染通用 `Game.Framework.Editor`。`includePlatforms:["Editor"]` |
| `Game.Framework.Demo` | `Framework/Demo/` | 示例，引用框架做"消费方边界"活样板 |
| `Game.Framework.Test` | `Framework/Test/` | PlayMode 测试（在 Unity Test Runner 窗口手动跑） |

**复用铁律：**
- `Game.Framework` / `.Editor` **禁止引用任何项目业务代码**（Assembly-CSharp 或业务 asmdef）。依赖只能指向声明在 asmdef references 里的第三方/Unity 程序集。
- 通用编辑器代码放 `Game.Framework.Editor`，不要在运行时 asmdef 里写 `#if UNITY_EDITOR` 的 `PropertyDrawer`/`EditorWindow`（历史遗留逐步清理）。**例外**：带重第三方依赖的内聚编辑器子模块（如资源构建管线依赖 `YooAsset.Editor`）单独开 editor asmdef（`Game.Framework.Build.Editor`），把第三方依赖隔离在子程序集，不让通用编辑器程序集背上——也利于将来换后端时整块替换。
- 新增第三方依赖先加到 `Game.Framework.asmdef` 的 references，再用；优先 UPM/asmdef 名引用。

## MonoLayerBase：三层 Mono 基类的共享实现

`MonoModelBase`/`MonoSystemBase`/`MonoUtilityBase` 都是 `MonoLayerBase<TLayer>`（`Internal/MonoLayerBase.cs`）的薄壳，只声明 `[DefaultExecutionOrder]` + 层标记接口。注册/注入/AssetReference 绑定/OnDestroy 释放+反注册的样板**集中在 `MonoLayerBase`**。
- 改这套生命周期逻辑改 `MonoLayerBase` 一处即可，三层自动一致。
- `[DefaultExecutionOrder]` 必须留在具体类（按具体类型生效，泛型基类标不生效）。
- `MonoViewBase` 不注册到容器（只 Inject），保持独立、不继承 `MonoLayerBase`。
- OnDestroy 反注册的 IsDisposed 短路（父 Context 先销毁场景）已在基类实现，业务/新层无需重写——除非你新增一个**会注册到容器的** Mono 层基类，那时照搬 `MonoLayerBase` 模式。
