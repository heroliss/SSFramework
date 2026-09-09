# Unity 自动化与验证

这份说明只记录 Framework Package 与 Unity Editor 自动化相关的通用边界。MCP、CLI 或其他宿主是可替换的调用方，不是 Framework 的运行时依赖。

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