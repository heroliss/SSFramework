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
| [0006](0006-odin-dependency.md) | Odin 硬依赖现状与未来解耦 | Accepted |
| [0007](0007-custom-object-pool.md) | 自研对象池替代第三方库 | Accepted（MVP） |
| [0008](0008-hybridclr-integration.md) | HybridCLR 热更：列表驱动机制 + Boot/内核/模块程序集分层 | Accepted |
| [0009](0009-luban-integration.md) | Luban 配置表：构建期生成 + 运行期经资源系统加载 | Accepted |
| [0010](0010-framework-reusability-upm.md) | 框架复用边界与 UPM 抽包路线 | Accepted |
| [0011](0011-directory-organization.md) | 项目目录组织与第三方隔离 | Accepted |
| [0012](0012-yooasset-3-migration.md) | YooAsset 3.0 迁移：先用官方兼容层 | Superseded by 0013 |
| [0013](0013-yooasset-native-rewrite.md) | YooAsset 原生 3.0 重写：去兼容层 | Accepted |
| [0014](0014-realtime-simulation-ownership.md) | 实时仿真 / 逐帧逻辑归 System（Update / R3 EveryUpdate），不走 Command | Accepted |
| [0015](0015-odin-decoupling-assessment.md) | Odin 解耦的可行路径与改动面评估（精化 0006 方向） | Proposed · 最低优先级（长远可选） |
| [0016](0016-ui-framework.md) | UI 框架：渲染后端无关的窗口/层级调度 + UGUI/UIToolkit 双 adapter | Accepted |
| [0017](0017-dlc-code-hotupdate.md) | DLC 热更：单 CodePackage 承载代码 + 运行时按需加载 + 业务 RawFile 包统一构建 | Proposed · 待实现 |
| [0018](0018-asset-encryption.md) | 资源加密：偏移内置为默认 + 代码接入位承载自定义，不内置 AES | Accepted |
| [0019](0019-service-installer-codegen.md) | 服务注册代码生成：目录扫描生成显式安装器 + 构建期值绑定自动注入 | Accepted |
| [0020](0020-ui-essentials.md) | UI 刚需补齐：异步过渡 + Back 键 + 安全区 + Top 层常用件 | Accepted |
| [0021](0021-local-storage.md) | 本地存储（存档）：IStorageUtility + 原子写文件 provider + 可插拔序列化 | Accepted |
| [0022](0022-audio-service.md) | 音频服务：IAudioUtility 音乐单通道 + 池化音效 + 分组音量 | Accepted |
| [0023](0023-game-flow.md) | 游戏流程状态机：IGameFlow 显式 Flow + 每状态一个子 Context | Accepted |
| [0024](0024-localization.md) | 本地化：ILocalizationUtility 响应式 locale + 文本源接缝 + 组合既有原语 | Accepted |
| [0025](0025-font-fallback.md) | 字体策略：精简字集随包 + fallback 链 + OS 字体运行时兜底 | Proposed |
