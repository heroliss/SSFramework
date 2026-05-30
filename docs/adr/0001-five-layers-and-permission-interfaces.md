# ADR-0001：五层 MVCS + 编译期权限接口

**Status:** Accepted

## Context

传统 MVC 的 Controller 随项目增长会变成"既响应输入、又协调数据、又写业务逻辑"的垃圾桶。游戏项目还有大量"用户意图 → 状态变更 → 视图反映"的链路，需要可追溯、可测试、可替换实现。

## Decision

把 Controller 一分为二，形成五层：**View / Command / System / Model(+Event) / Utility**。

- View 管"看与发意图"，System 管"怎么做"，Command 管"做什么"（连接 View 与 System 的接缝），Model 持有状态，Utility 是无状态工具。
- 各层能力用**空标记接口在编译期约束**（`ICanGetModel` / `ICanGetSystem` / `ICanSendEvent` / `ICanRegisterEvent` / `ICanSendCommand` / `ICanGetUtility`）。`IView : ICanSendCommand, ICanRegisterEvent, ICanGetUtility`——View 编译期就不能 `GetModel`/`SendEvent`。
- 扩展方法（`this.GetModel<T>()` 等）以 `ICanXxx` 约束调用者，权限即类型系统的一部分，不靠口头约定。

## Consequences

- ✅ 单向数据流被编译器保证；越权调用编译失败而非运行期排查。
- ✅ View 与业务层通过 Command 解耦，便于测试（注册 Mock System）与替换实现。
- ⚠️ 多一层 Command 样板；但 struct Command 零分配、查询 Command 可直接返回只读订阅源，成本可控。
- 关联：[0002](0002-commands-receive-icommandcontext.md)（Command 为何拿受限上下文）。
