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
- `ICommandSystem.ExecuteCommand(command, GameContext ctx)` 仍收 `GameContext`（它是框架内部派发器，需要完整上下文），不要与 Command 的 `Execute` 混淆。
