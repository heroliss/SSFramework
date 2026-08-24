# ADR-0027：响应式集合与列表绑定 —— ObservableCollections + 后端中立增量绑定

**Status:** Accepted（2026-07-05）

## Context

roadmap 中期最后一项：`RP<T>` / `ReadOnlyReactiveProperty<T>` 是**单值**流——一个 HP、一个分数、一段文本，变化时推新值，`Bag.BindText` / `BindEnabled` 订阅刷新。可是「一串会增删的东西」——背包格子、聊天记录、在线玩家、排行榜、队伍成员——是**集合状态**，R3 单值订阅覆盖不到。

现状能怎么做、为什么不够：
- 把集合塞进 `RP<IReadOnlyList<T>>`，每次增删推**整包**新列表。View 收到后只能「清空容器 → 重建全部子视图」。代价：丢滚动位置 / 选中 / 输入焦点，每帧重建抖 GC，大列表卡顿。它能跑，但把「加一项」放大成「重画整表」。
- 缺的是**增量通知**：集合告诉订阅者「第 3 位插了一个」「第 5 位被删了」「0 和 2 换位了」，UI 只动那一处。

Cysharp 的 **ObservableCollections** 正是这个原语（与已用的 UniTask / R3 同生态，roadmap「Cysharp 生态候选」已列）：`ObservableList<T>` 持有集合并发出细粒度变化事件；配套 **Observablecollections.R3** 把变化桥成 R3 `Observable<T>`，与框架「一切皆流 + Bag 订阅」的心智无缝。两个包已随 NuGetForUnity 装好（`Packages/nuget-packages`），且与框架用的同一个 R3 程序集（1.3.1）绑定，无类型身份冲突；DLL 设置与 R3.dll 一致（auto-reference、全平台），IL2CPP / 热更同样兜得住。

实测 `ObservableList<T>.ObserveChanged()` 的语义（决定绑定正确性的关键，已在 Editor 验证而非猜测）：
- 每次结构变化摊成**逐项** `Add` / `Remove` / `Move` 事件——`Add`/`AddRange`/`Insert` 都是逐项 `Add`（`NewStartingIndex` 为真实插入位）；`RemoveAt`/`RemoveRange` 逐项 `Remove`；`Move` 一条；索引器赋值一条 `Replace`；`Clear` 一条 `Reset`。
- 订阅**不回放**已有项——绑定时必须显式种入当前快照。

## Decision

### 1. 集合原语直接用 `ObservableList<T>`，不包装、不加别名

Model 持有集合就用 `ObservableList<T>`（如 `RP<T>` 之于单值）；只读暴露用它实现的 `IReadOnlyObservableList<T>`（如 `ReadOnlyReactiveProperty<T>` 之于单值——只读、仍可观察，零分配直接返回）。查询 Command 返回 `IReadOnlyObservableList<T>`，View 增量绑定。

- **不做 `OL<T>` 别名**：`RP<T>` 存在是因为 `SerializableReactiveProperty<T>` 又长又要 Inspector 序列化；`ObservableList<T>` 名字本就短、且不是 Unity 可序列化类型，加别名是纯噪音。
- **不包装成框架接口**：像 R3 的 `Observable` 一样直接用库类型。框架的贡献是**绑定层 + 约定 + 文档**，不是把集合再套一层壳。业务代码直接 `using ObservableCollections;`，框架内核零改动；因此第三方类型在 Model 侧是显式依赖，`Bag.BindList` 隐藏的是增量索引与逐行生命周期 Implementation，并没有假装隔离整个库。

### 2. 后端中立的增量绑定引擎放共享 UI 程序集（`Game.Framework.UI`）

`ReactiveListBinding.Bind<TSource,TItem>(bag, source, createItem, attach, detach, reorder)` 是**唯一**一份增量维护逻辑——种入快照、订阅 `ObserveChanged`、按 `Add/Remove/Replace/Move/Reset` 维护一份与源逐项对应的「子视图 + 子 bag」表。刻意**不认识** `VisualElement` / `Transform`：把「挂 / 摘 / 移」三个动作委托给后端。

- **放 `Game.Framework.UI`（两后端都引用的共享层）而非内核**：绑定是 UI 关注点，不该给范式无关的内核加 ObservableCollections 依赖（守住「接入 UI，内核零改动」不变量）；放共享层又让 Toolkit / UGUI 两个 `Bag.BindList` 各自只写 ~15 行适配，diff 的脏活（索引管理、每项子作用域、销毁时序）只此一份、只测一次。
- **每项一个子 `DisposableBag`**：随该项进出列表创建 / 销毁，项内订阅（「这一行血条随 RP 刷新」）挂它，项被移除时自动退订——与 `Bag` 统一生命周期心智一致。子 bag **不挂宿主 bag**（否则列表长期高频增删会让宿主 composite 累积已 dispose 的子 bag，构成泄漏），由引擎自己持有、按项释放、解绑时兜底全清。
- **Replace = 重造该槽**：子视图视为「元素值的纯函数」，换值即销毁旧行造新行（最稳妥）；就地更新是业务在项内订阅里的事。

### 3. 两个后端各一个 `Bag.BindList` 适配

- **UI Toolkit**（`UIBindingExtensions.BindList`）：绑到 `VisualElement` 容器，子视图是 `VisualElement`。attach=`container.Insert(i, view)`、detach=`view.RemoveFromHierarchy()`、reorder=摘下再插回目标位。
- **UGUI**（`UGuiListBindingExtensions.BindList`）：绑到 `Transform` 容器，子视图是 `GameObject`。attach=`SetParent(container,false)`+`SetSiblingIndex(i)`、detach=`Object.Destroy`、reorder=`SetSiblingIndex(i)`。

同一套写法只换容器与项类型；换后端业务绑定代码结构一致。

### 4. 刻意不做

- **虚拟化 / 滚动复用**：`BindList` 为每项造一个常驻子视图，目标是**项数适中**的 UI 列表（背包 / 聊天 / 设置项 / 队伍）。上万项要虚拟化用 UI Toolkit 原生 `ListView`（`itemsSource` + `RefreshItems`），guide §24 给姿势，**不包一层 `BindListView`**——`ObservableList<T>` 未实现非泛型 `IList`，硬桥 `itemsSource` 会留下强转的脏角，且虚拟化大列表是较罕见场景，用现有原语组合即可（[[feedback-no-over-engineering]]）。
- **过滤 / 排序视图**：ObservableCollections 有 `CreateView` / `AttachFilter`，但那是另一层能力；需要就在业务侧组织数据或直接用库的 view，不进框架绑定 API。
- **字典 / 集合 / 队列绑定**：先只做列表（`IReadOnlyObservableList<T>`）——UI 列表是最普遍刚需；其余集合类型按需再说。

## Consequences

- 集合状态有了与单值 `RP<T>` 对称的原语（`ObservableList<T>`）与绑定（`Bag.BindList` ↔ `Bag.BindText`），一套心智覆盖「单值」与「集合」两类响应式状态。
- 内核零改动、不新增内核依赖；ObservableCollections 只在 UI 层与业务层出现，守住范式无关内核不变量。绑定引擎在 `Game.Framework.UI`（热更列表内），两后端共享。
- 增量维护逻辑单点、纯 C# 可测（`ReactiveListBindingTests` 用引用式假容器覆盖种入 / 增删移换 / 换值 / Reset / 解绑 + 每项子 bag 释放），不依赖场景与帧推进。
- Test 程序集 `overrideReferences:true`，显式补了 `ObservableCollections.dll` + `ObservableCollections.R3.dll` 两条 precompiledReferences；运行时共享 UI asmdef 也显式声明两者，两个后端为公开绑定签名显式声明 `ObservableCollections`，不再借 auto-reference 隐藏依赖。
- 五件套齐：本 ADR / 引擎（`Game.Framework.UI/ReactiveListBinding.cs`）+ 双后端适配 / 测试（`ReactiveListBindingTests`）/ demo「响应式列表 · 集合绑定」章（`Modules/ReactiveListModule.cs`）/ guide §24 + AGENTS #31。
- ObservableCollections 从「roadmap 候选」变成已融入依赖：Model 显式使用集合类型，`Bag.BindList` 收口增量维护与逐行生命周期 Implementation；不再用“整个库都被隐藏”描述这条依赖。

**2026-08-24 模块审计补充：**共享引擎当前位于 `Game.Framework.UI`，所以任何窗口框架消费方都会在编译闭包中看到 ObservableCollections，即使没有调用 `BindList`；两个后端 asmdef 也显式声明各自公开签名里的集合类型。该耦合是可见且可测的，不再借 NuGet auto-reference 隐藏。是否进一步拆成可选 Module，须以目标平台 Player BuildReport 证明收益；编辑器 `SSFramework/诊断/模块裁剪审计` 先提供 Core-only / 单后端 / 完整 / 当前热更档位的原始托管闭包与删除测试，避免为理论体积提前制造浅 Module。

**2026-08-24 验证补充：**Demo 为每个真实行 View 增加稳定实例身份，并从 item factory 与 rowBag Dispose 两个 Seam 采集创建 / 释放 / 存活计数；画面可直接观察 Move 复用同一行、Replace 仅重造一槽。EditMode 契约同时断言真实 `VisualElement` 引用、父子层级与 rowBag 释放状态，避免“列表结果正确”掩盖整表重建或逐行生命周期错误。
