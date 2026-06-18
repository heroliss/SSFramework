# ADR-0004：程序集结构与 `RP<T>` 归位

**Status:** Accepted

## Context

早期只有 `Game.Framework`（覆盖 `Scripts/`）与测试两个 asmdef。`RP<T>`（`SerializableReactiveProperty<T>` 的简短泛型包装）被放在 `Framework/Extensions/`——该目录不在任何 asmdef 下，落入 `Assembly-CSharp`。这把**所有业务代码也钉死在 Assembly-CSharp**：业务一旦想用自己的 asmdef（更快编译、清晰边界），就用不了 `RP<T>`。编辑器绘制器散落在运行时文件的 `#if UNITY_EDITOR` 块里。还残留已删除的 `uPools` 悬空引用。

## Decision

- `RP<T>` 移入 `Game.Framework` 运行时程序集（`Core/Reactive/RP.cs`）。业务在 Assembly-CSharp 仍可用（autoReferenced），将来独立 asmdef 引用框架后同样可用；框架内部也能用了。
- 新增 `Game.Framework.Editor`（`includePlatforms:["Editor"]`）收纳所有编辑器代码：`RPDrawer`、`AssetReferenceDrawer`、文件夹菜单。
- 新增 `Game.Framework.Demo` 程序集，作为"消费方如何引用框架"的活样板。
- 移除 `uPools` 悬空引用。
- `Game.Framework` 需引用 `R3.Unity`（`SerializableReactiveProperty` 所在的 Unity 集成程序集；核心 `R3.dll` 是其预编译引用）。

## Consequences

- ✅ 业务可自由采用独立 asmdef；运行时程序集不含编辑器代码。
- ✅ 为 HybridCLR 的 AOT/热更分界（[0008](0008-hybridclr-integration.md)）和 UPM 抽包（[0010](0010-framework-reusability-upm.md)）打好边界。
- ⚠️ `ROP` 别名（`IntROP` 等）被废弃：闭合泛型别名只能 per-assembly `using`、跨程序集失效，统一用 `ReadOnlyReactiveProperty<T>`。
- ⚠️ R3 的 `SerializableReactivePropertyDrawer` 是 `internal` 且未用 `useForChildren`，`RP<>` 无法继承复用，`RPDrawer` 保留一份与 R3 同步的副本。
