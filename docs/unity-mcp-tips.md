# Unity MCP 工具调用要点

本项目用 **ab-unity-mcp**（Unity 包 `Packages/com.anklebreaker.unity-mcp`，工具前缀 `unity_*`）。下面是调用时容易踩的点，出错前先看。截图见 [.claude/skills/unity-screenshot/SKILL.md](../.claude/skills/unity-screenshot/SKILL.md)。

## 1. 实例与端口

首次调用前先选实例：`unity_list_instances` → `unity_select_instance port:<n>`，之后每个 `unity_*` 调用都带 `port:<n>`。**端口不固定**——Unity 重启 / 有时域重载后会变（见过 7891↔7892），`selectedPort` 可能指向已死端口。断连重连后**先重新 list + select**。

## 2. `execute_code` 是方法体

- 不能写 `using` / 顶层别名；类型全限定（`UnityEngine.UI.Image`、`UnityEditor.SceneManagement.EditorSceneManager`）。
- 扩展方法写成静态调用：UI Toolkit 的 `Q` → `UQueryExtensions.Q<...>(root, ...)`；LINQ → `System.Linq.Enumerable.XXX(...)`。
- **别同步等异步**（`.Result` / `.Wait()` / `.GetAwaiter().GetResult()`）——会冻住编辑器。只做"一次动作 / 读一份快照"，延迟检查拆成多次调用。
- `Game.Framework.System` 命名空间会让嵌套的 `System.X` 解析到错处，用 `global::System.X` 兜底。

## 3. 重编译会断连

改 `.cs` 后 `AssetDatabase.Refresh()`（或编辑器自发重编译）进域重载，期间 MCP 可能超时 / 断连——正常，不是失败，别重试同一个写操作。重连后按 §1 重新 select。进 Play / 截图 / 读状态不触发编译，不会断。

## 4. 编译结果与建脚本

- 编译结果用 `unity_get_compilation_errors`（基于 CompilationPipeline，不受 console 清空 / Play 刷屏影响；`severity: error|warning|all`），不要靠读 console。
- 新建脚本用 `unity_script_create`（走 Unity 脚本 API，导入可靠），别用文件工具 Write 后手动刷新。

## 5. 跑测试用 advanced_tool 的 testing 工具

`Game.Framework.Test` 是 PlayMode 程序集。经 `unity_advanced_tool` 的 testing 类工具跑：
`unity_testing_run_tests`（params：`mode: "PlayMode"` + `assemblyNames` / `testNames` / `groupNames`）→ 返回 jobId → `unity_testing_get_job` 查结果。注意参数名是 `mode` 不是 `testMode`（后者会被忽略、错跑成 EditMode）。

## 6. 改场景必须先退出 Play 模式

场景结构改动（增删节点 / 加组件 / 改属性）前先确认编辑器**不在 Play 模式**（`EditorApplication.isPlayingOrWillChangePlaymode`）。Play 下的场景修改是运行时状态——**停止运行即全部回滚**，工具返回 success 也是白做；且 Play 下 GameObject 路径解析可能异常（见过 `component_add` 报 "GameObject not found"）。在 Play 就先停掉再动手，改完 `unity_scene_save` 落盘。

---
经验沉淀文档：踩到新坑、确认调用方式后追加一节即可。
