# SSFramework 接入工具

**先运行本工具，再打开 Unity。** 工具把 SSFramework 的固定 Git 地址、OpenUPM 包源和所选 Unity MCP 合并到工程清单；Unity 启动后负责下载和编译，无需先在 Package Manager 添加 Framework。

如果希望完全自己操作 Package Manager，可直接使用[手动安装步骤](../docs/consuming-framework.md#手动安装无需工具)，无需运行本工具。两种方式都以[安装验收](../docs/consuming-framework.md#安装验收)结束，包括最小场景、测试、是否启用 HybridCLR 和目标 Player 构建。

建议下载并完整解压工具 ZIP，无需克隆整个框架仓库。仅配置包与编译选项时仍可只使用同目录的 `Setup-SSFramework.cmd` / `.ps1`；选择 Git / AI 初始化时还需保留 `Templates/UnityProject`，自检使用 `Check-SSFrameworkProject.cmd` / `.ps1`。脚本需要 Windows PowerShell 5.1 或 PowerShell 7；自动接入 Git 包还需要 Git。无需 OpenUPM CLI。Node.js / Python 等是可选 MCP 后续连接所需的环境。

下载[最新发布的工具 ZIP](https://github.com/heroliss/SSFramework/releases/latest/download/SSFramework-Setup.zip)后解压即可运行。工具默认固定 Framework 到 `v0.1.5` 标签；重复运行保留已有 Framework 版本，不自动升级。验证范围见[接入指南](../docs/consuming-framework.md#版本选择与发布)。YooAsset 随整包安装，暂不能取消；Git / AI 模板可选择仅创建缺失文件或完全手动采用，中文字体另有可选导入包，完整项目目录初始化尚未提供。

## Windows 双击运行

Framework 自动模式同时配置业务代码的 **C# 10.0**，支持 `record struct` 与 Trace 插值处理器。工具合并 `Assets/csc.rsp` 和已有 asmdef 同目录响应文件，保留其他参数；预览列出每条变更，已有文件逐份备份，写入失败时回滚已完成的修改。手动模式自行使用 [`Templates/CSharp10/csc.rsp`](Templates/CSharp10/csc.rsp)。采用原因、作用范围与限制见[语言版本约定](../docs/consuming-framework.md#c-10-默认约定与原因)。

1. 关闭需要配置的 Unity 工程。
2. 双击 [`Setup-SSFramework.cmd`](Setup-SSFramework.cmd)。
3. 粘贴 **Unity 工程根目录**，例如 `D:\Games\MyGame`，然后按 Enter。
4. SSFramework 默认推荐 **自动加入清单**，直接回车接受；也可选择手动安装或跳过。
5. MCP 可选 `0` 跳过、`1` AnkleBreaker、`2` Coplay。新工程推荐跳过；清单中已有一个已知提供方时，推荐保留它。回车接受显示的推荐项。
6. 若选择 MCP，其安装方式默认推荐 **加入清单**；想亲自体验 Package Manager 时可选手动指引。
7. 分别选择可选 **Git 配置**和 **AI 协作入口**。有缺失文件时推荐仅创建缺失项；已有文件时推荐跳过。无论选择哪项，都不覆盖项目已有文件。
8. 工具显示变更预览并检查联网。核对工程、所选包和修改范围，输入 `y` 才写入；直接回车只预览。`-Details` 可展开新增模板的完整内容。
9. 查看执行结果与备份路径，然后打开 Unity 等待解析、签名提示和编译。自动模式无需再添加 Git 地址；手动模式按最后显示的地址操作。MCP 插件安装后还需配置连接。
10. 运行下文的[安装后配置自检](#安装后配置自检只读)，处理文件检查提示，再在 Unity 完成实际安装验收。

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
| `-GitConfiguration Skip / CreateMissing` | 非交互默认 Skip；CreateMissing 仅创建缺失的 `.gitignore` / `.gitattributes`，不合并或替换已有规则 |
| `-AiRules Skip / CreateMissing` | 非交互默认 Skip；CreateMissing 仅在缺失时创建项目 `AGENTS.md` |
| `-SkipOpenUPM` | 不修改包源；新装 Framework 时仍检查必需 Scope 已存在。仅配置 MCP 可配合 `-FrameworkInstallMode Skip` |
| `-SkipCompilerConfiguration` | 自行维护业务程序集编译配置时使用；跳过响应文件修改，仍需确保使用指南示例的程序集启用了 C# 10 |
| `-Details` | 展开包源、包地址、MCP 环境、连接命令及本次新增模板内容 |
| `-CheckNetwork` | 检查 OpenUPM 与待添加 Git 包的元数据；双击入口默认启用，直接调用脚本时按需指定 |
| `-SkipNetworkCheck` | 跳过元数据预检，覆盖 CheckNetwork；仅在已确认 Unity 网络可用或计划稍后联网时使用 |
| `-Apply` | 应用预览中的清单、编译与可选项目文件变更；只选择选项不会自动写入 |
| `-WhatIf` | 即使指定 Apply 也只预览 |
| `-Interactive` | 显示推荐选择并询问是否应用；双击入口同时启用 CheckNetwork |
| `-PassThru` | 返回计划对象，包含所选版本、变更项、NetworkChecks / NetworkBlocked、Applied 和备份路径 |

例如，预览 Framework、包源与 Coplay 的清单改动，并展开详情：

```powershell
& './Tools~/Setup-SSFramework.ps1' -ProjectPath 'D:\Games\MyGame' -UnityMcp Coplay -McpInstallMode Manifest -CheckNetwork -Details
```

复核后加 `-Apply` 写入。只补齐包源并保留手动 Framework 安装时，使用 `-FrameworkInstallMode Manual`；想查看已有 MCP 的连接命令，可选择对应提供方并添加 `-Details`。

只预览缺失的 Git / AI 文件，不调整包源、Framework 或编译配置：

```powershell
& './Tools~/Setup-SSFramework.ps1' -ProjectPath 'D:\Games\MyGame' -FrameworkInstallMode Skip -SkipOpenUPM -GitConfiguration CreateMissing -AiRules CreateMissing -Details
```

选择初始化但发行目录缺少模板时，工具在任何写入前停止；重新解压完整 ZIP，或将对应选项设为 Skip。旧 `Configure-OpenUPM` 入口始终跳过这两项，不改变只配置包源的行为。

## 安装后配置自检（只读）

双击 [`Check-SSFrameworkProject.cmd`](Check-SSFrameworkProject.cmd)，输入工程根目录。自动、手动接入均可使用；无需 Unity MCP，也不要求升级已经安装的 Framework。它不启动 Unity、不联网、不写文件、不自动修复项目设置。

自检显示 **通过 / 需处理 / 错误 / 说明 / 待验证**，每个需处理项给出下一步：

- Unity 版本、清单与锁文件格式、Framework Git 选择是否与锁记录一致；
- Framework 的依赖闭包是否有版本记录，当前 Scope 是否仍能匹配已锁定包源；
- 业务默认 `csc.rsp`，以及按名称引用 Framework 的业务 asmdef 中会覆盖默认值的局部响应文件；
- HybridCLR 的项目 Enable 选择、全局启用场景数量、Git / AI 文件是否存在。

锁文件只能证明声明记录，不能证明下载或编译完成；自检不读取缓存 DLL 来推断成功。GUID 形式的 asmdef 引用留给 Unity 解析；自定义 Build Profile 可能覆盖全局场景列表。没有 HybridCLR 项目设置时显示“未明确”，不会误报为已关闭。已存在的 Git / AI 文件只报告存在，不宣称规则内容已经适合项目。

```powershell
& './Tools~/Check-SSFrameworkProject.ps1' -ProjectPath 'D:\Games\MyGame'
& './Tools~/Check-SSFrameworkProject.ps1' -ProjectPath 'D:\Games\MyGame' -Json
```

`-Json` 仅输出 JSON 报告，`-PassThru` 在普通显示后返回对象；`-FailOnError` 在文件错误或检查期间文件发生变化时抛错，供自动化使用。报告的 `Status` 为 `FilesChecked`、`Review`、`Blocked` 或 `Retry`，**`RequiresUnityVerification` 始终为 true**。`FilesChecked` 仅表示本轮文件检查未发现待处理项，不能充当编译、场景、测试或 Player 的验收证明。若 Unity / UPM 正在写入被检查文件，本轮结果标为 Retry，等待完成后再检查。

## 提前检查与常见问题

工具检查工程结构、Unity 版本、JSON 格式、包源冲突、已知 MCP 冲突、响应文件和 Git 是否存在。响应文件缺少语言选项时补齐，低于 10 时提升为 10.0；已选更高版本或 latest / preview 时保留并提示只验证过 10.0，不自动降级。多个冲突的语言选项会在写入前拒绝。响应文件使用 UTF-8 并保留原 BOM；已有文件的备份按原始字节保存。首次自动加入 Framework 限于 Unity 6.3；其他版本可用 Manual 模式先评估。PATH 检测不等于运行环境版本已达标。

联网预检读取包元数据，每个请求超时设置为 8 秒，失败最多重试一次；任一检查仍失败，整份清单保持原状。它只验证当前 PowerShell 进程访问这些元数据的情况，不保证 Unity 的代理、所有依赖下载、Git 克隆或账号服务都可用。配置已完整时不再联网。预检失败先检查网络并重试，已确认是预检环境差异时可显式跳过；脚本不会更改系统代理或证书。

遇到红色 Console 信息，先区分 Package Manager 下载错误、Unity 账号服务错误和 C# 编译错误，见[故障排查](../docs/consuming-framework.md#出现问题时先检查)。签名确认仍由 Unity 展示；工具不会自动点击确认。

## 消费工程的 Git 配置

仓库根目录的 `.gitignore` / `.gitattributes` 只管理 Framework 仓库，UPM 不会把它们复制或应用到游戏工程。可在安装器选择仅创建缺失文件，也可手动采用下面的模板；ZIP 的 `Templates/UnityProject` 目录附带同样的文件：

| 模板 | 放在 Unity 工程根目录时的名称 | 作用 |
|---|---|---|
| [`gitignore.template`](Templates/UnityProject/gitignore.template) | `.gitignore` | 排除 Unity / IDE 缓存、本机设置及资源构建输出；保留 Assets 与 meta、包清单 / 锁文件、ProjectSettings |
| [`gitattributes.template`](Templates/UnityProject/gitattributes.template) | `.gitattributes` | 统一源码与配置的文本换行，保持二进制资产；不启用 Git LFS 或自定义 Merge Driver |

1. 没有相应文件时，将模板复制到与 `Assets`、`Packages`、`ProjectSettings` 同级的位置并重命名；不要保留 `.template` 或误加 `.txt`。
2. 已有文件时对比后手动合并需要的条目，保留项目原有 LFS、合并与忽略策略。不要直接覆盖，也不在采纳模板时自动对整个仓库执行换行重写。
3. `Assets/StreamingAssets/yoo` 的忽略条目默认注释。只有确定每次检出都会先重建随包资源时才启用；不要忽略整个 StreamingAssets 或父目录的 `.meta`。
4. 用 `git status --short` 检查结果：源码、资源、meta、清单与项目设置应可提交，Library / UserSettings / 构建产物应被忽略。后加规则不会自动取消跟踪已经入库的缓存文件，需要自行审查处理。

安装工具不创建 Git 仓库、不执行 git add、不合并或覆盖已有规则。模板是一次性采用的起点；之后由消费工程维护，Framework 升级不会重写它们。Git LFS 与 Unity YAML Merge 在项目确有需要时单独配置。

模板已在独立 Unity 6.3 消费工程中核对 15 个应忽略路径、13 个应保留路径、5 项属性约定，以及二进制 `.asset` 不被文本换行转换；不将这组基线规则当作所有团队的完整 Git 策略。

## 项目 AI 协作入口

`-AiRules CreateMissing` 使用 [`AGENTS.md.template`](Templates/UnityProject/AGENTS.md.template)，只在工程根目录缺少 AGENTS.md 时创建。也可手动复制并去掉 `.template` 后缀；已有规则自行合并，工具不会检查后擅自改写。

模板说明版本与包锁的真值位置、PackageCache 边界、Unity 资产操作、业务程序集、验证证据和文档生命周期；不绑定 AI 客户端，也不写入登录信息、MCP 配置、虚构的场景、测试命令或游戏设计。README / docs 已存在时按需读取，不预先创建空目录或一套无人维护的文档。模板采用后由项目维护，与 Framework 包内的维护用 AGENTS.md 分开。

## 可选中文字体

下载独立的 [简体中文字体包](https://github.com/heroliss/SSFramework/releases/latest/download/SSFramework-Fonts-zh-CN.unitypackage)，在 Unity 使用 **Assets → Import Package → Custom Package** 导入，按[字体接入说明](../docs/starter-fonts.md)指定 TMP 或 TextCore 字体资产。无需运行字体生成工具；已有 SSFramework 工程也无需为字体升级 UPM 包。

字体包约 6.83 MiB，包含原始 OTF、两套动态字体资产、版权许可及说明。使用 TMP 时先导入 TMP Essential Resources。主安装器只提供入口，不下载字体或修改全局默认字体，使用者可以跳过或自行选择其他字体。

## MCP 环境与连接

工具中的候选版本已核对官方包清单及 npm / PyPI 发布信息，插件与配套服务端版本一起显示；这些版本是接入候选，不是当前工程已经通过 Unity 验收的声明。升级候选时同步核对两端版本和许可。

环境检查只报告命令是否能从 PATH 找到，不执行命令或保证版本满足要求。工具不下载运行环境、不启动服务端、不写 AI 客户端配置，也不选择在线服务或登录账号。显示的服务端命令用于后续配置，首次运行可能下载第三方代码。

插件解析与编译完成后，在提供方的 Editor 窗口配置所用客户端，核对生成配置里的服务端版本；连接后按[自动化接入验收](../docs/unity-mcp-tips.md#可选-mcp-接入与验收)检查目标工程、重编译恢复和最终测试结果。配置格式与启动方式取决于客户端，不能直接把一条终端命令当成所有客户端通用的配置文件。

## 修改范围与恢复

- 合并 `Packages/manifest.json` 中的 `scopedRegistries`；Framework 和 MCP 的 Manifest 模式一并加入对应 Git 依赖。Framework 自动模式还配置业务响应文件；Git / AI 的 CreateMissing 模式仅创建缺失文件。所有变更统一预览、统一提交或回滚，JSON 缩进可能变化。
- 保留其他依赖版本、Registry、已有 Scope、`testables` 和其他字段；不写 `packages-lock.json` 或 `ProjectSettings`，不替换已有 MCP 的版本或来源。
- 已有 OpenUPM 时只补缺少的 Scope；已有命名空间 Scope 足以覆盖依赖时不重复添加。
- 已声明、已解析或嵌入的 Framework 保留现有来源与版本；旧版本不会被静默升级。只在锁文件发现时，仍需在 Unity 核对实际依赖关系与解析结果。
- 被修改的已有清单 / 响应文件通过原子替换备份到 `UserSettings/SSFrameworkSetup/manifest-<唯一编号>.json` 或 `compiler-<唯一编号>.rsp`。新建的响应文件与项目规则没有旧文件备份，结果明确列出其路径；已有 Git / AI 文件保留不动。重复运行且配置已完整时，不写文件、不创建备份。
- 若其他 Registry 已声明相同包名或 `org.nuget` 的更具体 Scope，先在 Package Manager 中解决来源冲突；工具不会擅自覆盖该选择。
- MCP 的 Manifest 模式发现另一提供方、不同版本、已嵌入的同名包或由其他依赖引入的同名包时，会在写入前停止；包源与 MCP 改动一起应用，不会因为后一步冲突而只写入前一半。
- 检查范围包括清单、锁文件和 `Packages` 下的 embedded 包，以 `package.json` 中的包名识别嵌入包；不自动识别所有 `Assets` 导入方式或其他 MCP 产品。
- Unity 持有工程锁时，写入会失败；关掉该工程再运行即可。配置工具不关闭编辑器、不删除锁文件。
- 预览后如果清单被其他程序修改，应用会停止，重新运行以核对最新计划；输入无效选项也不会产生部分写入。

如需撤销，关闭 Unity，对比备份与当前文件，撤销本次添加的 Registry / Scope、Framework、MCP 和语言选项；只有确认之后没有其他改动时，才用完整备份覆盖。新建的响应文件或项目规则若后来加入用户内容，应逐项合并撤销，不能直接删除。重新打开工程后由 UPM 解析锁文件与重编译。服务端缓存与客户端连接配置各有自己的管理入口。备份只保存在本机，后续包下载、签名提示、编译与安装验收仍由 Package Manager 负责。

## 工具维护与验证

在 PowerShell 中运行，无需 Pester 或 Unity：

```powershell
& './Tools~/Test-Configure-OpenUPM.ps1'
& './Tools~/Test-Setup-SSFramework.ps1'
& './Tools~/Test-Project-Onboarding.ps1'
```

前两个测试入口支持 `-ConsumerProject '<工程根目录>'`：先对真实工程只读预览，再对清单副本验证写入；不安装包或改真实工程清单。测试夹具写入系统临时目录，可通过 `-OutputDirectory` 指定位置。包源测试检查所有非 Unity 依赖的 Scope 覆盖；安装测试覆盖 Framework / MCP、语言配置与局部响应文件、已有版本保留、推荐选项、手动模式、冲突、精确备份、锁、交互取消、并发修改、重复运行、联网失败与多文件写入失败回滚。联网单元场景使用模拟响应；真实联网预览另行执行。

`Test-Project-Onboarding` 另覆盖只读快照、单项 Scope 数组、来源漂移、依赖缺失、局部响应文件、HybridCLR 未配置、GUID 待验证，以及模板预览、文件保留、缺失分发、目录冲突、并发新建、重复运行和事务回滚。它只操作自身临时夹具，支持 OutputDirectory，不启动 Unity。

`Tools~` 是包外引导工具的分发目录；Unity 忽略以 `~` 结尾的目录，不为这些文件生成 `.meta`，也不会将脚本编译或自动执行。
