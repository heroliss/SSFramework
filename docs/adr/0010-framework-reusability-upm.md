# ADR-0010：框架复用边界与 UPM 抽包路线

**Status:** Accepted

## Context

目标是让 `Assets/Game/Framework` 成为**多项目可复用**的框架。最彻底的形态是 UPM 包（版本化、边界清晰、易分发）。但框架仍在活跃重构期，过早抽包会带来包内只读、样例/测试组织、依赖声明等摩擦，且现有 `AGENTS.md`/skill 路径都假设 `Assets/` 布局。

## Decision

**第一阶段保留在 `Assets/Game/Framework`**，但用 asmdef 边界 + 规则做成自洽模块；UPM 抽包列为路线图里程碑（框架稳定后再做）。

复用铁律（写入 `Assets/Game/Framework/AGENTS.md`）：
- `Game.Framework` / `Game.Framework.Editor` **禁止引用任何项目业务代码**；依赖只指向 asmdef references 声明的第三方/Unity 程序集。
- 新增 `Game.Framework.Demo` 程序集验证"消费方边界"——若框架不小心依赖了项目代码，Demo 会编译失败暴露问题。

## Consequences

- ✅ 现在就享受清晰边界与可独立编译，迁移成本低（asmdef 已就位，抽包时主要是移动目录 + 写 package.json）。
- ✅ 边界由编译强制，而非口头约定。
- ⏳ 抽包延后；届时补充后续 ADR 记录包形态（内嵌 `Packages/` vs 独立 git 包）与依赖声明。
- 关联：[0004](0004-assembly-structure-and-rp-location.md)、[0011](0011-directory-organization.md)。

**2026-08-25 准备度补充：**`Framework Module Audit` 现以 Player 编译图、DLL 真实引用、热更 Profile 与全部已安装源码中的 `link.xml` 生成 Core-only、单 UI 后端、完整及任意 Module 入口闭包，并分开解释项目消费者、热更传播和 linker 根。Editor 侧的 `FrameworkModuleSourceCatalog` 已消除 `Assets` 与 UPM `Packages` / `PackageCache` 的物理路径差异，审计、隔离构建探针与源码门禁不再依赖当前仓库根路径。它把“可抽包”从目录观感推进到可重复的删除计划与可迁移工具链；正式 UPM 分包粒度仍需 WebGL/小游戏等真实目标平台的 Player BuildReport 决定，详见 ADR-0039、0040。

**2026-08-25 依赖边界补充：**UPM 没有“依赖已安装就增强、未安装仍正常”的通用 optional dependency
声明，因此不能把所有 asmdef 机械塞进一个 package 再用开关假装可裁剪。建议发布粒度按真实变化原因收敛，而不是一程序集一包：

- `com.ssframework.core`：Core + 通用原生 Editor；依赖 UniTask / R3，不依赖 Odin；
- `com.ssframework.asset.yoo`：YooAsset Adapter；
- `com.ssframework.ui`：共享 UI + 所选后端，首版避免把紧密协作的每个 asmdef 都变成用户要管理的包；
- `com.ssframework.network.protobuf`、`com.ssframework.hotupdate.yoo-hybridclr`：重第三方组合 Adapter；
- Demo 作为独立样例工程或 Samples，不让教学依赖污染产品 package；
- Odin 只可作为用户另行安装的项目级工具。现有 `Game.Framework.Odin.Editor` 是不含插件本体、可整体删除的
  专属增强；正式抽包时可成为 `com.ssframework.odin.editor`，并保持对 Core 的单向 Editor 依赖。

当前 R3、UniTask、HybridCLR、Luban 等 Git 依赖和 `Packages/nuget-packages` 嵌入包仍是正式发布前的
工程化阻塞：package 自身不能假定复用主项目 manifest 中的 Git 地址，也不应让一个聚合 NuGet 包把未选库
一起带入。后续应为每个发布包建立干净消费者工程，明确 registry / Git 安装说明，并把聚合 NuGet 依赖按
真实 package closure 拆开。因而“迁移主要是移动目录 + 写 package.json”的旧估计已不再成立；源码定位已
准备好，但依赖发布、Samples、授权和干净安装验证仍是独立工作流。
