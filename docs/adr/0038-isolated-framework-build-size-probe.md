# ADR-0038：Framework Module 隔离构建体积探针

**Status:** Accepted（2026-08-25）

## Context

Framework Module Audit 已能从 Player 编译图和 DLL 元数据证明 Core、UGUI、Toolkit 等组合的真实托管闭包，但原始 DLL 字节不是最终玩家包大小。链接裁剪、IL2CPP AOT、引擎模块和平台压缩都可能改变结论，尤其是 WebGL / 小游戏这类强体积约束目标。

直接在主工程里给不同组合生成一份临时 `link.xml` 看似简单，实际证据会失真：

- 未选 Module 的目录与其 `link.xml` 仍在 `Assets`，可能继续强制保留 UIElements、Protobuf、YooAsset 等程序集；
- `Assets/HybridCLRGenerate/link.xml` 代表当前整款游戏的 AOT 补元数据，不代表 Core-only；
- Boot / 业务场景、Resources、渲染管线和项目级 PlayerSettings 构成固定背景，难以区分 Framework Module 增量；
- 临时改 HybridCLR 列表、Build Settings 或场景会污染人工开发状态，崩溃时还可能来不及恢复。

因此“调用 `BuildPipeline.BuildPlayer`”本身是浅 Implementation；真正困难的是让每一档输入可比、保证主工程不被改动，并把结果口径说清楚。

## Decision

### 1. 用隔离空工程执行真实删除测试

菜单 `SSFramework/诊断/真实构建体积证据` 在主工程 `Library/SSFramework/BuildSizeProbe/` 下创建一次性 Unity 工程。每个组合构建前只复制 Framework Module Audit 结构化结果中该档闭包包含的 Runtime Module；`Editor` / `Test(s)` 目录不复制，未选 Module 的源码、资产和 `link.xml` 物理上不存在。

组合及 Module 源目录继续来自 Framework Module Audit，同一份审计结果同时驱动窗口、原始报告和真实构建，不维护第二份“Core / UGUI / Toolkit”清单。实际 DLL 闭包回答当前用了什么；探针再合并 asmdef 声明闭包，保证“只声明、尚未产生 IL 引用”的 Framework Module 仍被复制并完整保留，因为隔离工程编译时已经需要该程序集。这使 Module 变化集中在一个地方，保持 locality。

Module 源码不假设位于 `Assets/Game/Framework`。审计先经 `FrameworkModuleSourceCatalog` 把 `Assets/...`、`Packages/...` 或 PackageCache 绝对路径还原为“稳定 Asset 身份 + 真实物理目录 + package id”，探针从物理目录复制，并在 JSON / Markdown 证据中只保留可分享的资产来源与实际复制内容指纹。运行目录、输出、结果与日志路径不进入 JSON，而由本机 EditorPrefs 指向最新运行目录，并在读取时按稳定 Profile key 重建。复制到隔离工程时以程序集名作为目标目录，避免不同 Package 都使用 `Runtime/` 叶目录而互相覆盖；若两个 asmdef 源目录相同或互相嵌套则 fail-fast，因为目录复制无法诚实表达删除组合。Domain Reload 恢复会逐一校验报告格式、档位、package 身份与内容指纹；旧格式缺证据、未来格式含未知字段时都拒绝续跑，漂移时完成已启动档位后停止。旧工具也拒绝重写未来格式，避免保留版本号却丢失未知字段。详见 ADR-0040。

### 2. 依赖版本来自当前工程，但按组合最小化

隔离工程不再维护“Module 名 → Package 名”的第二张映射表。每个组合从所选 Framework asmdef 的声明边与当前 Player DLL 元数据边收集直接外部依赖，再由 `FrameworkModuleSourceCatalog` 判定它属于 registry / Git / local / tarball / embedded / built-in / Assets 中的哪种来源：

- registry Package 只按稳定 Package 名进入该组合的最小 manifest，版本规格直接复用主工程 `Packages/manifest.json`；主工程已有的 `scopedRegistries` 原样保留，不为某个供应商写死 registry；整轮启动时即冻结各档 manifest 与 SHA-256，后续档位不会因主工程中途改版本而混入另一套依赖；
- Git、embedded、local directory 与 local tarball Package 都从 Source Catalog 的已解析源码根按 Package 整体复制；这既冻结 Git branch / tag 已解析到的实际内容，也避免主工程相对 `file:` spec 搬到隔离工程后改指向。报告只写可移植的“包名@版本”与实际复制内容 SHA-256，Unity packageId 中的本机 `file:` 路径、Git URL userinfo 或 token 不落盘，同一轮多个组合共享一次指纹计算；
- `com.unity.modules.*` 仍作为同 Unity 版本的固定引擎背景统一保留；没有可安装来源的 BCL / Unity 平台程序集不被误当 Package；
- Framework 若实际接触项目 `Assets` 中的外部程序集，或显式依赖无法还原来源，探针在启动子进程前 fail-fast，不把业务代码 / DLL 静默夹进框架体积证据。

外部 Package 自身的 registry / built-in 传递依赖继续由它的 `package.json` 解析，探针不把主工程 `packages-lock.json` 中的偶然传递版本提升为根依赖。若复制 Package 的 `package.json` 仍含相对工作区的 `file:` 传递依赖，探针 fail-fast，不修改第三方 manifest 或猜目标目录；维护者应先把该依赖改成 registry 版本或独立 embedded Package。Framework 原生基线不依赖 Odin，探针也不复制付费插件。每个组合因此只携带它直接需要的 Package，例如 Core 不安装 Input System / UGUI，Toolkit 不因另一档需要 UGUI 而被污染。

当前 `Packages/nuget-packages` 是一个聚合 embedded Package；只要组合需要其中任一预编译 DLL，探针就复制整个物理 Package。这能证明“当前可安装边界下的真实组合体积”，不能证明各 NuGet DLL 已达到最小安装闭包。若以后要独立拆卸某个 NuGet 依赖，应先把它拆成独立 Package / Adapter seam，再由同一依赖计划自然得到更细的证据，不在探针里按 DLL 名伪造虚拟 Package。

探针不联网选择“更新版本”，也不修改第三方库。若主工程缺少选中 Module 声明的依赖，构建前 fail-fast，而不是默默换一个版本继续。

### 3. 所选程序集完整保留，结果定义为体积上界

隔离工程生成只包含所选 Framework 程序集的 `link.xml`，统一使用 `preserve="all"`。这样结果回答的是：

> 在当前 Unity / 平台 / 脚本后端 / stripping / 依赖版本下，完整带上这组 Framework Module 的玩家构建体积上界是多少？

它不回答“某个游戏只调用三个方法后的最小增量”。后一问题取决于业务使用面，不能由框架仓库替所有项目猜测。完整保留牺牲绝对值精度，换取组合之间可重复、可解释的比较；实际游戏通常只会更小。

### 4. 当前目标平台原样构建，子进程顺序执行

探针读取主工程当前 BuildTarget、ScriptingBackend、ManagedStrippingLevel 与 Development 选择，但不自动切换平台。要测 WebGL，维护者先按正常流程切到 WebGL，再启动探针。

构建由隐藏的 Unity 子进程顺序执行，共用同一个隔离工程 Library 以复用导入与 IL2CPP 缓存。每档结束后主进程才替换 Module 目录并启动下一档，避免两个进程同时写同一工程。停止操作采用“当前完成后停止”，不强杀正在落盘的 Player Build。

当前档位、Profile key、子进程 PID 与待运行队列持续写入 `report.json`；运行目录由本机 EditorPrefs 保存，输出 / 结果 / 日志路径在读取时按 Profile key 重建，不进入分享 JSON。主 Unity 发生 Domain Reload 或重启后会重新附着仍在运行的 Unity 子进程；子进程已退出则从独立结果文件恢复，再继续下一档。恢复找不到进程和结果时明确标失败，不用空数据假装成功。

### 5. JSON、Markdown、日志和产物共同构成证据

每轮保留：

- `report.json`：机器可读环境、状态、可发布输出、BuildReport 总量、耗时和最大发布文件；
- `report.md`：相对 Core 的差值、证据口径与解释；
- `Logs/<profile>.log`：子 Unity 完整日志；
- `Output/<profile>/`：玩家构建产物。

输出留在 `Library`，不把与机器、平台和依赖缓存强相关的数字提交成跨环境“金线”。需要 CI 回归时，应在固定 Runner 上保存 artifact，再定义同环境阈值。

Unity Windows IL2CPP 会把 `*_BackUpThisFolder_ButDontShipItWithYourGame` 的 C++ 中间文件和 PDB 计入 `BuildReport.totalSize`。探针保留这个原始总量用于诊断，但默认比较的“可发布输出”会排除 BackUp / DoNotShip / dSYM 目录以及独立调试符号；否则一个空壳玩家会被误报成数百 MiB。

### 6. 首轮真实验证只作为实现证据，不作为跨平台基线

2026-08-25 在 Unity 6000.3.22f1、StandaloneWindows64、IL2CPP、Stripping Minimal、非 Development 环境完成三档矩阵：

| 组合 | 可发布输出 | BuildReport 原始总量 | 相对 Core | 用时 |
|---|---:|---:|---:|---:|
| Core | 80.04 MiB | 573.36 MiB | 0 B | 101.1s |
| Core + UGUI | 99.80 MiB | 1.00 GiB | +19.75 MiB | 142.4s |
| Core + UI Toolkit | 101.90 MiB | 1.08 GiB | +21.86 MiB | 147.5s |

三档均为 0 error / 0 warning。原始总量与可发布输出相差数百 MiB，实际证明了“非发布构建证据必须单独列出”的必要性；输出增长主要落在 `GameAssembly.dll` 与 `global-metadata.dat`，说明此环境下值得继续观察 UI / Input System / 响应式泛型，但不足以直接证明应拆新 Module。矩阵运行中主动触发主工程 Asset Refresh / Domain Reload，主 Unity 能按落盘 PID 自动重新附着并继续后续档位。

这些数字只验证探针的输入隔离、结果口径与恢复机制。它们不是仓库金线，也不能回答 WebGL / 小游戏的增量；相关结构决策仍须切到真实发布目标后重跑。

## Consequences

- ✅ 未选 Module 真正消失，主工程的 HybridCLR / link.xml / 业务场景不再伪造 Core-only 结果。
- ✅ 构建事务收口在窄的 Editor seam 后，隐藏复制、依赖最小化、子进程、失败回传、差值和清理口径，对调用者有较高 leverage；当前没有第二实现，不为形式完整额外制造 Interface。
- ✅ 主工程不切平台、不改场景、不改 Build Settings，人工与 AI 自动化都能安全重复运行。
- ✅ 主工程 Domain Reload / 重启不会让仍在工作的子构建失去 owner；进度能从落盘状态恢复。
- ✅ JSON + Markdown 同时服务 CI、issue 与人工阅读；窗口只负责选择与反馈，Implementation 保持 locality。
- ✅ 源码位于 Assets、嵌入式 Package 或 registry/Git PackageCache 时复用同一探针 Implementation；报告能追溯每个 Module 的 package 版本。
- ✅ Module 与 Package 依赖计划共用 asmdef / 当前 DLL / Source Catalog 证据；新增可选 Module 不必再同步修改体积探针中的名称映射。
- ⚠ 第一次创建隔离工程要重新导入包，IL2CPP / WebGL 多档构建可能耗时较长并占用 `Library` 磁盘。
- ⚠ `preserve="all"` 是体积上界，不应把 Windows 结果外推为 WebGL，也不应宣称等于具体游戏最终包体。
- ⚠ 当前只证明代码 Module 与引擎/第三方依赖；业务资源包、字体字集、shader variants 与 HybridCLR CodePackage 仍应由真实产品构建单独度量。
- ⚠ 聚合 embedded Package 是当前最小物理复制边界；其中未使用的 DLL 是否能被 linker 裁剪由真实构建回答，探针不把它包装成可单独卸载的模块。

## Alternatives considered

- **在主工程临时写 link.xml**：拒绝。未选目录、HybridCLR 生成物和业务资产仍在，删除测试不成立。
- **临时改 HybridCLR / asmdef / Build Settings，构建后恢复**：拒绝。会触发主工程重编译，崩溃时可能留下半恢复状态，不利于 AI 自动化。
- **只比较原始 DLL**：保留为快速候选筛选，但不能替代 Player BuildReport。
- **为每档维护永久样例工程**：拒绝。Unity / package 版本和 Module 清单会复制四份，维护成本高且容易漂移。

## Related

- ADR-0004（程序集结构）
- ADR-0008（HybridCLR 程序集档位）
- ADR-0010（UPM 抽包路线）
- ADR-0027（列表绑定 Module 粒度）
- `docs/framework-module-map.md`
