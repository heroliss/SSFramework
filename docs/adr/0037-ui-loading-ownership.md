# ADR-0037：全局 Loading 所有权 —— 引用计数 lease + 陈旧句柄安全

**Status:** Accepted（2026-08-23）

## Context

ADR-0020 把 Loading 做成 `ShowLoading → await 业务任务 → HideLoading` 的全局单窗口开关，并补了打开过程的生命周期令牌。令牌解决了宿主销毁后窗口迟到出现的问题，却没有表达“谁正在使用这扇共享窗口”。

两个互不从属的异步流程重叠时会出现确定竞态：A、B 都显示 Loading，A 先结束并调用 `HideLoading()`，就会把 B 仍需要的 Loading 一起关闭。单纯记录“最近一次 Show 的 generation”只能阻止旧 A 关闭新 B；若 B 先结束而 A 仍在运行，最新 generation 仍会误关，因此它不适合作为通用的忙碌状态语义。

约束：

- 核心继续渲染中立，Toolkit / UGUI adapter 只透传；
- 多个调用者共享一个视觉窗口，不能每个任务各建一个 Loading；
- 成功、失败、取消和宿主销毁都应沿同一种释放路径；
- `CloseAll` / Context 销毁仍有强制清场能力；
- 旧源码的 `ShowLoading/HideLoading` 调用暂时保留。

## Decision

### 1. `AcquireLoading` 返回所有权句柄

推荐入口为：

```csharp
using var loading = await ui.AcquireLoading("正在连接…", ct);
await Connect(ct);
```

`AcquireLoading` 复用同一个内置 Loading 窗口并返回 `LoadingHandle : IDisposable`。每个有效句柄代表一次占用；任意句柄释放只移除自己的占用，最后一个有效句柄释放后才关闭窗口。`using` 把正常返回、异常与取消统一进编译器生成的 `finally`，调用方不再手写容易配错的 Show/Hide 对。

句柄可登记进 `DisposableBag`；默认句柄、重复 `Dispose`、UI 已销毁后的释放均为安全 no-op。

### 2. active id 集合同时承担引用计数与陈旧安全

核心为每次占用签发自增 id，并保存 active id 集合。集合大小就是引用计数，但不能退化成一个整数：`Close` / `CloseAll` / `Dispose` 会清空集合，清场前签发的旧句柄随后释放时查不到自己的 id，不能误减清场后新占用的计数，也不会关闭后来重新显示的 Loading。

异步创建完成后还会复查 id 是否仍 active。若创建途中已经发生 Hide / CloseAll，迟到调用拿不到有效句柄；当没有其它 owner 时，刚创建的窗口立即关闭，不留下“幽灵 Loading”。

### 3. 兼容 Show/Hide 被建模成一个 legacy owner

`ShowLoading/HideLoading` 暂不删除，重复 Show 仍是“刷新同一窗口”的单 owner 语义。核心把这对调用建模成一个布尔 owner，并用 generation 防止创建途中 Hide 后的迟到打开：

- `HideLoading` 只释放 legacy owner；还有 active handle 时窗口保持；
- handle 释放只释放自己的 lease；legacy owner 仍在时窗口保持；
- 直接 `Close`、`CloseAll`、`Dispose` 是强制清场，二者全部失效。

新代码必须优先 `AcquireLoading`。兼容入口只用于迁移旧的严格单 owner 流程，不应作为新并发流程的所有权模型。

### 4. 视觉文本采用 last writer wins

每次 Acquire / Show 都调用同一窗口的 `OnOpen(args)` 刷新提示文本；最近一次写入保留到下次刷新或窗口关闭。释放较新的 lease 不恢复较旧文案：恢复文案需要维护 owner 优先级和历史栈，而 Loading 的首要契约是“忙碌期间保持遮罩”，复杂任务进度应由专门的进度 Model / 窗口表达。

## Compatibility

- 既有 `ShowLoading/HideLoading` 调用源码继续工作，且与新 handle 混用时不会互相误关。
- `IUIUtility` 新增成员会要求自定义实现补 `AcquireLoading`；仓库内只有核心、Toolkit、UGUI 三个实现，均在同批更新。
- 同批还给 Toast / Loading 增加了可选 `CancellationToken` 参数：源码调用点兼容，但旧的预编译实现本就需要重新编译。选择在同一批完成 lease，避免公共 Interface 连续两次迁移。
- 自定义实现可实现 `ILoadingHandleOwner` 并签发 `LoadingHandle`；必须保持陈旧 id 安全与“最后 owner 才关闭”的契约。

## Consequences

- ✅ A、B 等并行业务互不越权；全局 Loading 准确覆盖所有 active owner。
- ✅ 所有权能进入 `using` / `DisposableBag`，成功、异常、取消和销毁共享一条释放路径。
- ✅ generation 只处理兼容开关的时序，id 集合处理真正的多 owner 与陈旧句柄，两种职责不混淆。
- ✅ 两个渲染 adapter 仍是薄转发，所有状态与测试集中在核心，保持 locality。
- ⚠ 调用方丢弃 `AcquireLoading` 返回值等价于泄漏一次占用；Demo、guide 与 XML doc 必须始终展示 `using var`。
- ⚠ 多 owner 共用一段文本且 last writer wins；需要可合并进度或任务列表时应自建业务窗口，而不是扩张内置 Loading。
