# SSFramework 接入工具

**先运行本工具，再打开 Unity。** 工具把 SSFramework 的固定 Git 地址、OpenUPM 包源和所选 Unity MCP 合并到工程清单；Unity 启动后负责下载和编译，无需先在 Package Manager 添加 Framework。

工具可独立分发，只需同目录的 `Setup-SSFramework.cmd` 和 `Setup-SSFramework.ps1`，无需先克隆整个框架仓库。脚本需要 Windows PowerShell 5.1 或 PowerShell 7；自动接入 Git 包还需要 Git。无需 OpenUPM CLI。Node.js / Python 等是可选 MCP 后续连接所需的环境。

当前候选 Framework revision 见[接入指南](../docs/consuming-framework.md#unity-package-managergit-url)，固定到已推送提交；重复运行保留已有 Framework 版本，不自动升级。完整包仍需完成 Unity 6.3 消费验收。YooAsset 随整包安装，暂不能取消；字体、Git 文件和项目模板也尚未提供，用法边界见[初始化规划](../docs/project-startup.md#依赖与可选能力)。

## Windows 双击运行

1. 关闭需要配置的 Unity 工程。
2. 双击 [`Setup-SSFramework.cmd`](Setup-SSFramework.cmd)。
3. 粘贴 **Unity 工程根目录**，例如 `D:\Games\MyGame`，然后按 Enter。
4. SSFramework 默认推荐 **自动加入清单**，直接回车接受；也可选择手动安装或跳过。
5. MCP 可选 `0` 跳过、`1` AnkleBreaker、`2` Coplay。新工程推荐跳过；清单中已有一个已知提供方时，推荐保留它。回车接受显示的推荐项。
6. 若选择 MCP，其安装方式默认推荐 **加入清单**；想亲自体验 Package Manager 时可选手动指引。
7. 工具显示变更预览并检查联网。核对工程、所选包和修改范围，输入 `y` 才写入；直接回车只预览。
8. 查看执行结果与备份路径，然后打开 Unity 等待解析、签名提示和编译。自动模式无需再添加任何 Git 地址；手动模式按最后显示的地址操作。MCP 插件安装后还需配置连接。

输出分为 **选择 → 变更预览 → 执行结果 → 下一步**。颜色区分阶段、成功与提醒；无颜色的终端仍可通过文字区分。预览列出将添加的内容和用途，结果列出实际添加项；仅预览、无需修改和预检失败有各自的提示。详细 Scope、来源地址、许可链接和服务端命令使用 `-Details` 查看。

脚本含中文，保留 UTF-8 BOM 以兼容 Windows PowerShell 5.1。启动器的执行策略选项只作用于本次进程，不修改系统执行策略；启动器也支持转发下面的命令行参数。

原 `Configure-OpenUPM.cmd` / `.ps1` 保留为只配置包源的兼容入口；它们调用同目录的 `Setup-SSFramework.ps1`，使用旧入口时也要保留这个文件。

## 命令行

预览 Framework 自动接入，并执行联网预检：

```powershell
& 'D:\Tools\SSFramework-Setup\Setup-SSFramework.ps1' -ProjectPath 'D:\Games\MyGame' -CheckNetwork
```

| 参数 | 含义 |
|---|---|
| `-FrameworkInstallMode Manifest / Manual / Skip` | 默认 Manifest，把固定 Git 地址纳入计划；Manual 显示手动地址；Skip 跳过 Framework。均不升级或删除已有 Framework |
| `-UnityMcp None / AnkleBreaker / Coplay` | 选择提供方，默认 None；None 不卸载已有 MCP |
| `-McpInstallMode Manual / Manifest` | 非交互命令默认 Manual，保留原脚本行为；交互选择推荐 Manifest，将所选包纳入计划 |
| `-SkipOpenUPM` | 不修改包源；新装 Framework 时仍检查必需 Scope 已存在。仅配置 MCP 可配合 `-FrameworkInstallMode Skip` |
| `-Details` | 展开包源、包地址、MCP 环境和连接命令 |
| `-CheckNetwork` | 检查 OpenUPM 与待添加 Git 包的元数据；双击入口默认启用，直接调用脚本时按需指定 |
| `-SkipNetworkCheck` | 跳过元数据预检，覆盖 CheckNetwork；仅在已确认 Unity 网络可用或计划稍后联网时使用 |
| `-Apply` | 应用预览中的清单变更；只选择提供方不会自动写入 |
| `-WhatIf` | 即使指定 Apply 也只预览 |
| `-Interactive` | 显示推荐选择并询问是否应用；双击入口同时启用 CheckNetwork |
| `-PassThru` | 返回计划对象，包含所选版本、变更项、NetworkChecks / NetworkBlocked、Applied 和备份路径 |

例如，预览 Framework、包源与 Coplay 的清单改动，并展开详情：

```powershell
& './Tools~/Setup-SSFramework.ps1' -ProjectPath 'D:\Games\MyGame' -UnityMcp Coplay -McpInstallMode Manifest -CheckNetwork -Details
```

复核后加 `-Apply` 写入。只补齐包源并保留手动 Framework 安装时，使用 `-FrameworkInstallMode Manual`；想查看已有 MCP 的连接命令，可选择对应提供方并添加 `-Details`。

## 提前检查与常见问题

工具检查工程结构、Unity 版本、JSON 格式、包源冲突、已知 MCP 冲突和 Git 是否存在。首次自动加入 Framework 限于 Unity 6.3；其他版本可用 Manual 模式先评估。PATH 检测不等于运行环境版本已达标。

联网预检读取包元数据，每个请求超时设置为 8 秒，失败最多重试一次；任一检查仍失败，整份清单保持原状。它只验证当前 PowerShell 进程访问这些元数据的情况，不保证 Unity 的代理、所有依赖下载、Git 克隆或账号服务都可用。配置已完整时不再联网。预检失败先检查网络并重试，已确认是预检环境差异时可显式跳过；脚本不会更改系统代理或证书。

遇到红色 Console 信息，先区分 Package Manager 下载错误、Unity 账号服务错误和 C# 编译错误，见[故障排查](../docs/consuming-framework.md#出现问题时先检查)。签名确认仍由 Unity 展示；工具不会自动点击确认。

## MCP 环境与连接

工具中的候选版本已核对官方包清单及 npm / PyPI 发布信息，插件与配套服务端版本一起显示；这些版本是接入候选，不是当前工程已经通过 Unity 验收的声明。升级候选时同步核对两端版本和许可。

环境检查只报告命令是否能从 PATH 找到，不执行命令或保证版本满足要求。工具不下载运行环境、不启动服务端、不写 AI 客户端配置，也不选择在线服务或登录账号。显示的服务端命令用于后续配置，首次运行可能下载第三方代码。

插件解析与编译完成后，在提供方的 Editor 窗口配置所用客户端，核对生成配置里的服务端版本；连接后按[自动化接入验收](../docs/unity-mcp-tips.md#可选-mcp-接入与验收)检查目标工程、重编译恢复和最终测试结果。配置格式与启动方式取决于客户端，不能直接把一条终端命令当成所有客户端通用的配置文件。

## 修改范围与恢复

- 合并 `Packages/manifest.json` 中的 `scopedRegistries`；Framework 和 MCP 的 Manifest 模式一并加入对应 Git 依赖。所有变更一次预览、一次替换，JSON 缩进可能变化。
- 保留其他依赖版本、Registry、已有 Scope、`testables` 和其他字段；不写 `packages-lock.json` 或 `ProjectSettings`，不替换已有 MCP 的版本或来源。
- 已有 OpenUPM 时只补缺少的 Scope；已有命名空间 Scope 足以覆盖依赖时不重复添加。
- 已声明、已解析或嵌入的 Framework 保留现有来源与版本；旧版本不会被静默升级。只在锁文件发现时，仍需在 Unity 核对实际依赖关系与解析结果。
- 原文件通过原子替换备份到 `UserSettings/SSFrameworkSetup/manifest-<唯一编号>.json`，工具会输出具体位置。重复运行且配置已完整时，不写文件、不创建备份。
- 若其他 Registry 已声明相同包名或 `org.nuget` 的更具体 Scope，先在 Package Manager 中解决来源冲突；工具不会擅自覆盖该选择。
- MCP 的 Manifest 模式发现另一提供方、不同版本、已嵌入的同名包或由其他依赖引入的同名包时，会在写入前停止；包源与 MCP 改动一起应用，不会因为后一步冲突而只写入前一半。
- 检查范围包括清单、锁文件和 `Packages` 下的 embedded 包，以 `package.json` 中的包名识别嵌入包；不自动识别所有 `Assets` 导入方式或其他 MCP 产品。
- Unity 持有工程锁时，写入会失败；关掉该工程再运行即可。配置工具不关闭编辑器、不删除锁文件。
- 预览后如果清单被其他程序修改，应用会停止，重新运行以核对最新计划；输入无效选项也不会产生部分写入。

如需撤销，关闭 Unity，对比备份与当前 `manifest.json`，撤销本次添加的 Registry / Scope、Framework 和 MCP 依赖；只有确认之后没有其他清单改动时，才用完整备份覆盖。重新打开工程后由 UPM 解析锁文件；服务端缓存与客户端连接配置各有自己的管理入口。备份只保存在本机，后续包下载、签名提示、编译与安装验收仍由 Package Manager 负责。

## 工具维护与验证

在 PowerShell 中运行，无需 Pester 或 Unity：

```powershell
& './Tools~/Test-Configure-OpenUPM.ps1'
& './Tools~/Test-Setup-SSFramework.ps1'
```

两个测试入口均支持 `-ConsumerProject '<工程根目录>'`：先对真实工程只读预览，再对清单副本验证写入；不安装包或改真实工程清单。测试夹具写入系统临时目录，可通过 `-OutputDirectory` 指定位置。原包源测试检查所有非 Unity 依赖的 Scope 覆盖；新测试覆盖 Framework 与 MCP 共同安装、已有版本保留、推荐选项、手动模式、冲突、备份、锁、交互取消、并发修改、重复运行与联网失败恢复。联网单元场景使用模拟响应；真实联网预览另行执行。

`Tools~` 是包外引导工具的分发目录；Unity 忽略以 `~` 结尾的目录，不为这些文件生成 `.meta`，也不会将脚本编译或自动执行。
