# ADR-0002：Command 接收 `ICommandContext` 而非 `GameContext`

**Status:** Accepted

## Context

Command 的 `Execute` 需要访问层（`GetSystem`/`GetModel`/`SendEvent`/调用子 Command）。但若直接把完整 `GameContext` / `IGameContext` 传进去，Command 就能 `RegisterModel`、改 `Container`、写 `GameContext.Main`——这些都不是命令应有的副作用，会绕过框架约束。

struct Command 又不能用 `this.GetXxx<T>()` 扩展方法（值类型接口调用必然装箱，且无 `IHasGameContext`）。

## Decision

定义**受限上下文接口 `ICommandContext`**，只暴露 Command 合法的能力：`GetModel/GetSystem/GetUtility`、`SendEvent`、`CancellationToken`、`ExecuteCommand`（子命令）。`GameContext` 实现它；`CommandSystem` 把 `GameContext` 以 `ICommandContext` 传入。

所有 Command 接口的方法签名统一为 `Execute(ICommandContext ctx)` / `ExecuteAsync(ICommandContext ctx, CancellationToken)`。

## Consequences

- ✅ Command 内拿不到 Container/Register*/Main，越权即编译不出。
- ✅ struct/class Command 统一通过 `ctx` 参数访问层，零装箱零分配（struct 必须如此）。
- ⚠️ 文档/示例必须写 `ICommandContext ctx`——曾出现文档写成 `GameContext ctx` 的漂移（会让人/AI 写出无法实现接口的代码）。已统一修正，并以 Demo 为准绳。
- `ICommandSystem.ExecuteCommand(command, GameContext ctx)` 仍收 `GameContext`（它是框架内部命令分发器，需要完整上下文），不要与 Command 的 `Execute` 混淆。

**2026-08-30 命名澄清：**`ICommandSystem` / `CommandSystem` 的 `System` 是早期公共类型名，不表示五层业务 `ISystem`。命令分发器是 Context 基础设施 Seam：使用 `RegisterValue(..., typeof(ICommandSystem))` 精确注册，不使用 `RegisterSystem`。`LoggingCommandSystem` 已证明该 Interface 的替换价值，因此不删除 Seam；同时不引入 `ICommandDispatcher` 双契约别名——精确类型 DI 下两种 key 可能指向不同实例。若未来破坏性版本改名，应一次性迁移 Interface、Implementation、装饰器与注册 key。

**2026-08-31 异步完成边界：**Command 的异步实现允许临时切到 worker 处理纯数据，但 `ICommandSystem` 返回的 UniTask 必须在 Unity 主线程交付成功、异常或取消。默认 `CommandSystem` 以 `finally` 切回主线程，既覆盖全部终态，又保留原始异常对象与堆栈；`LoggingCommandSystem` 在落无锁环形缓冲前再次兜底，因为可替换的 inner 可能来自项目代码。这个保证让调用方 await 后可直接继续访问主线程独占的 Context / Event / Model，不把线程恢复责任扩散到每个业务 Command。

**2026-08-31 View 生命周期覆盖：**View 命令入口始终包含 Context owner；Mono 销毁令牌只是无参调用的界面侧默认值。调用方显式传入可取消 token 时，该 token 替代 Mono 销毁默认值，而不是追加成第三个取消源；`CancellationToken.None/default` 不构成覆盖。这样窗口发起但已经提交给更长寿命 owner 的保存、上传等工作可在原 View 销毁后继续，仍受 Context 最终收口；纯 C# View 则用 Bag / host token 显式表达自己的界面生命周期。该“替代”语义不符合 token 通常只追加的直觉，因此由 API XML、行为测试和教程共同锁定。
