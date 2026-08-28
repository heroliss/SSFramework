# Unity MCP 工具调用要点

本项目用 **ab-unity-mcp**（Unity Plugin **2.39.5** + 本地 Server **2.35.6**，工具前缀 `unity_*`）。
Plugin 与 Server 是两个独立仓库、版本号不要求相同；升级时按各自 release 配套检查，项目 `manifest.json` 固定 Plugin tag，
Codex 的 MCP 配置指向 `D:/unity-mcp-server/src/index.js`。下面是调用时容易踩的点，出错前先看。截图见 [.agents/skills/unity-screenshot/SKILL.md](../.agents/skills/unity-screenshot/SKILL.md)；`.claude` 只保留同名路由入口。

## 1. 实例与端口

首次调用前先选实例：`unity_list_instances` → `unity_select_instance port:<n>`，之后每个 `unity_*` 调用都带 `port:<n>`。**端口不固定**——Unity 重启 / 有时域重载后会变（见过 7891↔7892），`selectedPort` 可能指向已死端口。断连重连后**先重新 list + select**。

## 2. `execute_code` 是方法体

- 不能写 `using` / 顶层别名；类型全限定（`UnityEngine.UI.Image`、`UnityEditor.SceneManagement.EditorSceneManager`）。
- 扩展方法写成静态调用：UI Toolkit 的 `Q` → `UQueryExtensions.Q<...>(root, ...)`；LINQ → `System.Linq.Enumerable.XXX(...)`。
- **别同步等异步**（`.Result` / `.Wait()` / `.GetAwaiter().GetResult()`）——会冻住编辑器。只做"一次动作 / 读一份快照"，延迟检查拆成多次调用。

## 3. 重编译会断连

改 `.cs` 后 `AssetDatabase.Refresh()`（或编辑器自发重编译）进域重载，期间 MCP 可能超时 / 断连——正常，不是失败，别重试同一个写操作。重连后按 §1 重新 select。进 Play / 截图 / 读状态不触发编译，不会断。

### EditorWindow 截图与操作边界

`unity_screenshot_editor_window` 可按标题或类型截取 Inspector、Console 和框架自定义 EditorWindow；它使用
Win32 `PrintWindow`，即使窗口被遮挡也可观察且不抢焦点。检查诊断/配置窗口时优先用它，截图后仍必须实际打开
PNG 复查，不能只看工具返回 success。Game/Scene 继续分别用 `unity_screenshot_game` / `unity_screenshot_scene`。

EditorWindow 截图不等于通用交互：可表达的菜单、查询、滚动与尺寸调整优先用 `unity_execute_menu_item` / `unity_execute_code`；
只有 MCP 无法表达的原生控件点击、拖动或系统弹窗才转 Windows 界面控制。

## 4. 编译结果与建脚本

- 编译结果用 `unity_get_compilation_errors`（基于 CompilationPipeline，不受 console 清空 / Play 刷屏影响；`severity: error|warning|all`），不要靠读 console。
- 框架菜单的普通完成、失败、缺配置和 PlayMode 拦截统一输出一条 `[SSFramework.Tool][INFO|SUCCESS|WARNING|FAILURE]` Console 记录，并在当前 Editor 窗口短暂提示；它们不会再打开阻塞主线程的“结果弹窗”。自动化应读取稳定状态标记和完整详情：`INFO` 表示没有副作用且无需修复（也用于用户主动取消），`WARNING` 表示本次没有按理想路径完成或需要留意，`FAILURE` 才是失败。
- 仍会出现的弹窗必须代表真实选择，而不是结果通知：目前包括清理增量缓存的“全量重建”、在 Play 中打开其他场景前是否停止运行，以及 Unity 原生的脏场景保存选择。AI 不应盲点确认；先根据操作语义决定是否有权限继续。
- 新建脚本用 `unity_script_create`（走 Unity 脚本 API，导入可靠），别用文件工具 Write 后手动刷新。

## 5. 跑测试用 advanced_tool 的 testing 工具

`Game.Framework.Test` 是 PlayMode 程序集。经 `unity_advanced_tool` 的 testing 类工具跑：
`unity_testing_run_tests`（params：`mode: "PlayMode"` + `assemblies` / `testNames` / `groupNames`）→ 返回 jobId → `unity_testing_get_job` 查结果。注意参数名是 `mode` 不是 `testMode`，程序集过滤名是 `assemblies` 不是 `assemblyNames`；传错字段可能被忽略，造成错跑或范围失真。

定向运行前，先用 `unity_testing_list_tests` 传入相同 `mode` 与 `nameFilter`，确认目标 fixture 确实存在于该模式，再把类名交给
`groupNames`，或把清单返回的完整用例名交给 `testNames`。当前项目安装的 AnkleBreaker Unity 端只读取
`testNames/categories/assemblies/groupNames`；MCP schema 虽暴露 `filter` 便利别名，Unity 端并未解析它，可能静默退化成全量运行，
因此不要使用。任务终态 `succeeded + total=0` 只能说明 Runner 没找到用例，不能算验证通过；它通常意味着 mode 或筛选器写错。

### PlayMode 无弹窗预检（必须先做）

交互式 Editor 有脏场景时，Test Runner 在进入 PlayMode 前会打开原生保存弹窗。该弹窗阻塞 Unity 主线程，连 MCP 工具发现与队列查询都可能一起卡住；**弹窗出现后不能指望 Unity MCP 点击它**，只能人工或经操作系统 UI 自动化处理。

因此 MCP 启动每次 PlayMode 测试前，先调用：

```text
unity_execute_menu_item
menuPath: SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）
```

项目侧 `FrameworkAutomationPreflight` 会保存所有“已加载 + 脏 + 已有资产路径”的场景，并打印稳定标记 `[SSFramework.Automation] READY`；若 Editor 正忙、仍在 PlayMode、存在未命名脏场景或保存失败，则打印 `BLOCKED` 并 fail-fast，**不会打开新弹窗，也不会丢弃改动**。只有看到菜单调用成功且 Editor 编译空闲后，才调用 `unity_testing_run_tests`。

这不是全局自动保存 Hook：人工点击 Play / Test Runner 仍保留 Unity 原有确认语义，只有自动化显式选择预检才会落盘。若弹窗已经出现，先人工点 Save，再从预检重新开始；不要重复提交已经排队的测试命令。

## 6. 改场景必须先退出 Play 模式

场景结构改动（增删节点 / 加组件 / 改属性）前先确认编辑器**不在 Play 模式**（`EditorApplication.isPlayingOrWillChangePlaymode`）。Play 下的场景修改是运行时状态——**停止运行即全部回滚**，工具返回 success 也是白做；且 Play 下 GameObject 路径解析可能异常（见过 `component_add` 报 "GameObject not found"）。在 Play 就先停掉再动手，改完 `unity_scene_save` 落盘。

## 7. 长耗时操作优先用 Job / 队列轮询

Server 2.35.6 对队列 ticket 的瞬时断连会继续轮询，不再盲目重发已经执行的非幂等操作；默认 bridge / queue poll 超时为 60 / 120 秒。
这是相对旧服务端的重要可靠性修复，但分钟级构建仍可能超过 MCP 调用窗口：工具侧超时不等于 Unity 侧失败，先查 job、Editor.log 或目标产物，不能直接重跑。

- 测试等已有 Job API 的操作：启动后拿 `jobId`，用 `get_job(waitTimeout: ...)` 服务端等待。
- `Generate/All`、Player 构建等同步长操作：优先用专用构建工具；需要 `execute_code` 时仍在 `Temp/` 写操作锁和结果文件，使调用可恢复、可判定。
- 不用 `EditorApplication.delayCall` 调度关键长操作：编辑器失焦节流或域重载可能让回调迟迟不执行，甚至被吞掉。
- 超时后先确认 Unity 是否还在构建（Editor.log / 输出目录 / `BuildPipeline.isBuildingPlayer`），只有明确失败或已经结束且无产物才重试。
- `unity_component_set_property` 不支持数组/泛型属性（报 "Cannot set property type: Generic"），数组字段改用 `execute_code` + `SerializedObject`。

## 8. 新版工具发现与低 token 用法

- Server 端启用 `UNITY_MCP_COMPACT_TOOLS=1`：保留全部工具与参数结构，只裁掉注册表里重复的长说明；具体参数按需查 `unity_list_advanced_tools`。
- 发现高级工具优先用 `search` / `category` / `tool` 缩小结果，再让 `includeSchemas:true` 回显单个 schema，避免一次载入整份目录。
- `unity_scene_get_hierarchy` 默认是稠密输出（缺失字段代表默认值）；只有诊断序列化细节时才传 `verbose:true`。
- 资产创建/覆盖必须显式 `overwrite:true`，删除前先查清精确对象；可撤销操作用 `unity_undo_last` / `unity_undo_history`，不要假设每个工具都自动落成独立 Undo。

## 9. Test Runner 可在后台运行，不把 `editor_unfocused` 当阻塞

当前工程的 Player Settings 已启用 Run In Background；更重要的是 Unity Test Framework 1.6 会在 EditMode launcher 与
PlayMode `PreparePlayModeRunTask` 内临时设置 `Application.runInBackground = true`，结束后恢复原值。因此经 Test Runner
启动的测试不需要 Unity 一直处于前台，也不必每轮再用 `execute_code` 重复设置。单个 fixture 只有在脱离标准 Runner 也必须自洽，
或它明确验证后台/焦点语义时，才需要自己保存和恢复该值。

Test Framework 还会在运行期间把 Editor Interaction Mode 临时切到 `NoThrottling`，避免后台空闲节流拖慢任务，结束后恢复。
不应把该设置永久改成 `NoThrottling`：它不会解决原生模态框或真实输入焦点问题，只会让空闲 Editor 持续占用更多 CPU 与功耗。

AnkleBreaker Unity MCP 2.39.5 的 `blockedReason: editor_unfocused` 由 job 序列化层在
`InternalEditorUtility.isApplicationActive == false` 时直接附加，不参与 `TestRunnerApi.Execute` 或任务状态机；名称容易让人误判。
`total=0` 也可能只是测试发现、编译或域重载尚未回调 `RunStarted`。固定做法是保存 job id，用 30–60 秒 server-side wait
继续轮询同一个 job；只要 total/completed/currentTest、Console 里程碑或 Editor 状态在变化，就保持后台，不抢焦点、不清 job、
不重复启动。2026-08-26 实测在从未激活 Unity 的情况下，带该字段的 EditMode 16/16 与 PlayMode 14/14 都正常完成。
这里的 `total=0` 只允许出现在运行中的启动阶段；任务已经结束仍为 0 时，必须按第 5 节检查 mode 与筛选器，不能报告“测试通过”。

连续约 120 秒没有任何进度时，再依次检查编译、域重载、当前测试耗时、Console、保存弹窗与场景状态。只有这些证据都不能解释
停顿，才把“临时激活 Unity 一次”作为诊断实验，并继续观察原 job；它不是默认前置条件。普通手动 Play 不经过 Test Runner 时，
后台 PlayerLoop 仍取决于项目设置/运行时值，不应由通用框架为所有消费项目静默改写产品行为。

`Run In Background` 只保证继续推进，不保证“几十毫秒内一定获得某一帧”。测试若用 `Delay(0.05s)` 后断言对象仍处于
中间态，Editor 后台调度稍粗就可能在断言恢复前让后一个计时也到期。所有权、超时竞态等核心契约优先注入手动 clock / delay
Seam，由测试显式完成旧、新 owner；真实音频设备等焦点敏感集成另行验证。不要为了救脆弱墙钟断言把 Unity 前台焦点变成整库前置条件。

用 Additive 场景隔离用户现场时，也不要清空启动场景的全部根节点：Unity Test Framework 的 `Code-based tests runner` 本身就是根节点，销毁后业务帧仍会走，但测试协程再也不会恢复。只撤项目自己的 Composition Root（如 `MonoGameContextBase`），并在 TearDown 卸载测试加载的场景。

动态 TextCore / TMP 字体会在 Editor 测试中把新 glyph 与 atlas 缓存延迟写回源资产，单个 fixture TearDown 后清理仍可能被迟到写回覆盖。PlayMode Runner 还会先加载当前场景再进入筛选后的 fixture，所以即使只跑 Framework 测试，也可能触发 DemoScene 字体写回；PlayMode TestRun 守卫必须覆盖整轮、不能按测试类名前缀过滤。守卫会快照两份动态字体，整轮回到稳定 EditMode 后恢复，并同时确认磁盘原字节与该 asset path 下**全部** Unity Object（FontAsset 主对象、材质、atlas 纹理等子资产）的 dirty flag；任一对象标脏都先清标记并强制同步重导入，让内存态也回到快照。只 ClearDirty(main asset) 不够：脏子资产仍会让下一次 `Assets/Refresh` 保存整份 `.asset`，连带把 main asset 的 glyph table 写回。捕获前若已经有 dirty 对象则 fail-fast：磁盘字节只能保留“已保存但尚未提交 Git”的调整，不能安全恢复仍在内存中的用户编辑。Domain Reload / Editor 重启也能从 `Library` 续恢复。新增会触发动态字体的测试时沿用同一事务边界，不要在结束后笼统调用 `ClearFontAssetData`，它可能误删资产原有的 feature / atlas 基线。

## 10. 反射断言生成代码/第三方类型时注意成员形态

`execute_code` 动态编译不是稳定的项目自动化入口。当前验证过的 MCP 2.39.5 + Unity 6000.3.22f1 组合会在用户代码执行前报 `System.Object / mscorlib` 缺失；重试、切焦点或改反射写法都无效。优先把重复流程做成可测试的项目菜单 / Editor API，再用 `unity_execute_menu_item` 调用。只有最小代码探针已证明该工具可编译时，才用 `AppDomain.CurrentDomain.GetAssemblies()` 反射拿项目 asmdef 或 YooAsset.Editor 类型；Luban 生成 bean 的成员是 **readonly 字段**（`GetField`），元组返回值取 `Item1/Item2` 字段。

## 11. 改完代码立刻 Play 验证：防「Play 中域重载」毁掉验证现场

改 .cs 后进 Play，若编译/重载恰好迟到发生在 Play 期间（MCP 桥重启日志是信号），**非序列化运行时状态全被清空、`Start()` 不会重跑**，而 `[SerializeField]` 字段保留重载前的值——现场呈现自相矛盾的状态（实测：配置 Model `State=Ready` 但 `Tables=null`，像 bug 其实是现场被毁）。验证流程固定为：改完代码 → `AssetDatabase.Refresh` → 确认 `unity_get_compilation_errors` 的 `isCompiling=false` 且 0 错 → 再进 Play；Play 中看到 MCP 桥重启日志就别采信本轮结果，停掉重进。

## 12. Module 体积矩阵走隔离构建探针

Core / UGUI / Toolkit 的真实体积比较不要在主工程临时改 `link.xml`、HybridCLR 清单或 Build Settings。人工组合用 `SSFramework/诊断与分析/真实构建体积`；AI / CI 的常用回归直接执行无窗口菜单 `SSFramework/诊断/AI 自动化/常用档位隔离构建（Core + UGUI + Toolkit）`，只验证内核时用相邻的 Core 菜单。它们都在 `Library/SSFramework/BuildSizeProbe/` 创建无 MCP 插件的最小 Unity 子工程，物理删除未选 Module 后顺序构建。

- 先正常切到想测的 BuildTarget；探针不会替 Agent 静默切平台。
- 启动后轮询最近一轮 `report.json` 或窗口状态；不要因为单次 MCP 超时重复点击构建。
- 子 Unity 没有 ab-unity-mcp，不会抢主工程端口；主工程 Domain Reload 后 MCP 端口仍可能改变，按 §1 重新 list + select。
- Profile key、队列状态、子进程 PID 和“当前完成后停止”的具体原因会写进可分享报告；本机运行根由 EditorPrefs 保存，结果 / 日志 / 输出路径按运行根与 key 重建，不把机器绝对路径写进 JSON。主 Unity 重载 / 重启后据此重新附着子进程，或从已完成的独立结果继续下一档；人工停止与自动证据漂移不会在重载后失效或互相改写。即使重载时 manifest / Package 正在写入、暂时无法重建拓扑，也会把它当作漂移并优先附着已启动 child；PID 已退出但结果已落盘时先消费该结果再停止，不应看到报告永久停在“构建中”或把已完成档误判失败。
- 每档会同步重建隔离子工程的 `Library` / `Temp` / `obj`，复制目录也刻意不与 asmdef 同名；Profile 切换时主 Editor 可能短暂无响应，这是用缓存速度换独立删除证据，不要重复点击、跨档复用派生状态或把目录改回程序集名。若清理在目标机器上长期达到分钟级，再考虑每档独立 workspace，不先引入第二套调度。
- 探针启动后不要修改 Framework Runtime 源码或需复制的本地 / Git Package；每档复制前后都会重算冻结指纹，检测到写入会让当前档失败并停止剩余矩阵。子进程模板在运行目录的 `Inputs/` 中冻结；已编译 Editor DLL、主探针源码或模板变化会改变自动证据实现指纹，切档与 Domain Reload 恢复都会拒绝新旧逻辑混写。报告版本仍用于有意改变序列化证据契约或字段语义，不能替代自动内容指纹。
- 子工程会在 Player Build 前核对期望 Framework 程序集是否出现在编译图且拥有源码，再由目标平台 BuildPipeline 成功证明真实编译；不使用可能仍指向 Editor DLL 的预构建 `outputPath`。出现“缺少期望 Module / 没有源码 / 拒绝空壳 Player”说明输入或 Unity 导入异常，不应降级成 warning。
- 停止用“当前完成后停止”，不要从任务管理器强杀正在落盘的 Player Build。
- 默认比较“可发布输出”，不是 Unity 把 BackUp / DoNotShip / PDB 也算进去的 BuildReport 总量。Windows 结果不能外推成 WebGL。

完整证据口径见 ADR-0038；具体游戏的业务资源、HybridCLR CodePackage 和 shader / 字体仍看正式产品构建。

## 13. Unity 界面观察与操作的选择顺序

Unity 自动化优先使用语义接口，不把 Editor 当成只能按坐标点击的黑盒：

1. 优先调用 `unity_*` 的场景、组件、资产、选择、菜单、测试和 Console 工具；这些操作不依赖窗口焦点，也比模拟鼠标可验证。
2. 工具没有现成入口但 Unity API 能表达时，用 `unity_execute_code` 读取或操作一次明确状态；UI Toolkit 页面可查询元素、切页、滚动或临时调整布局后再截图。
3. 视觉证据按对象选择 `unity_graphics_game_capture` / `unity_screenshot_game`、Scene View 截图或 `unity_screenshot_editor_window`。指定 EditorWindow 的截图可在窗口被遮挡时工作，但不等于能点击窗口内任意坐标。
4. 只有 Windows 原生模态框、文件选择器、临时右键/下拉弹层、真实焦点/拖拽行为，或 MCP 已被模态框阻塞时，才使用操作系统 UI 自动化；若只是普通 Unity 状态，不因 Editor 在后台就切换到桌面控制。

需要真实前台的剩余范围很小：原生保存/文件/凭据/崩溃窗口，验证物理键鼠、Game View 输入焦点、拖拽与 Docking，或捕获
Tooltip、上下文菜单等临时弹层。场景、组件、资产、菜单、Console、测试、构建和指定 EditorWindow 截图均不应因此常驻前台。
Input System 的“PlayerLoop 在后台继续”与“键鼠设备失焦后是否仍投递”也是两个问题；后台逻辑测试优先用程序化输入，只有产品
确实要求真实焦点行为时才做前台端到端验证。

边界：Unity MCP 不是通用鼠标/键盘代理，不能承诺捕获或操作任意屏幕矩形、Tooltip、上下文菜单和系统子窗口。尤其原生保存框已经出现时，Unity 主线程与 MCP 队列可能一起被阻塞；先人工或经系统 UI 自动化关闭，再回到 §5 的预检流程，不要重复提交 Unity 命令。若只需一次 OS 激活，窄命令行 Adapter 最终仍调用 Windows 前台 API；它可以避免坐标点击，但不能做到“切焦点却完全不打扰用户”。Game/Scene/指定 EditorWindow 都能由 MCP 捕获时，截图与定位操作应留在 Unity 工具链内。完整后台判定流程见 Project Skill `unity-background-automation`。

## 14. Unity CLI 负责工程外启动，不绕过当前 Editor 边界

Hub 3.21 安装的独立 `unity` CLI 适合查询 Editor / Module / Project，并在本工程 Editor 关闭后运行 headless test、build、run；
仓库的 `Tools/UnityAutomation.psm1` 已把它作为首选启动 Adapter，并在 CLI 不可用时回退直接 Editor。它不能绕过
`Temp/UnityLockfile`，不要因为 CLI 能找到版本就对正在交互式打开的同一工程启动第二实例。

安装 `com.unity.pipeline` 后，CLI 确实可以连接当前 Editor，执行场景、资产、测试、截图、PlayMode、菜单等内置命令，或由
`unity mcp` 暴露 MCP；这与当前第三方 `unity_*` 工具有较大重叠，但官方说明第三方 MCP 不受 Assistant MCP 迁移影响。
当前项目不安装实验性的 Pipeline，也不改变 §1–§13 的稳定流程。重复的项目语义仍先实现成可测试的 Editor API / 菜单；只有
需要第二个机器 Adapter 且删除测试成立时，才在独立 Editor-only Module 中加 `[CliCommand]` 薄适配。完整说明见
`docs/unity-cli-automation.md` 与 ADR-0044。

---
经验沉淀文档：踩到新坑、确认调用方式后追加一节即可。
