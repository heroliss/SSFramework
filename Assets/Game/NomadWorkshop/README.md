# 《游牧工坊》技术 Spike

> 状态：**居民 Utility AI + 实时 3D 灰盒证据 v0.1**，更新于 2026-09-01。它是可删除的技术验证，不是 Foundation Prototype、垂直切片或正式资产基线。当前产品真值见 [`docs/nomad-workshop-game-vision.md`](../../../docs/nomad-workshop-game-vision.md)。

## 当前证明了什么

- 居民决策内核可以脱离 Unity 场景运行：按需求压力、工作价值、玩家优先级、个人倾向、等待时间与执行成本评分；
- 同意图目标先归并，紧急候选优先进入选择池，再在相对高分短名单中用确定性 Softmax 抽样；
- 同一世界 Seed、居民稳定 ID 与决策序号会重现相同随机值、候选分解和选择；
- 目标、材料与设施交互位可以全有或全无地预留，失败不会残留部分占用；
- Unity 展示层能把选中行动推进为“走到设施 → 执行 → 结算 → 再决策”，并显示中文诊断面板；
- 固定正交视角中的实时 3D 甲板、设施和居民占位体具有足够的初步可读性。

这些证据尚不能证明游戏好玩、正式画面达标、多人居民调度自然或参数已经平衡。

## 目录与边界

```text
NomadWorkshop/
├── Simulation/       # 无 UnityEngine 依赖：候选、效用选择、轨迹与预留
├── Runtime/          # 可删除灰盒表现：程序 3D 假人、行动执行与 IMGUI 诊断
├── Scenes/           # 只经 Unity Editor 保存的 Spike 场景
└── Tests/
    ├── EditMode/     # 纯规则与确定性契约
    └── PlayMode/     # 隔离场景中的最小运行路径
```

`UtilityDecisionEngine` 只决定“现在做什么”；它不拥有寻路、任务进度、资源结算或动画。`ReservationLedger` 只处理单线程模拟中的原子占用。`UtilityAiSpikeController` 是临时表现 Adapter，正式游戏不应把规则放进 Animator 回调或从假人代码继续堆产品逻辑。

这些程序集暂时不引用 SSFramework：当前风险是 Utility AI 语义和 3D 可读性，不需要用 Context 包装后再证明一次。进入 Foundation Prototype 时，游戏 Runtime 会作为独立业务程序集消费 Framework 的时钟、存档、UI、资源和诊断等公共接缝；只有出现跨游戏证据后，游戏专属 AI 才考虑回流 Framework。

## 运行与观察

1. 用 Unity 打开 [`Scenes/UtilityAiSpike.unity`](Scenes/UtilityAiSpike.unity) 并进入 Play；
2. 左侧面板显示需求、动力损伤、积压、当前行动以及每个候选的效用分项、概率和排除原因；
3. “制造严重故障”用来观察紧急工作层，“提高 / 降低”用来观察玩家优先级；
4. “重置”恢复固定 Seed 和初始状态，便于重现同一决策序列。

当前角色是程序生成的 3D 占位体，只验证移动与交互接缝。它没有 Humanoid 骨架、Avatar、重定向动画、IK 或正式寻路，因此不能用本场景声称“3D 角色资产管线已经成立”。

项目默认 URP 配置当前仍以 `Renderer2D` 为默认 Renderer；本 Spike 已证明 MeshRenderer 主体能显示，但没有证明正式 3D 光照、阴影、后处理或性能预算。下一个视觉证据应增加游戏专属 Universal Renderer 配置，不覆盖 Framework Demo 基线。

## 当前测试证据

- EditMode：`Game.NomadWorkshop.Simulation.Tests`，9 个测试；
- PlayMode：`Game.NomadWorkshop.PlayMode.Tests.UtilityAiSpikePlayModeTests`，1 个隔离 smoke；
- Game View：检查过固定镜头、中文诊断、设施与居民可见性；截图属于临时证据，位于被 Git 忽略的 `Screenshots/`。

测试重点覆盖危险口渴压过休闲、多需求行动按实际压力得分、同 Seed 重现、不同 Seed 只在短名单内变化、重复设施不放大意图概率、无正效用时安全等待，以及冲突预留不泄漏。

## 明确未做

- 三名居民同时投标、路径拥堵、设施队列与任务中断恢复；
- 行动承诺和任务老化的长时间平衡统计；
- 标准 Humanoid 模型、共享动作库、设施锚点和 IK；
- 正式 NavMesh / 网格寻路、建造、库存、保存读取和 Framework Context 接线；
- 正式 UI、艺术指导、音效、性能采样、Player Build 与玩家体验验证。

下一步优先完成“共享 Humanoid 角色 + 五个通用动作 + 三类设施锚点”的资产管线 Spike；若外部模型或动作来源尚未确定，可以先增加第二与第三名占位居民，验证预留、任务竞争、行为解释和长跑稳定性。
