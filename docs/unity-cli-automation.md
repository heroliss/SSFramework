# Unity CLI 与项目自动化

> 适用版本：Unity CLI 1.0.0-beta.5、Unity Pipeline 0.5.0-exp.1（2026-08-28 调研基线）。两者仍是 Experimental；升级后先复核 `--help`、退出码和 Package API，再更新本文。

## 先说结论

Unity CLI 值得使用，但不应被理解成“新的 Unity MCP”或“所有 Unity 操作的唯一入口”。它最有价值的两个位置是：

1. **工程外自动化**：按 `ProjectVersion.txt` 找到精确 Editor，管理 Editor / Module / Project，并在关闭交互式 Editor 后执行 headless test、build、run；
2. **可选的当前 Editor Adapter**：安装 `com.unity.pipeline` 后，通过本机 HTTP Server 执行内置或项目自定义命令，也可由 `unity mcp` 暴露为 stdio MCP。

SSFramework 当前只落地第一层：`Tools/UnityAutomation.psm1` 统一 Editor 发现与启动，`Tools/run-tests.ps1` 和资源构建 workflow 共同复用。Pipeline 尚未加入 `Packages/manifest.json`，现有第三方 Unity MCP、PlayMode 预检和隔离构建探针均保持不变。

## 能力分工

| 场景 | 推荐 Interface | 原因 |
|---|---|---|
| Editor / Module 安装、版本查询、工程列表 | Unity CLI | 独立于项目进程，支持机器可读 JSON / TSV 与稳定退出码 |
| 关闭本工程 Editor 后跑全量测试或 CI 构建 | Unity CLI `test` / `build` / `run`，或项目 `Tools` Adapter | 能按 `ProjectVersion.txt` 选精确版本；适合无窗口流程 |
| 已打开 Editor 的场景、Prefab、组件、Console、测试 job、截图 | 当前 `unity_*` MCP | 已验证有队列、多实例、可轮询 job 与项目协作约束 |
| 重复的项目专属 Editor 操作 | 项目内静态 Implementation + 稳定菜单；未来可加 Pipeline Adapter | 业务语义只实现一次，菜单、MCP、CLI 只是入口 |
| 原生保存框、凭据框、文件选择器、真实键鼠/拖拽 | 窄 OS UI Adapter | Unity API、MCP、Pipeline 都可能被原生模态框一起阻塞 |

CLI 不绕过 Unity 的工程锁：同一工程已由交互式 Editor 打开时，不要再对它执行 headless `unity test/build/run`。当前 Editor 的 PlayMode 测试继续先走项目预检，再由 MCP 启动和轮询。

## 常用命令

```powershell
# CLI 与环境
unity --version
unity doctor --json
unity editors --installed --json
unity editors path 6000.3.22f1 --json
unity projects --json

# 关闭交互式 Editor 后的 headless 流程
unity test . --mode EditMode --output Logs/editmode.xml --non-interactive
unity test . --mode PlayMode --output Logs/playmode.xml --non-interactive
unity run . --non-interactive -- -executeMethod Namespace.Type.Method
unity build . --target WebGL --output-path Build/WebGL --non-interactive

# 当前 Editor 的 Pipeline（只有工程已安装 com.unity.pipeline 才可用）
unity pipeline list --json
unity status --project-path . --json
unity list --project-path . --json
unity command editor_status --project-path . --json
unity command screenshot --project-path . --json -- --view game
```

`unity run` 会把 `--` 后参数交给 Editor，但 `-batchmode`、`-quit` 与 `-projectPath` 由 CLI 自己拥有，不能重复透传；项目 Adapter 会在 CLI 路径移除这三项，在 Direct 路径原样保留。`unity command` 则把 `--` 后参数按已注册命令的 schema 解析，两者含义不同，不能混用。CI 加 `--non-interactive`，并以产物存在性做第二道门禁：退出码为 0 但没有 NUnit XML、BuildReport 或目标产物，仍应判基础设施失败。

## 项目当前实现

`Tools/UnityAutomation.psm1` 的选择规则是：

1. 显式 `-UnityPath` 或 `UNITY_EDITOR_PATH`：使用 Direct Adapter；Windows 核对文件 ProductVersion / revision，平台不暴露该元数据时至少要求路径含精确版本目录；
2. 否则在 `Auto` 模式下寻找 Unity CLI，用 `unity editors path <ProjectVersion>` 解析并核对精确 Editor，再由 `unity run` 同步启动；
3. CLI 不存在或未登记该版本：回退 Hub 默认目录、secondary install path 与 Windows Installer 注册表；
4. 通用 Editor 操作由 `Invoke-UnityEditor` 承载：CLI Adapter 会移除 `unity run` 自己拥有的 `-batchmode`、`-quit`、`-projectPath`；Direct Adapter 保留原始 Editor 参数并统一做 Windows command-line quoting；
5. 测试由独立的 `Invoke-UnityTests` 承载：CLI Adapter 映射到 `unity test`，Direct Adapter 映射到 `Unity.exe -runTests`，共同产出 NUnit XML；通用 `unity run` 即使收到 `-runTests` 也可能在测试前退出 0，因此项目 Module 会明确拒绝这种误用；
6. 不自动安装或升级 Editor / Module，不删除 `UnityLockfile`，也不在失败后偷偷换版本。

隔离的最小工程 smoke test 已验证 beta.5 在 Windows 上能精确选择 6000.3.22f1、同步等待 Editor 并返回 0；含 1 条 EditMode 测试的工程经 `unity test` 产生 NUnit XML（1/1 passed）。测试还证实重复透传 `-batchmode` 会被 CLI 以退出码 6 拒绝，而 `unity run -- -runTests` 会退出 0 但不产出 XML。调用者应复用项目 Adapter，不要在测试与 workflow 各自维护一套参数兼容逻辑。

```powershell
# 默认 Auto：能用 CLI 就用，不能则安全回退
powershell -File Tools/run-tests.ps1

# 明确验证某个 Adapter
powershell -File Tools/run-tests.ps1 -Adapter UnityCli -TestPlatform EditMode
powershell -File Tools/run-tests.ps1 -Adapter Direct -UnityPath E:\Unity\6000.3.22f1\Editor\Unity.exe
```

资源构建 workflow 不再硬编码某台机器的 Editor 版本；它读取仓库中的 `ProjectVersion.txt`，复用同一 Module。旧 Hub 或特殊安装机器只需配置 runner 环境变量 `UNITY_EDITOR_PATH`。

快速回归启动 Seam 而不启动 Unity：

```powershell
powershell -File Tools/Tests/UnityAutomation.Tests.ps1
```

该契约测试覆盖 Auto / Direct 选择、错误版本拒绝、CLI-owned 参数过滤、Direct quoting、`unity run` 的 `-runTests` 拒绝和专用 `unity test` 参数映射。真实 Editor / NUnit 端到端仍由隔离 smoke 与正式项目测试承担。

## Pipeline 能做什么

`com.unity.pipeline` 在当前 Editor / Development Player 中启动 localhost HTTP Server。0.5.0-exp.1 已提供命令发现、场景与 GameObject、资产 / Prefab、脚本编译与热重载、Package Manager、项目设置、构建、测试、Console / 性能、Game / Scene / UI Toolkit 截图、Play / Stop / Pause、菜单和后台 `set_autotick` 等命令。项目也可用 `[CliCommand]` 与 `[CliArg]` 注册静态 C# 命令，返回结构化 JSON。

它对 AI 友好的关键点不是“又多一套工具”，而是允许把频繁的 `execute_code` 临时代码深化成有名称、有 schema、有测试的项目命令。涉及覆盖、删除或设置变更时，应遵循 Package 的 `confirm` / `dry_run` 约定；场景 / GameObject 变更进入同一个 Undo group，调用者提供的路径必须限制在项目安全根内。

## 为什么现在不直接安装 Pipeline

- CLI 与 Pipeline 仍是 beta / experimental，Package API 和命令名可能变化；
- 当前 `com.anklebreaker.unity-mcp` 已覆盖交互式 Editor 的主要能力，Unity 官方明确第三方 MCP 不受 Assistant MCP 迁移影响；
- 把 Pipeline 直接引用进 `Game.Framework.Editor` 会让通用 Framework 对实验 Package 产生硬依赖，破坏可裁剪边界；
- 当前最痛的真实问题是工程外 Editor 定位漂移，已由独立 `Tools` Module 解决，无需先承担 Package 成本。

若后续实测需要第二个当前 Editor Adapter，应新增可物理删除的 Editor-only Module（例如 `Game.Framework.UnityCli.Editor`）：它只引用共享的项目自动化 Implementation 和 `Unity.Pipeline`，不让 Runtime、Demo 或既有 Editor Module 反向依赖它。至少验证“删除该 Module + 删除 Pipeline Package 后，MCP / 菜单 / headless 测试仍工作”，再将它纳入默认工程。

## 官方资料

- [Unity CLI 文档](https://docs.unity.com/en-us/unity-cli)
- [Unity CLI command reference](https://docs.unity.com/en-us/unity-cli/unity-cli-reference)
- [Unity Hub 中使用 Unity CLI](https://docs.unity.com/en-us/hub/use-unity-cli)
- [Unity CLI 与 Pipeline Package 的分工](https://docs.unity.com/en-us/unity-production-pipeline/local-tools-cli/unity-cli-pipeline-package)
- [Unity Pipeline 0.5.0-exp.1](https://docs.unity3d.com/Packages/com.unity.pipeline@0.5/manual/index.html)
- [从 Unity Assistant MCP 迁移到 Unity CLI](https://docs.unity.com/en-us/unity-cli/replace-mcp-server-unity-cli)
