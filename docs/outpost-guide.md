# Outpost 垂直切片导读 —— 用一个小游戏串起框架

> 给谁看：**刚接触本框架、或已读过 [framework-guide.md](framework-guide.md) 想看"真项目怎么落地"的人。**
> Outpost 是一个刻意做小的塔式生存自动战斗 demo，它存在的首要目的不是"好玩"，而是**把框架的十来个能力在一个真实消费场景里串起来、并顺手验证接缝**。本文是"游戏里看得见的现象 ↔ 框架能力 ↔ 源码位置"的对照地图。

代码位置：[`Assets/Game/Outpost/`](../Assets/Game/Outpost/)。程序集 `Game.Outpost`（业务）+ `Game.Outpost.Sim`（零引擎依赖的纯 C# 模拟）。

---

## 30 秒看懂它在演示什么

一句话：**玩家固定居中的炮塔自动转向索敌开火，敌人从四周一波比一波多地涌入，每波清空后三选一强化，直到哨站被摧毁——无限模式，比拼坚持到第几波、击杀多少。** 全部美术是程序几何体 + URP 后处理辉光，没有一张贴图。

这个"最小可玩闭环"故意压得很薄，好让每一处都对应一个可指认的框架用法：

| 你在游戏里看到 | 背后的框架能力 | 落在哪 | 深读 |
|---|---|---|---|
| 标题→战斗→结算的界面切换 | **游戏流程状态机** `IGameFlow`（一次性状态、传参走构造） | [`Scripts/Flow/`](../Assets/Game/Outpost/Scripts/Flow/) | guide §20 / [ADR-0023](adr/0023-game-flow.md) |
| 标题的"框架看点"弹窗盖住标题页 | **UI 窗口栈 + 模态遮罩**（Popup 层 + Modal） | [`Windows/AboutWindow.cs`](../Assets/Game/Outpost/Scripts/Windows/AboutWindow.cs) | guide §17 / [ADR-0016](adr/0016-ui-framework.md) |
| HUD 实时刷血量/波次/击杀/得分 | **读写分离 + 只读订阅**（View 经查询 Command 拿 `ReadOnlyReactiveProperty`） | [`Battle/BattleHudView.cs`](../Assets/Game/Outpost/Scripts/Battle/BattleHudView.cs) | guide §5 / [ADR-0001](adr/0001-five-layers-and-permission-interfaces.md) |
| 波间弹出三选一升级卡片 | **响应式集合增量绑定** `ObservableList` + `Bag.BindList` | [`Battle/UpgradeChoiceView.cs`](../Assets/Game/Outpost/Scripts/Battle/UpgradeChoiceView.cs) | guide §24 / [ADR-0027](adr/0027-reactive-collections-list-binding.md) |
| 点卡片 → 升级即时生效 | **命令外发**：View 不能直调 System，写意图经 `ExecuteCommand` 中转 | [`Battle/UpgradeCommands.cs`](../Assets/Game/Outpost/Scripts/Battle/UpgradeCommands.cs) | guide §3 |
| 敌人 / 曳光 / 飘字 / 脉冲 / 碎片成群出现又消失 | **对象池** `IPoolUtility`（`Bag.Spawn`/`Despawn` 自动借还） | [`Battle/BattleDirectorSystem.cs`](../Assets/Game/Outpost/Scripts/Battle/BattleDirectorSystem.cs) | guide §7 / [ADR-0007](adr/0007-custom-object-pool.md) |
| 敌人三种（炮灰无人机 / 快速突袭者 / 重甲装甲兵）、升级六种、无限波次成长参数 | **配置表** Luban（强类型只读数据、全层可读的 Utility） | [`Config/Gen/`](../Assets/Game/Outpost/Config/Gen/) | guide §16 / [ADR-0009](adr/0009-luban-integration.md) |
| 整个战斗的规则演算 | **纯 C# 模拟接缝** `IBattleSim`（可 AOT / 可单测 / 可置换 ECS） | [`Sim/`](../Assets/Game/Outpost/Sim/) | [ADR-0014](adr/0014-realtime-simulation-ownership.md) |

---

## 三个最值得看的设计

### 1. `IBattleSim`：把"规则"关进零引擎依赖的盒子

战斗的所有数值演算（移动、索敌、开火、结算）都在 [`Sim/`](../Assets/Game/Outpost/Sim/) 里，那是一个 **`noEngineReferences` 的程序集**：不 `using UnityEngine`，坐标用 `System.Numerics.Vector2`。

为什么这么做：

- **可单测 / AI 可验证**：规则是确定性纯函数，`new ReferenceBattleSim()` 喂一段 Tick 序列毫秒级跑完，不用进 Play。平衡就是这样在编辑器里"跑数"调出来的（见 `BattleSetupFactory`）。
- **可 AOT**：热更/裁剪环境永不炸。
- **可置换后端**：`ReferenceBattleSim`（面向对象参考实现）是接缝的第一个后端；里程碑 M6 会加一个 ECS/DOTS 后端塞进同一个 `IBattleSim`，做"同题对比"。届时**上层一行不改**。

> 接缝的价值只有在"真被换一次"时才结算——这正是 Outpost 存在的原因之一。

### 2. 事件 → 表现的翻译层

模拟只往外发**领域事件**：`EnemySpawned` / `EnemyHit` / `EnemyDetonated` / `WaveCleared`。它不知道什么是 GameObject、曳光、Bloom。玩法上是一门"近防炮拦截来袭弹"：敌人径直冲基地，抵达即自爆（`EnemyDetonated`，一次性伤害）；在离基地过近处击毁会吃拦截溅射（`EnemyHitEvent.SplashDamage`，越近越疼）。**炮塔的开火与回转都是内核的真机制**——按回转速度逐帧转向最近目标、炮口对准（容差内）才开火，射速带**预热缓升缓降**（`IBattleSim.SpinUp`，近防炮点火感）且**无上限**，后期靠攻速升级能飙到每分钟上万发的火墙。攻击 / 射程 / 回转都封顶、唯独射速不封：攻击封顶让敌人不再被秒杀（得多发命中，火力压力交给射速），**回转封顶**是无限模式能收尾的关键——面对 360° 密集来袭，扫不过来的方向必然漏怪，击杀率随数量渐降直至被压垮。波次由角色表 `WaveRole` + 全局 `WaveScaling` 逐波程序化生成、越来越多，唯一终态是失守——这些规则全在纯 C# 内核里，表现层只负责演出（`TurretAngle` / `SpinUp` 供表现层画炮管指向与火墙强度）。

[`BattleDirectorSystem`](../Assets/Game/Outpost/Scripts/Battle/BattleDirectorSystem.cs)（一个 `System`）订阅这些事件，把它们翻成 Unity 表现：池化敌人、发光炮塔、弹道曳光、脉冲圈、伤害飘字、相机震动；同时把聚合值（血量/波次/得分）写进 `BattleModel` 供 HUD 只读订阅。

**换 ECS 后端时，这层翻译原样保留**——因为它只依赖接口事件，不依赖某个后端的内部结构。视觉升级（五种敌人形状、雷达扫描射程圈、拦截/自爆爆炸 + 冒烟 + 碎片飞溅、受创震屏、后期射速火墙）全部加在这一层，模拟数学一字未动。后期"每秒上百次命中 + 海量敌人 + 每发特效"正是 OOP 对象池吃力、帧率下探之处（曳光已做每帧上限降级但伤害仍每发结算）——这正是 M6 换 ECS/Burst 想在同场景压出差异的看点。

### 3. 读写分离，且"层 + 消费者"同一子树整棵撤

- **读**：HUD / 升级面板是 View，只能经查询 Command 拿到 `ReadOnlyReactiveProperty` 订阅，读不到也写不到 Model/System（编译期权限接口挡住）。
- **写**：任何改动都走 `ExecuteCommand`。连"选了个升级"也是——`UpgradeChoiceView` 发 `ChooseUpgradeCommand`，命令再 `ctx.GetSystem<BattleDirectorSystem>().ChooseUpgrade(id)`，因为 View 不能直接碰 System。
- **生命周期**：战斗私有的 `BattleModel` / `UpgradeModel` / 对象池都注册在 [`BattleContext`](../Assets/Game/Outpost/Scripts/Battle/BattleContext.cs)（战斗子场景的根）。战斗场景一卸载，这棵子上下文连同它的层、订阅、池化对象整棵销毁——下一局全新一份，**零跨局残留、零手动清理**。这就是框架说的"换血不允许，撤就整棵撤"。

---

## 推荐阅读路径（顺着代码读一遍）

1. [`Scripts/Flow/`](../Assets/Game/Outpost/Scripts/Flow/) —— 先看宏观阶段：`BootState → TitleState → BattleState → ResultState`，理解 `IGameFlow` 怎么切界面 + 传参。
2. [`Sim/IBattleSim.cs`](../Assets/Game/Outpost/Sim/IBattleSim.cs) + [`ReferenceBattleSim.cs`](../Assets/Game/Outpost/Sim/ReferenceBattleSim.cs) —— 规则内核，纯 C#，最好懂。
3. [`Battle/BattleContext.cs`](../Assets/Game/Outpost/Scripts/Battle/BattleContext.cs) + [`BattleDirectorSystem.cs`](../Assets/Game/Outpost/Scripts/Battle/BattleDirectorSystem.cs) —— 子上下文注册了什么、导演怎么把事件翻成表现和 Model。
4. [`Battle/BattleHudView.cs`](../Assets/Game/Outpost/Scripts/Battle/BattleHudView.cs) + [`BattleQueries.cs`](../Assets/Game/Outpost/Scripts/Battle/BattleQueries.cs) —— 只读订阅一侧长什么样。
5. [`Battle/UpgradeChoiceView.cs`](../Assets/Game/Outpost/Scripts/Battle/UpgradeChoiceView.cs) + [`UpgradeModel.cs`](../Assets/Game/Outpost/Scripts/Battle/UpgradeModel.cs) —— 集合绑定 + 命令外发一侧长什么样。

---

## 视觉为什么"全是几何体"

这是刻意的，不是没时间做美术。Demo 的重点是**框架 + 未来 DOTS 置换验证**，美术资产越少，越能让读代码的人把注意力放在数据流和接缝上，也让 M6 的 ECS 压力波次不受美术拖累。

所以视觉升级都限定在"**程序几何 + 后处理辉光**"内做可读性，不引入贴图/精灵：

- 敌人用程序网格换形状表达身份（五种）——炮灰无人机是**小三角**、快速突袭者是**箭头**、重甲装甲兵是**厚重六边形**、极速掠袭机是**细长针**、重装攻城核是**厚八边形**，有向种逐帧转向来袭方向（[`OutpostMeshes.cs`](../Assets/Game/Outpost/Scripts/Battle/OutpostMeshes.cs)）。
- 背景做成"火控台"读法——射程圈内有极淡填充盘（"我的火力覆盖区"）+ 缓慢旋转的雷达扫描臂（"正在索敌"），外缘是呼吸的暖红危险警戒环（[`ArenaDecor.cs`](../Assets/Game/Outpost/Scripts/Battle/ArenaDecor.cs)）。
- 炮塔转到位才开火、且射速带预热：这在无限模式里是**内核的真机制**——`ReferenceBattleSim` 按回转速度逐帧转向最近目标、炮口对准（容差内）才发命中事件（hitscan 同帧结算），有效射速 = 基础射速 × 预热系数（`SpinUp` 缓升缓降）；表现层按 `TurretAngle` 画炮管、`SpinUp` 涨亮核心、命中即放炮口闪光与曳光（高射速下每帧限量、只降级视觉不影响伤害）。慢回转在切换分散目标时留出空当，"回转伺服"升级正是解药。

---

## 里程碑与已知偏离

里程碑：M0 骨架闭环 → M1 战斗核心 + 视觉 → M2 波间三选一 →（M3 存档/音频/本地化 → M4 网络排行 → M5 构建收口 → M6 DOTS 置换）。当前已到 M3 起步（历史战绩存档），并把玩法改造成**无限模式**：一波比一波多而难、无胜负终态（唯有失守）。敌人五种（慢弱靠量的炮灰无人机 + 快速突袭者 + 重甲装甲兵 + 极速掠袭机 + 重装攻城核），六种升级里**射速无上限**（后期每分钟上万发的火墙）、攻击/射程/回转封顶——回转封顶是长局能收尾的关键。后期的"海量低血炮灰 + 每分钟上万发命中 + 每发特效"正是为 M6 的 ECS 压力置换铺垫的真实高吞吐场景。

已知偏离（诚实记录）：

- **波间抉择未用"嵌套子 Flow"**：原计划想借它验收框架的子阶段状态机，但抉择本质是导演的一个暂停相位，现有相位机已自然容纳，再套一层子 `GameFlow` 属过度设计。§28 的嵌套子 Flow 验收留给更贴合的场景单独做。

更完整的框架能力清单、每个 §N 的教学与 API 见 [framework-guide.md](framework-guide.md)；架构决策的来龙去脉见 [docs/adr/](adr/)。
