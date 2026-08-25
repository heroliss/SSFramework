# ADR-0008：HybridCLR 热更集成 —— 列表驱动的热更机制与程序集分层

**Status:** Accepted（2026-06 评审改版，取代初版「框架=AOT、业务=热更」固定分界；2026-06-12 Windows IL2CPP 端到端验证通过：改入口版本 → 只重打代码包 → 玩家包增量下载 2 文件后新版本生效）

## Context

项目需要 C# 热更新能力，已安装 HybridCLR（UPM 包 `com.code-philosophy.hybridclr`，v8.x，官方稳定支持 6000.x）。HybridCLR 的热更**最小粒度是程序集**：热更 DLL 从 AOT 编译剔除，运行时 `Assembly.Load` 字节流交解释器执行。

初版设计把框架整体钉死在 AOT、只热更业务。评审后目标修正为：**尽量多的代码可热更，且热更范围按版本可配置**——框架既可热更（灵活档）也可退回 AOT（性能档），不把这个取舍焊死在架构里。

## Decision

### 1. 机制与策略分离：列表驱动

- **机制**（框架提供，程序集无关）：一个**热更程序集列表**作为单一真源，谁进列表谁热更，框架代码不硬编码任何程序集名。
- **策略**（项目按版本决定）：默认档位见 §2 表格；改档位 = 改列表 + 重出包，业务代码零改动。

单一真源派生到三处，不做第二份人工维护：

```
构建配置里的热更程序集列表（单一真源）
  ├─ Editor 同步 → HybridCLRSettings（不手工双维护）
  ├─ 构建管线   → CompileDll 后按列表拷热更 DLL
  │              + AOTGenericReferences 自动生成的补元数据 DLL 清单
  │              → 写出 hotupdate-manifest.json → 同进专用 RawFile 包（CodePackage）
  └─ 运行时     → Boot 只读随资源下发的 manifest（列表本身可热更：
                  发版后可新增热更程序集，无需发新包）
```

### 2. 程序集分层（AOT/热更档位）

| 程序集 | 归属 | 原因 |
|---|---|---|
| `Game.Framework.Boot`（新·薄引导） | AOT，永远 | 引导自举（鸡生蛋）；越薄越好，目标是几乎永不修改 |
| UniTask、YooAsset | AOT，必须 | Boot 引用它们，AOT 不能引用热更 |
| `Game.Framework`（内核） | 默认热更，**可退回 AOT** | Context/DI/Command/Event/层基类/Bag/RP/Pool + 资源系统后端无关部分；性能敏感版本移出列表跑原生 |
| `Game.Framework.Asset.Yoo`（自内核抽出） | 默认热更 | YooAsset 接触面（Provider 及注册胶水）。把 ADR-0013 的「YooAsset 收口在 Provider」从口头纪律升格为 asmdef 编译期强制；适配层是集成 bug 高发区（见 `docs/yooasset-pitfalls.md`），可热修价值高 |
| R3、Odin | AOT（默认） | Odin 预编译 DLL 没得选；R3 有 `RuntimeInitializeOnLoad`/PlayerLoop 入位问题且更新频率极低，热更红利≈0。机制不禁止（满足 §3 准则即可进列表） |
| 业务程序集、纯业务玩法库 | 热更 | 主战场；先单一热更 asmdef，多包拆分等真实业务出现再说 |

内核**不再细拆**（纯 C# 核心 vs Mono 适配、Pool 独立等）：拆开后它们在任何现实配置里同进同退，多一条边界只多管理成本。这条缝留给 UPM 抽包（[0010](0010-framework-reusability-upm.md)）时再议。未来模块（网络/存储/UI 框架/Luban 运行时）落地时各占一个 `Game.Framework.X` asmdef，粒度随真实模块自然生长。

### 3. 引用纪律（构建期校验兜底）

铁律：**AOT 不能引用热更** ⇒ 谁被热更，引用它的全部程序集必须跟着热更（热更集合对「被引用关系」向上封闭）。

- Boot 只引用 YooAsset + UniTask，**永不引用框架任何部分**。
- 内核永不引用模块（`Game.Framework.Asset.Yoo` 等）；接口在内核、实现在模块（ports & adapters）。
- 模块之间默认互不引用。
- 三方库进热更列表的判定准则：**引导链用不到它 && 没有任何 AOT 程序集引用它**。
- **热更程序集一律 `autoReferenced:false`**：否则 Assembly-CSharp（散落脚本 / 工具生成的无 asmdef 代码，
  如 HybridCLR 生成的 `AOTGenericReferences.cs`）会隐式自动引用它们，构成「AOT→热更」违规——散落脚本用了
  热更类型在真机是运行时谜案，关掉隐式引用让它变成编译期显式决策（业务代码必须住 asmdef，本就是项目纪律；
  这收紧了 [0004](0004-assembly-structure-and-rp-location.md) 「业务在 Assembly-CSharp 仍可用」的旧承诺）。
- 构建管线沿 asmdef 引用图**校验列表合法性**（存在 AOT→热更引用即报错并指出元凶）；DLL 加载顺序由引用图**拓扑排序自动生成**进 manifest，无人工排序规则。
- 构建管线记录 Generate 环境 stamp（Unity / HybridCLR Package 与本地 Runtime / 平台 / Development / 热更列表 / UPM 包锁、NuGet 清单与 HybridCLRSettings 哈希 / AOT PlayerSettings 指纹），
  代码包构建只消费与当前环境完全一致的生成物；热更列表非空时，AOT 清单缺失、格式异常、意外为空或裁剪 DLL 缺失一律失败，不把错误推迟到真机。

### 4. 引导流程

```
Boot 场景（唯一随包场景：Launcher + 朴素进度 UI，只挂 Boot 程序集的脚本）
  → 初始化 CodePackage（专用 RawFile 包，归 Boot 管）
  → 下载 manifest + DLL → 逐个 RuntimeApi.LoadMetadataForAOTAssembly(SuperSet)
  → 按拓扑序 Assembly.Load 热更 DLL
  → 反射调入口（默认约定 HotUpdateEntry.Enter()，类型全名 Inspector 可配）
入口（已在热更世界）
  → 创建 MonoGlobalContext → 框架资源系统照常初始化资源包（DefaultPackage 等）
  → 从 bundle 加载真正的首场景
```

- 代码包与资源包**彻底分家**：CodePackage 归 Boot，资源包归框架 `AssetInitSystem`，互不知晓，无「包被初始化两次」纠缠。
- **编辑器/非 IL2CPP 旁路用运行时判断，不用 define**：程序集已在 AppDomain，直接反射入口——单一代码路径，开发体验零变化。
- DLL 防明文：YooAsset 加密钩子（`IBundleMemoryDecryptor`）已在 Provider 接口面预留，需要时启用。

### 5. 硬边界与代价（知情决策）

- **随包场景不得挂热更程序集的脚本**：框架热更时连 `MonoGlobalContext` 都不能进随包场景；业务场景/prefab 一律 bundle 化（热更游戏标准形态）。Demo 场景只服务编辑器教学、不进包，不受影响。
- **性能**：热更代码走解释器（比 AOT 慢约一个数量级）。框架热更档位下 DI/事件/Command 分发全解释执行——当前项目可接受；性能敏感产品把内核移出列表。远期商业版 DHE（方法级差分）可两全，机制无需改动。
- 热更↔AOT 边界调用有桥接开销，但最低档位（仅业务热更）本来就跨该边界，分层不引入新量级。
- **完全不做代码热更也是一档**：热更列表为空 ⇒ 全程 AOT，Launcher 退化为 AppDomain 直连旁路，或干脆省掉 Boot 用 `MonoGlobalContext` 老式启动（业务场景也不必 bundle 化）；资源热更（YooAsset）独立可用。落地见框架手册 §15「不做代码热更怎么搭」。

### 6. 反射兼容（已验证，2026-06-12）

框架的 [InjectionPlan](../../Assets/Game/Framework/Core/Internal/InjectionPlan.cs) / [LayerInterfacesCache](../../Assets/Game/Framework/Core/Internal/LayerInterfacesCache.cs) / `GameContext.FindContextField` 对热更类型有效（都是真实 `System.Type`，解释器下元数据齐全）。AOT 泛型补元数据由 `AOTGenericReferences` 扫描自动覆盖。

**IL2CPP 真机自检通过（GameEntry 自检 8/8，Windows player）**：DI 容器注册/解析、`RP<T>` + R3 订阅（跨 AOT 泛型）、struct Command 分发、双泛型 `ExecuteCommand<TCmd,TResult>` 零装箱返回值、class Command `[Inject]` 注入、事件总线、UniTask 异步命令（解释器 async 状态机）、Odin `SerializationUtility` 对热更类型的序列化往返（反射 formatter）。

**迭代边界（实测）**：上述自检是在 v2→v3 **只重打代码包**（不重跑 Generate、不重出安装包）的前提下通过的——热更代码新增跨 AOT 泛型用法（Odin 泛型、R3 订阅泛型、命令双泛型等新实例化）由 SuperSet 补元数据 + 解释器兜底覆盖。需要重跑 Generate（并重出安装包）的仍是 AOT 集合本身的变化：增删第三方库 / 调整热更列表档位 / 升级 Unity 或 HybridCLR。Odin `SerializedMonoBehaviour` 挂场景资产反序列化热更类型未单测（当前形态业务场景全 bundle 化、入口后才加载，等真实业务场景接入时一并验证）。

## Consequences

- ✅ 热更范围成为一行配置而非架构定论：业务 / 业务+模块 / 业务+模块+内核 三档按版本选。
- ✅ 复用资源系统分发 DLL（RawFile 包），不引入第二套下载通道；manifest 让热更列表自身可热更。
- ✅ Boot 独立薄程序集（直接裸用 YooAsset+UniTask），框架一行不拆，实现成本低于初版预估。
- ✅ `Game.Framework.Asset.Yoo` 抽出顺带把 ADR-0013 的隔离纪律变成编译期强制。
- ⚠️ 抽取 Provider 需把「谁来 new YooAssetProvider」反转为注册/工厂（内核不得引用模块）。
- ⚠️ 构建管线新增职责：CompileDll、补元数据清单、manifest 生成、RawFile 包构建、引用图校验。
- ✅ Odin × 热更类型、解释器下泛型桥接已通过 Windows IL2CPP 真机自检（见 §6；`SerializedMonoBehaviour` 场景资产形态留待真实业务场景接入时补验）。

## 已决事项（初版开放决策的落定）

- 业务热更 asmdef 粒度：先单一 `Game.Main`（入口编排 + 未拆分业务），按需再拆模块/DLC。
- **目录与程序集按领域命名（Main / 模块 / DLC），不按「是否热更」命名**——热更与否是热更 profile 里的
  部署决策（按版本可变），不是代码的内在属性；一个领域单元 = 一个 asmdef = 热更列表一行 =（DLC 时）一个资源 package。
- Demo 不参与热更（编辑器教学定位，asmdef 用 `defineConstraints:["UNITY_EDITOR"]` 排除出玩家包——
  **不能用 `includePlatforms:["Editor"]`**：编辑器平台程序集的 MonoBehaviour 挂在场景上进 Play 模式会被剔成 missing，DemoScene 直接报废；define 约束在编辑器域恒满足、Play 正常，仅出包时不编译）。
- 热更入口：Launcher Inspector 显式配置程序集限定类型；该类型提供公共静态无参入口方法（默认方法名 `Enter`），作为游戏的 main。
- 边玩边下/版本灰度：本期不做；YooAsset 按需下载原语已具备，需要时组合。
- **入口的启动编排落地（2026-07，Outpost M5 驱动，详见 ADR-0029）**：`GameEntry.Enter` 从「挂自检」模板换成真实编排——
  代码搭最小引导资源栈（`MonoGameContextBase` + `AssetUtility` 双 AddComponent → `Configure`(为此提升 public) →
  `Initialize` → `LoadScene` 首场景 → Destroy 交棒），编辑器旁路走 EditorSimulate、玩家包走 Host。
  首个真实业务程序集 `Game.Outpost` 入热更列表（9 个）；`Game.Outpost.Sim` 刻意留 AOT（M6 ECS 后端只依赖它）——
  「热更程序集引用 AOT 程序集」方向合法，Generate 的 link.xml 保住仅被热更侧引用的 AOT 类型不被裁剪。
