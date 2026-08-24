# Unity MCP 工具调用要点

本项目用 **ab-unity-mcp**（Unity Plugin **2.39.5** + 本地 Server **2.35.6**，工具前缀 `unity_*`）。
Plugin 与 Server 是两个独立仓库、版本号不要求相同；升级时按各自 release 配套检查，项目 `manifest.json` 固定 Plugin tag，
Codex 的 MCP 配置指向 `D:/unity-mcp-server/src/index.js`。下面是调用时容易踩的点，出错前先看。截图见 [.claude/skills/unity-screenshot/SKILL.md](../.claude/skills/unity-screenshot/SKILL.md)。

## 1. 实例与端口

首次调用前先选实例：`unity_list_instances` → `unity_select_instance port:<n>`，之后每个 `unity_*` 调用都带 `port:<n>`。**端口不固定**——Unity 重启 / 有时域重载后会变（见过 7891↔7892），`selectedPort` 可能指向已死端口。断连重连后**先重新 list + select**。

## 2. `execute_code` 是方法体

- 不能写 `using` / 顶层别名；类型全限定（`UnityEngine.UI.Image`、`UnityEditor.SceneManagement.EditorSceneManager`）。
- 扩展方法写成静态调用：UI Toolkit 的 `Q` → `UQueryExtensions.Q<...>(root, ...)`；LINQ → `System.Linq.Enumerable.XXX(...)`。
- **别同步等异步**（`.Result` / `.Wait()` / `.GetAwaiter().GetResult()`）——会冻住编辑器。只做"一次动作 / 读一份快照"，延迟检查拆成多次调用。

## 3. 重编译会断连

改 `.cs` 后 `AssetDatabase.Refresh()`（或编辑器自发重编译）进域重载，期间 MCP 可能超时 / 断连——正常，不是失败，别重试同一个写操作。重连后按 §1 重新 select。进 Play / 截图 / 读状态不触发编译，不会断。

## 4. 编译结果与建脚本

- 编译结果用 `unity_get_compilation_errors`（基于 CompilationPipeline，不受 console 清空 / Play 刷屏影响；`severity: error|warning|all`），不要靠读 console。
- 新建脚本用 `unity_script_create`（走 Unity 脚本 API，导入可靠），别用文件工具 Write 后手动刷新。

## 5. 跑测试用 advanced_tool 的 testing 工具

`Game.Framework.Test` 是 PlayMode 程序集。经 `unity_advanced_tool` 的 testing 类工具跑：
`unity_testing_run_tests`（params：`mode: "PlayMode"` + `assemblies` / `testNames` / `groupNames`）→ 返回 jobId → `unity_testing_get_job` 查结果。注意参数名是 `mode` 不是 `testMode`，程序集过滤名是 `assemblies` 不是 `assemblyNames`；传错字段可能被忽略，造成错跑或范围失真。

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

## 9. Play 验证先开 `Application.runInBackground`

AI 经 MCP 驱动 Play 验证时编辑器几乎必然**失焦**，默认设置下 Play 循环暂停（Update 不 tick）——异步初始化/加载会一直卡住，看起来像"加载死了"。进 Play 后先 `execute_code` 执行 `Application.runInBackground = true;` 再做断言。该值是运行时状态，不写工程设置，停止 Play 自动失效。

真实 PlayMode 测试不能只依赖 Agent 在外面“碰巧设到正确一帧”：Test Runner 可能切换域/Play 状态。需要持续 PlayerLoop 的测试应在自己的 `UnitySetUp` 保存旧值并设为 `true`，在 `UnityTearDown` 的 `finally` 恢复。MCP job 返回的 `blockedReason: editor_unfocused` 只是诊断提示；先同时看 `Time.frameCount`、当前里程碑与 Console，不要仅凭失焦把业务等待判成死锁。

用 Additive 场景隔离用户现场时，也不要清空启动场景的全部根节点：Unity Test Framework 的 `Code-based tests runner` 本身就是根节点，销毁后业务帧仍会走，但测试协程再也不会恢复。只撤项目自己的 Composition Root（如 `MonoGameContextBase`），并在 TearDown 卸载测试加载的场景。

## 10. 反射断言生成代码/第三方类型时注意成员形态

`execute_code` 动态编译只引用部分程序集，项目 asmdef（如 `Game.Framework.Build.Editor`）和 YooAsset.Editor 里的类型都要走 `AppDomain.CurrentDomain.GetAssemblies()` 反射拿。常见翻车点：Luban 生成 bean 的成员是 **readonly 字段**（`GetField`），不是属性（`GetProperty` 返回 null → NRE）；元组返回值取 `Item1/Item2` 字段。

## 11. 改完代码立刻 Play 验证：防「Play 中域重载」毁掉验证现场

改 .cs 后进 Play，若编译/重载恰好迟到发生在 Play 期间（MCP 桥重启日志是信号），**非序列化运行时状态全被清空、`Start()` 不会重跑**，而 `[SerializeField]` 字段保留重载前的值——现场呈现自相矛盾的状态（实测：配置 Model `State=Ready` 但 `Tables=null`，像 bug 其实是现场被毁）。验证流程固定为：改完代码 → `AssetDatabase.Refresh` → 确认 `unity_get_compilation_errors` 的 `isCompiling=false` 且 0 错 → 再进 Play；Play 中看到 MCP 桥重启日志就别采信本轮结果，停掉重进。

---
经验沉淀文档：踩到新坑、确认调用方式后追加一节即可。
