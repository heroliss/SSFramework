# SSFramework 接入工具

在 Unity 外运行，配置 OpenUPM，并按需选择 Unity MCP。工具本身只需要 Windows PowerShell 5.1 或 PowerShell 7；不要求先安装 Framework、Node.js、Python 或 OpenUPM CLI。连接所选 MCP 时，才需要准备该提供方的服务端环境。

**默认保留手动安装流程**：MCP 可以跳过、选择 AnkleBreaker 或 Coplay。选择提供方后，默认输出固定版本的 Git URL、配套服务端命令、许可来源与连接步骤；也可以选择把 MCP 包加入工程清单。Framework 的 Git URL 仍由使用者在 Unity Package Manager 中添加。

当前选择器只管理上述 MCP，不提供运行时模块、字体、Git 文件或项目文档模板选择。这些能力的当前边界与后续工作见[初始化规划](../docs/project-startup.md#依赖与可选能力)。

## Windows 双击运行

1. 关闭需要配置的 Unity 工程。
2. 双击 [`Setup-SSFramework.cmd`](Setup-SSFramework.cmd)。
3. 粘贴 **Unity 工程根目录**，例如 `D:\Games\MyGame`，然后按 Enter。
4. 选择 MCP：`0` 跳过、`1` AnkleBreaker、`2` Coplay；直接回车跳过，已有包会保留。
5. 选择 MCP 后，再选 `1` 手动指引或 `2` 加入清单；直接回车使用手动指引。
6. 核对工程、包源、所选版本及待修改项。输入 `y` 应用清单配置；其他输入只预览。
7. 重新打开 Unity，在 Package Manager 中添加已审查的 Framework Git URL。选择手动安装 MCP 时，按工具显示的地址安装；随后完成服务端与客户端连接。

新版双击入口只需要同目录的 `Setup-SSFramework.cmd` 和 `Setup-SSFramework.ps1`。脚本含中文，保留 UTF-8 BOM 以兼容 Windows PowerShell 5.1。启动器的执行策略选项只作用于本次进程，不修改系统执行策略。

原 `Configure-OpenUPM.cmd` / `.ps1` 保留为只配置包源的兼容入口；它们调用同目录的 `Setup-SSFramework.ps1`，使用旧入口时也要保留这个文件。

## 命令行

预览包源配置，并显示 AnkleBreaker 的手动接入步骤：

```powershell
& 'D:\Source\SSFramework\Tools~\Setup-SSFramework.ps1' -ProjectPath 'D:\Games\MyGame' -UnityMcp AnkleBreaker
```

| 参数 | 含义 |
|---|---|
| `-UnityMcp None / AnkleBreaker / Coplay` | 选择提供方，默认 None；None 不卸载已有 MCP |
| `-McpInstallMode Manual / Manifest` | 默认 Manual，仅提供 MCP 指引；Manifest 将所选包纳入待应用计划 |
| `-SkipOpenUPM` | 只处理 MCP，不配置 Framework 包源 |
| `-Apply` | 应用预览中的清单变更；只选择提供方不会自动写入 |
| `-WhatIf` | 即使指定 Apply 也只预览 |
| `-Interactive` | 与双击入口相同的交互流程 |
| `-PassThru` | 返回计划对象，包含所选版本、变更项、是否应用及备份路径 |

例如，预览同时配置包源和安装 Coplay 的清单改动：

```powershell
& './Tools~/Setup-SSFramework.ps1' -ProjectPath 'D:\Games\MyGame' -UnityMcp Coplay -McpInstallMode Manifest
```

复核后加 `-Apply` 写入。仅补齐包源、不选择 MCP 时，可省略两个 MCP 参数。

## MCP 环境与连接

工具中的候选版本已核对官方包清单及 npm / PyPI 发布信息，插件与配套服务端版本一起显示；这些版本是接入候选，不是当前工程已经通过 Unity 验收的声明。升级候选时同步核对两端版本和许可。

环境检查只报告命令是否能从 PATH 找到，不执行命令或保证版本满足要求。工具不下载运行环境、不启动服务端、不写 AI 客户端配置，也不选择在线服务或登录账号。显示的服务端命令用于后续配置，首次运行可能下载第三方代码。

插件解析与编译完成后，在提供方的 Editor 窗口配置所用客户端，核对生成配置里的服务端版本；连接后按[自动化接入验收](../docs/unity-mcp-tips.md#可选-mcp-接入与验收)检查目标工程、重编译恢复和最终测试结果。配置格式与启动方式取决于客户端，不能直接把一条终端命令当成所有客户端通用的配置文件。

## 修改范围与恢复

- 合并 `Packages/manifest.json` 中的 `scopedRegistries`；选择 MCP 的 Manifest 模式时，一并加入所选包的依赖。JSON 缩进可能变化。
- 保留其他依赖版本、Registry、已有 Scope、`testables` 和其他字段；不写 `packages-lock.json` 或 `ProjectSettings`，不替换已有 MCP 的版本或来源。
- 已有 OpenUPM 时只补缺少的 Scope；已有命名空间 Scope 足以覆盖依赖时不重复添加。
- 原文件通过原子替换备份到 `UserSettings/SSFrameworkSetup/manifest-<唯一编号>.json`，工具会输出具体位置。重复运行且配置已完整时，不写文件、不创建备份。
- 若其他 Registry 已声明相同包名或 `org.nuget` 的更具体 Scope，先在 Package Manager 中解决来源冲突；工具不会擅自覆盖该选择。
- MCP 的 Manifest 模式发现另一提供方、不同版本、已嵌入的同名包或由其他依赖引入的同名包时，会在写入前停止；包源与 MCP 改动一起应用，不会因为后一步冲突而只写入前一半。
- 检查范围包括清单、锁文件和 `Packages` 下的 embedded 包，以 `package.json` 中的包名识别嵌入包；不自动识别所有 `Assets` 导入方式或其他 MCP 产品。
- Unity 持有工程锁时，写入会失败；关掉该工程再运行即可。配置工具不关闭编辑器、不删除锁文件。

如需撤销，关闭 Unity，对比备份与当前 `manifest.json`，撤销本次添加的 Registry / Scope 和 MCP 依赖；只有确认之后没有其他清单改动时，才用完整备份覆盖。重新打开工程后由 UPM 解析锁文件；服务端缓存与客户端连接配置各有自己的管理入口。备份只保存在本机，后续包下载、签名提示、编译与安装验收仍由 Package Manager 负责。

## 工具维护与验证

在 PowerShell 中运行，无需 Pester 或 Unity：

```powershell
& './Tools~/Test-Configure-OpenUPM.ps1'
& './Tools~/Test-Setup-SSFramework.ps1'
```

两个测试入口均支持 `-ConsumerProject '<工程根目录>'`：先对真实工程只读预览，再对清单副本验证写入；不安装包或改真实工程清单。测试夹具写入系统临时目录，可通过 `-OutputDirectory` 指定位置。原包源测试继续检查所有非 Unity 依赖的 Scope 覆盖；新测试覆盖两种 MCP 选择、手动模式、冲突、备份、锁、交互取消、并发修改及重复运行。

`Tools~` 是包外引导工具的分发目录；Unity 忽略以 `~` 结尾的目录，不为这些文件生成 `.meta`，也不会将脚本编译或自动执行。
