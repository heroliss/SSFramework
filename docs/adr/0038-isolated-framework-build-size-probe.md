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

菜单 `SSFramework/诊断与分析/真实构建体积` 在主工程 `Library/SSFramework/BuildSizeProbe/` 下创建一次性 Unity 工程。每个组合构建前只复制 Framework Module Audit 结构化结果中该档闭包包含的 Runtime Module；`Editor` / `Test(s)` 目录不复制，未选 Module 的源码、资产和 `link.xml` 物理上不存在。

组合及 Module 源目录继续来自 Framework Module Audit，同一份审计结果同时驱动窗口、原始报告和真实构建，不维护第二份“Core / UGUI / Toolkit”清单。实际 DLL 闭包回答当前用了什么；探针再合并 asmdef 声明闭包，保证“只声明、尚未产生 IL 引用”的 Framework Module 仍被复制并完整保留，因为隔离工程编译时已经需要该程序集。这使 Module 变化集中在一个地方，保持 locality。

Module 源码不假设位于 `Assets/Game/Framework`。审计先经 `FrameworkModuleSourceCatalog` 把 `Assets/...`、`Packages/...` 或 PackageCache 绝对路径还原为“稳定 Asset 身份 + 真实物理目录 + package id”，探针从物理目录复制，并在 JSON / Markdown 证据中只保留可分享的资产来源与实际复制内容指纹。运行目录、输出、结果与日志路径不进入 JSON，而由本机 EditorPrefs 指向最新运行目录，并在读取时按稳定 Profile key 重建。复制目标采用“源码职责叶目录 + 可读程序集名 + 稳定短哈希”（如 `Core__Game_Framework__<hash>`）：职责名让证据可读，程序集身份避免不同 Package 都使用 `Runtime/` 或 slug 恰好相同时互相覆盖，同时规避 Unity 6000.3 在目录与其中 asmdef 同名时可能把定义误交给 `DefaultImporter` 的导入歧义。若两个 asmdef 源目录相同或互相嵌套则 fail-fast，因为目录复制无法诚实表达删除组合。Domain Reload 恢复会逐一校验报告格式、证据实现、档位、package 身份与内容指纹；旧格式缺证据、未来格式含未知字段时都拒绝续跑，漂移时完成已启动档位后停止。旧工具也拒绝重写未来格式，避免保留版本号却丢失未知字段。详见 ADR-0040。

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

### 4. 当前目标平台原样构建，子进程顺序执行且每档重建派生状态

探针读取主工程当前 BuildTarget、ScriptingBackend、ManagedStrippingLevel 与 Development 选择，但不自动切换平台。要测 WebGL，维护者先按正常流程切到 WebGL，再启动探针。

构建由隐藏的 Unity 子进程顺序执行。每档结束后主进程才替换 Module / Package 输入并启动下一档，避免两个进程同时写同一工程；同时删除隔离子工程的 `Library` / `Temp` / `obj` 派生状态，让每个 Profile 重新导入和编译。递归删除前必须同时满足 `<RunsRoot>/<run>/Project` 精确结构与 ProjectVersion / child template 标记；只证明目标位于调用方传入目录内并不够，因为误把主项目根当 workspace 时同样会包含主 `Library`。输出与报告位于工程外侧的 run 目录，不受清理影响。启动时会把子进程模板写入 `<run>/Inputs/FrameworkBuildSizeProbeChild.cs.txt`，所有档位只从这份快照恢复子入口，不再回读主工程里的 live 模板；报告同时记录模板 SHA-256，以及“当前已编译 Editor DLL + 主探针源码 + 子模板”的联合证据实现 SHA-256。每档复制前会验证证据实现、重新计算 Runtime / Package 来源 SHA-256，复制后再计算目的内容；任一与启动计划不符都将当前档标失败并终止剩余队列，覆盖无 Domain Reload 时的并发写入窗口。Windows 下 run 目录会让深层 Package 文件超过传统 `MAX_PATH`，文件指纹 IO 使用 extended-length path，但报告仍保留普通可读相对路径。子入口在调用 `BuildPipeline` 前从 `CompilationPipeline` 核对这一档声明的全部 Framework 程序集与非空源码清单；目标平台 IL 是否真正成立由随后 `BuildPipeline` / BuildReport 成功负责。刻意不检查预构建 `outputPath`，因为 Unity 6000 在这里可能仍返回 Editor ScriptAssemblies，无法跨平台证明 Player DLL。停止操作采用“当前完成后停止”，不强杀正在落盘的 Player Build。

初版曾让多个 Profile 共用隔离工程 `Library` 以换取速度。2026-08-27 的常用档位回归暴露了两个独立风险：跨档派生状态会削弱物理删除证据；而把 Core 复制进与 `Game.Framework.asmdef` 同名的目录时，Unity 6000.3.22f1 曾把该定义按普通资产导入，导致 Core 档生成没有 Framework IL 的空壳 Player、UI 档随后因缺 Core 而编译失败。探针因此同时采用每档重建派生状态、非同名的确定性复制目录和 Player 编译图真值门禁。对体积证据而言，这些额外时间优先于缓存速度与“BuildPipeline 返回成功”的表面结果。

当前档位、Profile key、子进程 PID、待运行队列与“当前完成后停止”的具体原因持续写入 `report.json`；运行目录由本机 EditorPrefs 保存，输出 / 结果 / 日志路径在读取时按 Profile key 重建，不进入分享 JSON。主 Unity 发生 Domain Reload 或重启后会重新附着仍在运行的 Unity 子进程；子进程已退出则从独立结果文件恢复，再继续下一档。停止原因是报告状态而不是易失的静态布尔值：人工请求和自动证据漂移在重载后都会继续生效，最终跳过项也保留真实原因。若恢复时恰逢 manifest / Package / Source Catalog 正在变化，连当前拓扑都无法重建，该异常也被归为漂移：优先按落盘 PID 附着当前 child，完成后停止；PID 已退出但冻结输入产生的独立结果已落盘时，先消费该结果再停止后续档位；只有进程和结果都不存在时才明确失败并完成报告，不让子进程因主 Editor 的重建异常失去 owner，也不用空数据猜成功。

#### 窗口预览与执行证据分离（2026-08-29）

`FrameworkBuildSizeProbeWindow.CreateGUI` 只建立布局、读取已落盘结果和会话内只读快照；打开窗口不再隐式运行 Module Audit、遍历全部 asmdef 或为所有档位计算源码 / Package SHA-256。用户明确点击“读取可构建组合”时才刷新用于选择的审计预览，工程、Package、构建场景、目标平台或编译图变化后预览立即失效并给出提示。

预览缓存不是构建输入。点击构建或调用无窗口机器菜单时，动作 Implementation 必须重新采集当前审计证据，并且只为请求的 Profile 计算闭包、manifest 与内容指纹；不能为了方便复用可能陈旧的窗口对象，也不应先为未选组合做昂贵哈希。这样把“打开工作台”“理解并选择”“冻结真实执行证据”分成三个明确阶段，同时保留启动时 fail-fast 与整轮漂移检测。

### 5. JSON、Markdown、日志和产物共同构成证据

每轮保留：

- `report.json`：机器可读环境、状态、可发布输出、BuildReport 总量、耗时和最大发布文件；
- `report.md`：相对 Core 的差值、证据口径与解释；
- `Inputs/FrameworkBuildSizeProbeChild.cs.txt`：整轮共用且经过 SHA-256 验证的子进程实现快照；
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

2026-08-27 在相同 Unity / 平台 / 后端 / 裁剪条件下，用新增的 Player 编译图真值门禁复验 Core、UGUI、Toolkit：三档分别为 77.76 MiB、93.41 MiB、95.75 MiB 可发布输出，用时 281.5s、341.8s、398.8s，仍均为 0 error / 0 warning。UGUI 的最小 manifest 只有 `com.unity.ugui`，Toolkit 不安装 UGUI，两者都不再因 Demo 的物理返回键接线安装 Input System。该轮是最终 v8 之前的三档真值门禁回归，用于证明目录消歧、每档重建和期望程序集门禁生效；数值变化仍不升级为跨机器基线，不能把它表述成最终 v8 三档矩阵。

同日完成冻结/恢复边界修复与 37/37 针对性测试后，以最终 v8 契约复验 Core：可发布输出 77.76 MiB，BuildReport 530.60 MiB，用时 109.1s，0 error / 0 warning。报告的证据实现 SHA-256 为 `c13c4feb…d58e1`，与该次最终源码及已编译 Editor DLL 绑定；日志明确显示散列目录中的 `Game.Framework.asmdef` 由 `AssemblyDefinitionImporter` 导入，并生成 `Library/Bee/PlayerScriptAssemblies/Game.Framework.dll`。`report.json` / `report.md` 均含 64 字符的证据实现与模板快照指纹，run-owned `Inputs/` 也保留了实际模板。该复验关闭 v8 最终主链串联正确性，但不把单档结果包装成一次新的三档矩阵。

## Consequences

- ✅ 未选 Module 真正消失，主工程的 HybridCLR / link.xml / 业务场景不再伪造 Core-only 结果。
- ✅ 构建事务收口在窄的 Editor seam 后，隐藏复制、依赖最小化、子进程、失败回传、差值和清理口径，对调用者有较高 leverage；当前没有第二实现，不为形式完整额外制造 Interface。
- ✅ 主工程不切平台、不改场景、不改 Build Settings，人工与 AI 自动化都能安全重复运行。
- ✅ 主工程 Domain Reload / 重启不会让仍在工作的子构建失去 owner；进度能从落盘状态恢复。
- ✅ JSON + Markdown 同时服务 CI、issue 与人工阅读；窗口只负责选择与反馈，Implementation 保持 locality。
- ✅ 打开窗口不再触发全工程扫描或全矩阵哈希；昂贵工作都有明确按钮或机器命令，执行仍强制使用新鲜证据。
- ✅ 源码位于 Assets、嵌入式 Package 或 registry/Git PackageCache 时复用同一探针 Implementation；报告能追溯每个 Module 的 package 版本。
- ✅ Module 与 Package 依赖计划共用 asmdef / 当前 DLL / Source Catalog 证据；新增可选 Module 不必再同步修改体积探针中的名称映射。
- ✅ 固定自动化菜单要求所请求档位全部存在；物理删除某个 Module 后会明确失败并提示改选，不把残缺矩阵静默包装成 Core / UGUI / Toolkit 全部成功。
- ⚠ 每个档位都会重新导入包并建立编译状态；IL2CPP / WebGL 多档构建会更慢，但不会用上一档的 AssetDatabase / Bee 缓存换取失真的删除证据。
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
