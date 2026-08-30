# ADR-0046：资源运行时收敛为 AssetUtility 单入口

- **状态（Status）**：已采纳（Accepted）
- **日期（Date）**：2026-08-30

## 背景

原场景接线要求在同一 Context 中同时放置 `AssetSystemConfigModel`、`AssetInitSystem` 与 `AssetUtility`。这套结构形式上对应 Model / System / Utility 三层，实际却没有形成三个独立、可替换的深模块：

- 配置只描述 provider、资源包、CDN 与下载器如何启动，不是金币、库存或进度等业务状态，也没有独立业务消费者；把它注册成 Model 会错误暗示 View/System 可以把资源基础设施配置当业务数据读取。
- `AssetInitSystem` 只在 Awake 读取配置、调用 Utility、逐包循环，没有独立业务规则；真正的包状态机、并发 owner、provider 生命周期、加载与维护协调都已由 `AssetUtility` 持有。
- 三个组件必须同 Context、依赖执行顺序与 `[Inject]` 才能工作，Inspector 与 Demo 需要反复解释“为什么少一个就坏”，增加接线错误和教学负担。
- Boot 代码路径本来就只创建 `AssetUtility`，随后 `Configure → Initialize → LoadScene`；场景路径与代码路径存在两套装配心智。

五层是业务权限语言，不是要求每项基础设施都机械拆成五份。配置、生命周期与状态机只有一个真实变化原因时，保持 Locality 比形式对称更重要。

## 决策

1. `AssetUtility` 成为资源运行时唯一正式 Mono 入口：持有序列化的 `AssetRuntimeSettings`、provider 生命周期、包状态机、自动初始化批次以及加载/下载/维护 API。
2. `AssetRuntimeSettings` 是普通 `[Serializable]` 基础设施配置，不实现 `IModel` / `ISystem`，也不注册到 Container。业务继续只依赖 `IAssetUtility`；需要展示运行配置的教学/Editor 代码从具体 `AssetUtility.Settings` 读取。
3. 场景路径在 `AssetUtility.Awake` 应用配置并建立状态，在 `Start` 执行自动初始化。这样 `AddComponent<AssetUtility>()` 后立即调用 `Configure` 的代码引导拥有一个确定窗口；显式 `Configure` 会接管启动并抑制 Inspector 自动初始化。
4. 标记为自动初始化的包在 Awake 后即被 Utility 识别。若其它组件在 `AssetUtility.Start` 前调用 `EnsureInitialized`，首次调用可直接启动同一个包 owner；后续 Start 幂等加入，避免依赖兄弟 Start 顺序。
5. 保留 `AssetSystemConfigModel` 与 `AssetInitSystem` 作为 `[Obsolete]`、隐藏 Add Component 的旧场景兼容层：前者维持旧字段名并能深拷贝成新设置，后者只把旧设置交给 Utility 的同一批量初始化实现。兼容层不再是新设计扩展点。
6. 提供基于真实 GameObject 选择的 Editor 迁移操作：深拷贝配置到同节点 `AssetUtility`，再经 Undo 删除旧组件并标记场景为脏；缺少同节点 Utility 时 fail-fast，不跨 Context 猜 Utility。迁移器按显式 `_targetContext` 或最近父 Context 解析旧接线，先验证 Config 与 Utility 同作用域，且该作用域恰好各有一份 Config 和 Utility，再一次删除同一 Scene/Context 的全部 `AssetInitSystem`（包括兄弟节点）。跨 Scene、跨 Context 与无法判断 `GameContext.Main` 回退归属的无宿主接线等已识别风险均在任何写入前失败。Project 中的持久化 Prefab 不直接修改，必须进入 Prefab Mode；全局歧义扫描按 Main Stage / 当前 Prefab Stage 隔离，其它预览 Scene 不会误拦当前作用域的迁移。
7. 仓库内 DemoScene 与 OutpostGame 通过 Unity Editor 迁移为单组件入口。Demo、主指南、资源流程与加密文档统一使用 `AssetUtility.Settings` 术语。
8. 单入口也是唯一销毁事务 owner：宿主销毁先取消初始化 / 维护任务，再释放 Provider 并完结所有已发布状态流，最后从 Context 反注册。可替换 Provider 的 `Dispose` 异常只记录，不得截断后续阶段；销毁后的旧 Utility 引用查询状态必须 fail-fast，不能重新创建一份脱离 Context 的状态。

## 影响与取舍

### 收益

- 新场景从三个互相依赖的组件缩成一个可配置、可诊断入口，Hierarchy 与 Inspector 的完成定义更直观。
- 资源配置不再占用业务 Model 身份，初始化薄编排不再占用业务 System 身份；五层权限表达更诚实。
- 场景与 Boot 共用同一状态机和初始化实现，只在“配置来自 Inspector 还是代码”上有明确差异。
- 旧场景可先继续运行再逐步迁移，迁移复制的是深拷贝而不是对将删除组件的引用。

### 代价与边界

- `AssetUtility` 的职责比普通无状态 Utility 更重，但这些职责围绕同一个资源运行时生命周期共同变化；再拆只会重新制造跨组件时序。
- 代码引导必须在 `Start` 前调用 `Configure`。运行中热换 provider/运行配置仍不支持；注入和包 owner 已经持有快照，热换会造成语义分叉。
- 兼容组件会保留一段迁移期，Core 因此暂时仍含历史类型。新代码、场景和文档不得继续依赖它们；未来破坏性版本可在外部场景完成迁移后删除。
- `Settings` 对业务是只读配置视图，不是响应式业务状态；运行期包状态仍从 `IAssetUtility.InitState/GetInitState` 观察。

## 验证

- 架构测试锁定 `AssetRuntimeSettings` 不属于 Model/System，`AssetUtility` 仍属于 Utility。
- Editor 迁移测试覆盖全部字段深拷贝（含两端运行模式、包元素与 CDN 集合不共享引用）、CDN 规范化、包级策略、同一 Scene/Context 的兄弟 Init 删除，以及多 Config / 多 Utility / 跨 Context / 无宿主歧义 / 缺少同节点 Utility / 持久化 Prefab 时零副作用失败。
- 资源协调测试证明 `Configure` 在 Start 前抑制 Inspector 自动启动；YooAsset 加载测试只搭建 Context + AssetUtility 并走真实单入口自动初始化。
- 两个仓库场景迁移前后逐字段 diff 相同，且场景中不再存在旧配置/初始化组件。
- Unity 编译、相关 EditMode / PlayMode 与完整回归通过。
