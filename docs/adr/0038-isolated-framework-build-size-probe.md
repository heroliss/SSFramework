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

组合及 Module 源目录继续来自 Framework Module Audit，同一份审计结果同时驱动窗口、原始报告和真实构建，不维护第二份“Core / UGUI / Toolkit”清单。这使 Module 变化集中在一个地方，保持 locality。

### 2. 依赖版本来自当前工程，但按组合最小化

隔离工程复用当前 `Packages/manifest.json` 中的 R3、UniTask、Input System、UGUI / TMP、YooAsset 版本以及内置 Unity Module 版本；NuGet 嵌入包和 Odin 运行时 DLL 从当前工程复制。每个组合单独生成最小 manifest，例如 Core 不安装 Input System / UGUI，Toolkit 不因另一档需要 UGUI 而被污染。

探针不联网选择“更新版本”，也不修改第三方库。若主工程缺少选中 Module 声明的依赖，构建前 fail-fast，而不是默默换一个版本继续。

### 3. 所选程序集完整保留，结果定义为体积上界

隔离工程生成只包含所选 Framework 程序集的 `link.xml`，统一使用 `preserve="all"`。这样结果回答的是：

> 在当前 Unity / 平台 / 脚本后端 / stripping / 依赖版本下，完整带上这组 Framework Module 的玩家构建体积上界是多少？

它不回答“某个游戏只调用三个方法后的最小增量”。后一问题取决于业务使用面，不能由框架仓库替所有项目猜测。完整保留牺牲绝对值精度，换取组合之间可重复、可解释的比较；实际游戏通常只会更小。

### 4. 当前目标平台原样构建，子进程顺序执行

探针读取主工程当前 BuildTarget、ScriptingBackend、ManagedStrippingLevel 与 Development 选择，但不自动切换平台。要测 WebGL，维护者先按正常流程切到 WebGL，再启动探针。

构建由隐藏的 Unity 子进程顺序执行，共用同一个隔离工程 Library 以复用导入与 IL2CPP 缓存。每档结束后主进程才替换 Module 目录并启动下一档，避免两个进程同时写同一工程。停止操作采用“当前完成后停止”，不强杀正在落盘的 Player Build。

当前档位、子进程 PID、待运行队列和结果路径持续写入 `report.json`。主 Unity 发生 Domain Reload 或重启后会重新附着仍在运行的 Unity 子进程；子进程已退出则从独立结果文件恢复，再继续下一档。恢复找不到进程和结果时明确标失败，不用空数据假装成功。

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
- ⚠ 第一次创建隔离工程要重新导入包，IL2CPP / WebGL 多档构建可能耗时较长并占用 `Library` 磁盘。
- ⚠ `preserve="all"` 是体积上界，不应把 Windows 结果外推为 WebGL，也不应宣称等于具体游戏最终包体。
- ⚠ 当前只证明代码 Module 与引擎/第三方依赖；业务资源包、字体字集、shader variants 与 HybridCLR CodePackage 仍应由真实产品构建单独度量。

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
