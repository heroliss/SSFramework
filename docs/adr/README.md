# 架构决策记录（ADR）

记录 SSFramework 的关键设计决策——**为什么这样设计**，供人类与 AI 追溯。改动既有决策时更新对应 ADR 的 Status 并补充新 ADR，不要静默推翻。

格式：每个 ADR 含 `Status`（Accepted / Proposed / Superseded）、`Context`（背景/问题）、`Decision`（决策）、`Consequences`（后果/权衡）。

| # | 决策 | Status |
|---|---|---|
| [0001](0001-five-layers-and-permission-interfaces.md) | 五层 MVCS + 编译期权限接口 | Accepted |
| [0002](0002-commands-receive-icommandcontext.md) | Command 接收 `ICommandContext`（受限）而非 `GameContext` | Accepted |
| [0003](0003-custom-di-container.md) | 自研精简 DI 容器 + 主线程独占契约 | Accepted |
| [0004](0004-assembly-structure-and-rp-location.md) | 程序集结构与 `RP<T>` 归位 | Accepted |
| [0005](0005-no-runtime-hot-swap-of-layers.md) | 运行时不热替换已注册层 | Accepted |
| [0006](0006-odin-dependency.md) | 第一阶段接受 Odin 硬依赖 | Superseded by 0015 |
| [0007](0007-custom-object-pool.md) | 自研对象池替代第三方库 | Accepted（MVP） |
| [0008](0008-hybridclr-integration.md) | HybridCLR 热更：列表驱动机制 + Boot/内核/模块程序集分层 | Accepted |
| [0009](0009-luban-integration.md) | Luban 配置表：构建期生成 + 运行期经资源系统加载 | Accepted |
| [0010](0010-framework-reusability-upm.md) | 框架复用边界与 UPM 抽包路线 | Accepted |
| [0011](0011-directory-organization.md) | 项目目录组织与第三方隔离 | Accepted |
| [0012](0012-yooasset-3-migration.md) | YooAsset 3.0 迁移：先用官方兼容层 | Superseded by 0013 |
| [0013](0013-yooasset-native-rewrite.md) | YooAsset 原生 3.0 重写：去兼容层 | Accepted |
| [0014](0014-realtime-simulation-ownership.md) | 实时仿真 / 逐帧逻辑归 System（Update / R3 EveryUpdate），不走 Command | Accepted |
| [0015](0015-odin-decoupling-assessment.md) | Unity 原生基线与 Odin 可选增强边界 | Accepted |
| [0016](0016-ui-framework.md) | UI 框架：渲染后端无关的窗口/层级调度 + UGUI/UIToolkit 双 adapter | Accepted |
| [0017](0017-dlc-code-hotupdate.md) | DLC 热更：单 CodePackage 承载代码 + 运行时按需加载 + 业务 RawFile 包统一构建 | Proposed · 待实现 |
| [0018](0018-asset-encryption.md) | 资源加密：偏移内置为默认 + 代码接入位承载自定义，不内置 AES | Accepted |
| [0019](0019-service-installer-codegen.md) | 服务注册代码生成：目录扫描生成显式安装器 + 构建期值绑定自动注入 | Accepted |
| [0020](0020-ui-essentials.md) | UI 刚需补齐：异步过渡 + Back 键 + 安全区 + Top 层常用件 | Accepted |
| [0021](0021-local-storage.md) | 本地存储（存档）：IStorageUtility + 原子写文件 provider + 可插拔序列化 | Accepted |
| [0022](0022-audio-service.md) | 音频服务：IAudioUtility 音乐单通道 + 池化音效 + 分组音量 | Accepted |
| [0023](0023-game-flow.md) | 游戏流程状态机：IGameFlow 显式 Flow + 每状态一个子 Context | Accepted |
| [0024](0024-localization.md) | 本地化：ILocalizationUtility 响应式 locale + 文本源接缝 + 组合既有原语 | Accepted |
| [0025](0025-font-fallback.md) | 字体策略：精简字集随包 + 主字体 fallback 链 + OS 字体运行时兜底 | Accepted |
| [0026](0026-framework-diagnostics-panel.md) | 框架诊断面板：Editor 采集层（Context 登记表 / 计数）+ 总览窗口 + LoggingCommandSystem | Accepted |
| [0027](0027-reactive-collections-list-binding.md) | 响应式集合：ObservableList + 后端中立增量列表绑定（`Bag.BindList`），补 RP 单值订阅的集合空缺 | Accepted |
| [0028](0028-network.md) | 网络：IHttpUtility 请求-响应 + IWebSocketUtility 推送转事件，传输/序列化双接缝 | Accepted |
| [0029](0029-outpost-vertical-slice.md) | 垂直切片 Outpost：13 模块整合验收 + 接缝发现清单 + 玩家包端到端 | Accepted |
| [0030](0030-outpost-ecs-battle-backend.md) | Outpost M6：DOTS 后端置换（EcsBattleSim）+ 两级对拍 + 跨编译域浮点边界 | Accepted |
| [0031](0031-outpost-real-projectiles-wreck-interaction.md) | Outpost M7：真弹道碰撞 + 残骸减速泥地（让后端优势在真实游玩中可见） | Accepted |
| [0032](0032-outpost-wreck-entities-sim-push.md) | Outpost M8：残骸实体化 + 推挤入模拟（让后端差距随战局拉大） | Accepted |
| [0033](0033-ugui-into-uitoolkit-rendertexture-bridge.md) | UI 嵌入桥：RenderTexture 把 UGUI / 相机内容真嵌进 UI Toolkit 内容流（+ v2 输入穿透） | Accepted |
| [0034](0034-framework-logging-seam.md) | 框架日志接缝：内核 `ILogSink` 多播 + Console/File sink + Unity 日志桥 | Accepted |
| [0035](0035-container-factory-ownership.md) | Container 工厂所有权：构造时机与生命周期正交，显式 `RegisterOwnedFactory` | Accepted |
| [0036](0036-ai-playmode-preflight.md) | AI PlayMode 预检：显式保存有路径脏场景，不以全局 Hook 劫持人工 Play | Accepted |
| [0037](0037-ui-loading-ownership.md) | 全局 Loading 所有权：引用计数 lease + 陈旧句柄安全 | Accepted |
| [0038](0038-isolated-framework-build-size-probe.md) | Framework Module 隔离构建体积探针：真实删除 + Player BuildReport 上界证据 | Accepted |
| [0039](0039-framework-module-retention-model.md) | Framework Module 选择与保留证据：五层正交状态 + 安全移除事务 + UPM 分工 | Accepted |
| [0040](0040-upm-aware-module-source-catalog.md) | UPM-aware Module Source Catalog：稳定 Asset 身份 + 真实物理源码 + Package 所有权 | Accepted |
| [0041](0041-module-dependency-integrity.md) | Module 依赖完整性：真实 asmdef/DLL 声明、HybridCLR 元数据拓扑新鲜度与 Adapter-local 默认装配 | Accepted |
| [0042](0042-external-dependency-evidence-catalog.md) | 第三方依赖证据目录：只读、来源可追溯、与 UPM 正交 | Accepted |
| [0043](0043-editor-menu-navigation-workbenches.md) | Editor 菜单只导航：副作用操作进入 Module 工作台，自动化与上下文入口显式例外 | Accepted |
| [0044](0044-unity-cli-external-automation-adapter.md) | Unity CLI 作为工程外启动 Adapter：ProjectVersion 单一真值、Direct 回退、Pipeline 延迟接入 | Accepted |
| [0045](0045-build-editor-module-split.md) | 资源构建与 HybridCLR 热更新构建拆分：单向依赖、RawFile 显式归属与删除测试 | Accepted |
| [0046](0046-asset-utility-single-runtime-entry.md) | 资源运行时单入口：配置、生命周期与自动初始化收敛到 AssetUtility，旧三组件可迁移 | Accepted |
