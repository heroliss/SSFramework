# Unity 自动化与验证

这份说明只记录 Framework Package 与 Unity Editor 自动化相关的通用边界。MCP、CLI 或其他宿主是可替换的调用方，不是 Framework 的运行时依赖。

## 可选 MCP 接入与验收

MCP 适合作为消费工程的可选开发工具。安装由所选提供方的 Unity 插件、配套服务端和 AI 客户端连接配置组成；只把插件加入 Package Manager 不能证明整个连接已经可用。

- 使用官方来源，固定已验证的插件与服务端版本组合，记录所需本机环境；升级后重做相关验收。许可、署名和再分发条件按所选版本核对。
- 先连接一个提供方，按任务启用所需工具类别。通过返回的工程路径、Unity 版本和实例身份确认目标；同时打开多个 Editor 时仍须能确定操作对象。
- 在真实消费工程或其副本验证读取场景 / Console、创建并重新读取临时对象、撤销或清理、脚本编译后的连接恢复。临时测试对象只在明确用于验收的场景中创建。
- 按下面的预检运行测试，等到最终结果并读取报告；使用截图检查需要视觉验收的操作。超时、重编译或重连后先确认操作是否已完成，再决定是否重试。
- 检查 Player 编译与构建，确认只用于 Editor 的连接组件没有引入非预期的运行时依赖。记录通过的范围；“MCP 已连接”不能代替项目编译、测试或构建结果。

项目共享的开发说明记录版本和复现步骤，本机客户端配置按使用的宿主管理。MCP 附加的上下文入口指向项目当前文档，不重复注入整份历史方案。Framework 提供的预检 API 保持独立，MCP 更换时仍可通过其他受支持入口调用。

## 测试前置条件

启动 Unity EditMode 或 PlayMode Test Runner 前，先确认：

1. Editor 不在 Play、编译、刷新或 Player Build 状态；
2. 已加载的脏场景都有资产路径；
3. 没有未命名的脏场景或阻塞性的原生弹窗；
4. 通过 FrameworkAutomationPreflight.PreparePlayModeTests()，或菜单 SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）建立无弹窗前置条件。

预检会显式保存有路径的脏场景；遇到未命名场景、忙碌状态或保存失败会输出 BLOCKED 并停止。READY 只表示测试可以开始，不表示测试已经通过。名称保留了历史兼容性，EditMode 和 PlayMode 都使用同一入口。

## 运行与取证

- 测试、构建和长耗时 Editor 操作应保存 job、报告或产物路径，工具调用超时不等于 Unity 侧失败。
- 不要盲目重发非幂等写操作；先查看 Editor 状态、编译结果、报告和目标产物，再决定是否重试。
- FrameworkModuleAudit 用于解释程序集、外部依赖和保留根；它不替代 Unity Package Manager，也不把 autoReferenced:false 解释成自动裁剪。
- FrameworkBuildSizeProbe 的报告用于同一环境下比较 Module 组合和发现依赖异常；最终 Player 包体仍以目标平台真实构建为准。
- 测试范围、结果和截图应保留原始证据，并区分“命令已受理”“测试已完成”和“结果通过”。

## 修改边界

场景和 Prefab 只经 Unity Editor 或项目提供的 Editor API 修改，不手改 YAML。Framework 文档和源码修改可以在文件工具中完成；涉及 Unity 资产时，先检查 Editor 状态，修改后保存并重新读取验证。

相关设计见 [ADR-0036：测试预检](adr/0036-ai-playmode-preflight.md)、[ADR-0038：隔离体积探针](adr/0038-isolated-framework-build-size-probe.md) 和 [ADR-0043：Editor 菜单与工作台](adr/0043-editor-menu-navigation-workbenches.md)。
