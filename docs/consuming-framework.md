# 接入与升级 SSFramework

这份文档面向任何需要在 Unity 工程中使用 `com.liss.ssframework` 的团队。它只描述包本身的公开接入契约，不依赖某个游戏、教程工程或工作区布局。

安装包不会自动配置消费工程根目录的 Git / AI 规则、生成完整项目目录或提供中文字体。已有[可手动采用的消费工程 Git 模板](../Tools~/README.md#消费工程的-git-配置)；其余能力与建议的初始化流程见[新项目准备](project-startup.md)。

**完整包当前以 Unity 6.3 LTS 为接入目标，版本基线为 `6000.3.22f1`。Unity 6.6 暂不能完整编译此包**：YooAsset 3.0.5 的 Editor 使用了 Unity 6.6 已移除的 `UxmlFactory` / `UxmlTraits`。补齐 DLL 或切换同版本的 Git 来源不能解决这个 API 不兼容；新项目应优先在 6.3 LTS 中创建，不要直接把已经由 6.6 保存的工程降级打开。参见 [Unity 6.6 API 移除说明](https://unity.com/releases/editor/alpha/6000.6.0a5)。

## C# 10 默认约定与原因

SSFramework 默认使用 **C# 10.0**，并在各个一方 `.asmdef` 同目录附带 `csc.rsp`。业务工程也应配置 `-langversion:10.0`，使指南中的写法与实际编译行为一致。Unity 6.3 官方默认语言版本仍为 C# 9；框架在此基础上显式选择 C# 10，而不是把 Unity 默认配置误当成已经启用。

选择 C# 10 的收益：

- **`record struct` 简化事件与数据载体。** 例如 `public record struct GoldChangedEvent(int Delta) : IEvent;` 自动生成位置属性、构造、按值比较、解构与 `with` 支持。它是值类型，普通实例创建不需要额外分配一个引用对象；装箱、引用字段和具体调用方式仍可能分配。无须这些生成行为的 Command、ECS 组件仍可用普通 `struct` / `readonly struct`，不做机械替换。
- **日志插值处理器跳过关闭的 Trace 消息。** C# 10 调用方的 `Log.Trace($"数量：{GetCount()}")` 在全局或所有 sink 拒收时，不计算 `GetCount()`、不拼消息字符串。开启后仍有格式化和写入成本，插值参数不得依赖副作用。
- **统一编译约定。** 框架源码、包内测试与业务示例使用同一固定语言版本；文件范围命名空间等语法可以按需使用，不强制全局 using，也不使用随编译器变化的 `latest` / `preview`。

需要理解的边界：

- **C# 10 不等于 .NET 10。** 响应文件只改变编译语言版本，不会升级 Unity 的 BCL、序列化、Burst 或 IL2CPP。新增语法和相关工具仍需在项目 Unity 版本上编译、测试并构建验证，不能据此保证所有 C# 10 特性都可用。
- 普通位置 `record struct` 的属性默认可写；作为事件时按快照使用，不把它误称为不可变类型。位置 `readonly record struct` / `init` 还需要 `IsExternalInit` 标记，Unity 的对应 BCL 不自带；当前框架不替所有业务程序集注入该类型，使用前应在所属程序集显式补齐并验证，或采用已有的只读结构体写法。
- 记录类型用于纯 C# 数据传递；Inspector / Unity 序列化配置继续用受支持的字段与资产类型。ECS 组件的布局与 Burst 限制单独评估，不因启用 C# 10 就统一改成 record。
- 原来按 C# 9 编译的日志调用在 C# 10 下可能改选处理器重载，从而跳过原本会求值的插值。业务效果不能依赖日志参数中的 `i++`、写状态等操作。

**配置位置与接入顺序：** 自动工具的 Framework 自动模式默认配置 `Assets/csc.rsp`，同时合并 `Assets` 下已有 asmdef 同目录响应文件中的语言选项；保留其他参数，预览全部路径，备份已有文件。手动安装可复制 [`csc.rsp` 模板](../Tools~/Templates/CSharp10/csc.rsp)，或在已有文件中合并 `-langversion:10.0`；不要覆盖其他参数或重复写多个 `-langversion`。

Unity 优先读取 asmdef 同目录的响应文件，没有局部文件才回退到 `Assets/csc.rsp`，两者不会自动合并。框架包内文件负责框架程序集，不会传播给引用它的业务程序集；只在包根目录放一份也不能覆盖全部子模块。修改后重新编译，核对 Editor / PlayMode 测试与目标平台构建。不要修改自动生成的 `.csproj` 或 `PackageCache`。

若有意保留某个 C# 9 业务程序集，其普通 API 调用仍可使用显式 `Log.IsEnabled` 守卫，但不能照搬 `record struct` 示例或假定自动插值转换。安装工具的 `-SkipCompilerConfiguration` 留给自行维护编译配置的使用者。

依据：[Unity 6.3 编译器基线](https://docs.unity3d.com/6000.3/Documentation/Manual/csharp-compiler.html)、[C# 版本历史](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-version-history)、[记录类型语义](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/record)、[Unity 响应文件选择实现](https://github.com/Unity-Technologies/UnityCsReference/blob/6000.3/Editor/Mono/Scripting/Compilers/ResponseFileProvider.cs)。

## 选择安装方式

两条路线安装的是同一个完整包，可自由选择；**手动路线完全不需要下载或运行接入工具**。

| 方式 | 操作顺序 | 适合的情况 |
|---|---|---|
| [手动安装](#手动安装无需工具) | 在 Unity 配置包源 → 按需逐个验证依赖 → 添加 Framework Git 地址 → 选择开发工具 → 验收 | 想熟悉每个安装步骤，或自行管理工程清单 |
| [自动安装工具](#自动安装工具) | 关闭 Unity → 运行工具并核对预览 → 应用 → 打开 Unity 下载与编译 → 验收 | 希望一次配置来源、框架和所选 MCP |

两条路线都需要 Unity 6.3、可访问 GitHub 与包源的网络，以及能被 Unity 找到的 Git。Windows 安装 Git 后若 Editor 已经打开，应重开 Editor，让它读取新的 PATH；前置条件见 [Unity Git 安装说明](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-ui-giturl.html)。已有工程先保存或提交当前包清单，方便对比与恢复。

SSFramework 的 `package.json` 已声明第三方 UPM 包和 NuGet 运行库的 UPM 分发包。配置 OpenUPM 后，添加 Framework 时 Unity 会自动解析和下载必需依赖。**手动安装 Framework 也能自动补齐依赖**，无需逐个安装 DLL 或额外引入 NuGetForUnity。

## 自动安装工具

**先运行工具，再打开 Unity；无需先安装 Framework。** Windows 使用者关闭目标工程，双击 [`Tools~/Setup-SSFramework.cmd`](../Tools~/Setup-SSFramework.cmd)，粘贴工程根目录。Framework 默认推荐自动加入清单；MCP 可选 AnkleBreaker / Coplay，新工程推荐跳过，清单中已有单一提供方时推荐保留。选中 MCP 后，其安装方式推荐加入清单。

核对预览与联网检查后输入 `y`，工具一次保存包源、Framework Git 地址和所选 MCP，自动备份原清单。然后打开 Unity，UPM 会下载清单中的包及其依赖，自动模式无需再粘贴 Git 地址。想亲自操作 Package Manager 时选择 Framework / MCP 的手动模式，按工具最后显示的地址安装。

工具只需同目录的 `.cmd` 与 `.ps1`，可单独发布或下载，不必先克隆整个框架仓库。它保留已有 Framework 版本，重复运行不会自动升级；包源、MCP 冲突或联网预检失败时停止写入。旧 `Configure-OpenUPM` 入口仍只配置包源；默认选项、详情输出、命令行、恢复与验证入口见[工具说明](../Tools~/README.md)。`Tools~` 被 Unity 忽略，不会随包导入自动执行。

## 手动安装（无需工具）

先按上面的 [C# 10 配置说明](#c-10-默认约定与原因)准备业务响应文件，随后配置包源并安装框架。包内编译配置会随 UPM 一起安装，不需要逐个编辑框架文件。

<a name="首次接入配置第三方包源"></a>
<a name="手动配置"></a>

### 1. 手动配置包源

在 **Edit → Project Settings → Package Manager → Scoped Registries** 中点击 **+**，填写：

- **Name**：`OpenUPM`
- **URL**：`https://package.openupm.com`
- **Scope(s)**：下表的六项，每个单独一行；通过 Scope(s) 下方的 **+** 添加行。

| Scope | 用途 |
|---|---|
| `com.cysharp.unitask` | 异步任务包 |
| `com.cysharp.r3` | R3 的 Unity 适配包 |
| `com.code-philosophy.luban` | 配置表运行库 |
| `com.tuyoogame.yooasset` | 资源加载与构建包 |
| `com.code-philosophy.hybridclr` | 热更新相关托管代码与 Editor 工具 |
| `org.nuget` | 覆盖下文运行库及其传递依赖 |

点击 **Save** 保存（修改已有 Registry 时为 **Apply**），再按下文添加依赖或 Framework。Scope 是“哪些包名去这个包源查找”的匹配规则；`org.nuget` 覆盖该命名空间及其多层依赖，其他五项是精确包名。Unity 自带依赖继续使用默认 Unity Registry，不需要把 `com.unity` 加入 OpenUPM Scope。已有 OpenUPM 配置时补齐缺项即可，不要重复创建同一个 Registry。

**从早期版本升级**：如果已经配置最初四项 Scope，请补充 `com.code-philosophy.hybridclr` 和 `org.nuget`，再将 Framework 更新到包含 `0.1.1` 依赖修复的 Git commit。旧 commit `182d967493c90b9632bc3a6e5b3ce95a7eb496b1` 没有声明这些新增依赖，仅补 Scope 不会让旧包自动安装它们。

Registry 配置保存在**消费工程**的 `Packages/manifest.json` 中；Framework 的 `package.json` 不能代替工程设置 `scopedRegistries`。包内 Editor 安装脚本也不能作为解决首次依赖解析失败的前提。因此，工具或手动操作负责“每个工程配置一次包源”，之后由 UPM 自动安装已声明的依赖。相关规则见 [Unity Scoped Registry 文档](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-scoped-use.html)。

`com.cysharp.r3` 提供 Unity 适配层，`org.nuget.r3` 提供 R3 运行库；两者都需要。本依赖基线与 C# 10 配置已在真实 Unity `6000.3.23f1` 消费工程完成解析、编译、测试和普通 Windows IL2CPP 验收；范围见[发布验证](#v011-发布验证)。

### 2. 依赖自动解析或逐个手动安装

一般直接进入下一步添加 Framework 即可。若想了解各个包、提前验证包源，或定位某一个下载失败的依赖，可以先手动安装[依赖清单](#依赖前置条件)中来自 OpenUPM 的包：

1. 打开 **Window → Package Management → Package Manager**，点击左上角 **+**。
2. 选择 **Install package by name**（部分界面显示 **Add package by name**）。在 **Name** 填精确包名，在 **Version** 填依赖清单中的版本；不要填产品显示名或完整 Git URL。
3. 点击 **Install / Add**，等待解析完成后再继续下一个包。已经以合适版本安装的包可以跳过，包自身的传递依赖仍由 UPM 安装。
4. 全部解析完成后再添加 Framework。Unity 自带的 UGUI / UIElements 通常已随工程提供，先核对现有版本和解析结果，不为对齐表格而降级已有 Unity 包。

菜单与版本字段见 [Unity 按名称安装说明](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-ui-quick.html)。逐个添加会让这些包成为工程自己的直接依赖；以后升级 Framework 时，也要复核工程显式指定的版本，避免继续固定在旧版。无论采用哪种方式，当前整包中的 YooAsset、HybridCLR、Luban 都是必需依赖，不能只取消安装其中一个来关闭对应能力。

### Unity Package Manager：Git URL

这是手动路线的第 3 步。在 Package Manager 左上角 **+** 中选择 **Install package from git URL**（部分界面显示 **Add package from git URL**），粘贴下面整行并点击 **Install / Add**。工具自动模式已加入 Framework 时，无需重复添加：

```text
https://github.com/heroliss/SSFramework.git
```

上面解析默认分支 main，适合希望使用简便入口的工程。要固定到本次发行版，使用下面的地址；接入工具默认也使用这个标签。其他已审查版本可使用对应 tag 或完整 commit SHA：

```text
https://github.com/heroliss/SSFramework.git#v0.1.3
```

正式项目优先使用完整 commit SHA 或发布后不再移动的 tag；`#main` 等分支名仍会随开发变化，不能单靠名字确认包内容。不同地址的解析与升级行为见[版本选择与发布](#版本选择与发布)。等待 UPM 下载和脚本编译后，继续选择下面的开发工具，或直接进行[安装验收](#安装验收)。

#### 安装时出现 Missing signature

支持包签名验证的 Unity 版本可能在解析第三方依赖时弹出 **Missing signature**。这表示列出的包没有可验证的 UPM 签名；包已能被找到，但安装仍等待用户决定，后续还需完成编译验证。Unity 对缺少签名与签名无效的区分见[官方说明](https://discussions.unity.com/t/package-manager-changes-package-signing-and-status-labels/1688660)。

2026-09-10 的早期版本安装反馈中，窗口列出了 R3.Unity、UniTask、Luban 和 YooAsset，Source 均为 OpenUPM；新增依赖后也可能列出 HybridCLR。使用本文配置的 `https://package.openupm.com`，并确认包名、版本和来源符合预期后，可以点击 **Install Anyway** 继续安装。缺少签名本身不代表包损坏，也不证明内容安全。如果提示的是 **Invalid signature**，应另行排查来源和包内容，不能直接沿用缺少签名的处理结论。

保留 Registry 安装方式时，要消除缺少签名的提示，需要采用带有效 UPM 签名的依赖发行版，并验证升级兼容性。OpenUPM 已支持发布作者签名的包，但不会代替作者签名；仅给 SSFramework 自身签名不能覆盖独立分发的第三方依赖。发行机制见 [OpenUPM 签名文档](https://openupm.com/docs/signing-upm-packages)。

直接从 Git 安装是另一种来源选择。[Unity 团队于 2026-06-29 的说明](https://discussions.unity.com/t/unity-core-standards-new-features-and-what-s-next/1723174?page=2)指出，Git 和 Embedded 包当前不参与签名验证，因此不应触发自身缺少签名的提示；这不代表它们已获得 UPM 签名。Git 包仍可依赖来自 Registry 的包，后者会单独接受检查，这也是通过 Git 安装 SSFramework 时仍会看到上述窗口的原因。

若将第三方依赖改为各作者的 Git 来源，需要由消费工程显式声明包路径和固定 revision，并验证兼容性。Unity 仅允许在工程 `Packages/manifest.json` 中声明 Git 依赖，不支持在 Framework 的 `package.json` 中声明另一 Git 包，见 [Unity Git 依赖文档](https://docs.unity3d.com/6000.6/Documentation/Manual/upm-git.html)。当前接入流程使用 OpenUPM 自动解析依赖；尚未在消费方验证逐个改用 Git 的方案。安装体验的优化应优先补齐依赖分发与验证，不以关闭签名检查作为接入前提。

## 可选开发工具

这些工具可以在 Framework 编译通过后再安装，也可以完全跳过；不属于根 `package.json` 的必需依赖。以下 MCP 地址与安装器中的固定候选一致，不运行安装器也能使用。

### Unity MCP：选择一个提供方

MCP 让 AI 客户端通过工具读取和操作 Unity Editor。先使用一个提供方完成连接验收；已有插件时先核对实际来源与版本，不要为跟随本文候选而重复导入另一份。

| 提供方 | 本机前置条件 | 固定版本与连接入口 |
|---|---|---|
| AnkleBreaker | Git、Node.js 18 或以上，含 npm / npx | Unity 插件 `2.39.5`，服务端 `2.35.6`；**Window → MCP Dashboard**。采用含署名与再分发条件的[自定义许可](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/blob/v2.39.5/LICENSE) |
| Coplay | Git、Python 3.10 或以上、uv / uvx | Unity 插件与服务端均为 `10.2.0`；**Window → MCP for Unity**。采用 [MIT 许可](https://github.com/CoplayDev/unity-mcp/blob/v10.2.0/LICENSE) |

在 Package Manager 的 **Install package from git URL** 中添加所选提供方的地址。

AnkleBreaker：

```text
https://github.com/AnkleBreaker-Studio/unity-mcp-plugin.git#v2.39.5
```

Coplay：

```text
https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.2.0
```

插件编译完成后，在对应 Editor 窗口按提供方文档配置所用 AI 客户端：[AnkleBreaker 服务端与客户端配置](https://github.com/AnkleBreaker-Studio/unity-mcp-server/blob/v2.35.6/README.md)、[Coplay 安装与连接](https://github.com/CoplayDev/unity-mcp/blob/v10.2.0/website/docs/getting-started/install.md)。核对配置中的服务端版本与上表一致；首次启动服务端可能下载第三方代码。客户端配置格式因宿主而异，安装 Unity 插件本身不会自动完成整个连接。最后执行[可选 MCP 接入与验收](unity-mcp-tips.md#可选-mcp-接入与验收)，确认目标工程、Console 读取和重编译后的连接恢复。

### Odin、字体与其他项目依赖

- **Odin Inspector**：当前不随框架提供，也没有现成 Odin Adapter。需要时由项目自行取得许可，并按[官方安装说明](https://odininspector.com/tutorials/getting-started/installing-odin-inspector)导入；框架使用原生 Inspector 即可工作。扩展限制见 [Odin 依赖边界](optional-odin-integration.md)。
- **中文 / 中日韩字体**：框架提供字体相关能力，但未附带兜底字体资源。由项目选择授权合适的字体、创建所用 UI 后端的字体资产并验证实际字符；安装框架不会自动保证中文显示。
- **ECS、Input System、URP / HDRP**：按游戏需求通过 Unity Package Manager 单独选择；它们不是安装 SSFramework 的前置条件。项目 Git 文件、目录和 AI 规则的准备范围见[新项目准备](project-startup.md)。

## 安装验收

手动与自动路线使用同一套验收，包名出现在 Package Manager 中只是第一步。

1. **依赖解析**：在 Package Manager 核对 Framework 的来源与 revision、依赖解析结果；所有包下载完成，没有待处理的签名或解析错误。`Packages/manifest.json` 记录工程选择，`packages-lock.json` 记录 UPM 实际解析结果，两者应一并纳入工程版本管理。
2. **Editor 编译**：等待导入和编译结束，检查 Console 没有 C# 编译错误；打开 **SSFramework → 工具中心**，确认能读取当前安装的模块。新项目尚无业务 Profile 时，应按需创建所用能力的配置，不必为了消除“尚未配置”说明先生成所有资源。
3. **最小运行路径**：在消费工程创建并保存一个启动场景，按下文[最小使用路径](#最小使用路径)接入 Context、一个 Model / System 与一个 View。验证进入、退出 Play，以及 Command → Model / Event → View 的完整交互；新建业务 asmdef 要显式引用所用的 Framework 程序集，名称见[模块地图](framework-module-map.md)。
4. **测试与目标平台构建**：运行受影响的 EditMode / PlayMode 测试，并用上述最小场景完成一次目标平台 Development Build。构建前先处理下文 [HybridCLR 的默认启用状态](#构建前选择是否启用热更新)。选择 IL2CPP 时在 Unity Hub 补齐对应构建模块；Windows 还需要 Visual Studio 2019 或以上的 C++ Tools 及 Windows SDK `10.0.19041.0` 或以上，见 [Unity Windows 构建要求](https://docs.unity3d.com/6000.3/Documentation/Manual/windows-requirements-and-compatibility.html)。把构建成功和实际启动结果都记下来。包内测试的启用方式见[依赖前置条件](#依赖前置条件)，测试操作前置条件见[Unity 自动化与验证](unity-mcp-tips.md)。
5. **记录可复现基线**：在消费工程自己的当前说明中记录 Unity 版本、Framework SHA、目标平台和通过的验证范围；玩法、场景、字体、输入与项目规则留在消费工程。MCP 连接验收与游戏运行验收分别记录。

通过上述最小闭环后就可以开始玩法开发，再按实际需要接入资源构建、配置表或热更新。完整包依赖已安装，并不要求第一个玩法原型启用所有 Module 或配置所有外部工具链。

### 资源包与 Player 交付验收

使用 YooAsset 资源模块时，可在玩法开发前用一个预制体和一份文本走通下面的流程。普通 IL2CPP 与资源包可以独立组合；不做代码热更新时，先按[构建设置](#构建前选择是否启用热更新)关闭 HybridCLR 的 Enable。

1. **准备最小内容**：在消费工程创建资源目录，将预制体、材质和文本加入 YooAsset 收集器。配置包名与寻址规则；场景按地址加载这些资源，避免直接引用预制体，使测试能够证明内容来自资源包。
2. **对齐构建与运行配置**：在 **SSFramework → 构建与发布 → 资源构建** 创建或选择 `FrameworkAssetBuildProfile`，同步收集器中的包。验证全部离线随包内容时，首包策略选 **ClearAndCopyAll**，场景 `AssetUtility` 的默认包与列表使用相同包名，运行模式选 **Offline**。默认的“按标签内置”若没有填写标签，只会内置清单，不能用于这项完整离线验收。
3. **构建真实资源**：按需生成包名与构建常量，等待编译，再构建资源包。核对本轮版本、清单和 bundle 已进入 `Assets/StreamingAssets/yoo/<包名>`。原始构建输出在项目的 `AssetBuild/Bundles`；这些产物应由构建流程重新生成，是否入库由消费工程决定。
4. **Editor 读取构建产物**：临时将 Editor 运行模式也设为 **Offline**，验证初始化、加载、释放、再次加载与退出再进入 Play。`EditorSimulate` 直接读开发资产，适合日常迭代，但其成功不能代替这一步。
5. **构建并运行 Player**：资源构建成功后再构建目标平台 Player。除 Development Build 外，还应检查一次非 Development IL2CPP 构建，验证裁剪后的代码、资源版本、文本、材质和交互。运行时应携带完整 Player 目录；将产物复制或解压到另一个目录后再启动，可检查交付文件是否齐全。

**Package 的裁剪规则接入**：保留 `Game.Framework.Editor` 时，构建回调会自动收集目标平台 Framework Runtime asmdef 同目录的 `link.xml`，经 Source Catalog 解析后提交给 UnityLinker。生成文件位于 `Library/SSFramework/Linker/<平台>/link.xml`，Console 会列出本轮来源；无需手动复制到 Assets。该回调只处理 Framework Module 自有规则，不替第三方 Package 接管保留策略。Unity 不会自动采用 Package 内的 `link.xml`，如果裁掉 Framework Editor Module，需要由消费工程提供等价构建入口或 Assets 保留规则。[Unity 裁剪规则说明](https://docs.unity3d.com/6000.3/Documentation/Manual/managed-code-stripping-preserving.html)。

记录构建报告和实际运行结果，区分代码编译、资源构建、随包复制与运行加载各自是否成功。这个离线闭环不覆盖 Host/CDN 更新、HybridCLR 代码热更新或其他平台；选用这些能力时再补对应验收。

## 本地开发包

调试 Framework 源码时，可以通过 Package Manager 的本地路径引用工作副本。消费方只需要重新导入包即可看到修改；准备提交前仍应切换回明确的 tag 或 commit，避免把本地未提交状态当成版本依赖。

## 版本选择与发布

**让安装入口长期不变，与让安装内容自动追踪最新版本，是两件事。** 推荐保留固定的安装说明 / 工具下载入口，由维护者更新其中已验证的推荐版本；正式工程继续记录具体版本，方便复现问题与回退。

| Framework 地址形式 | 解析方式与适用场景 |
|---|---|
| Git 地址加 `#<完整 commit SHA>` 或已发布 tag | 指向已验证的版本，推荐用于游戏开发与构建；发布 tag 不再移动 |
| Git 地址加 `#main` | 指向 main 分支，适合主动参与集成验证的使用者 |
| `https://github.com/heroliss/SSFramework.git` | 首次解析时取远端默认分支 main 当时的最新提交，是手动安装的简便入口；检查并提交工程锁文件 |

省略 revision **不会持续自动更新**：UPM 将实际 Git commit 写入 `packages-lock.json`，已有工程按锁定结果加载。需要重新取分支最新内容时，可在 **Install package from git URL** 再次提交同一地址，UPM 会重新解析；随后检查变更并验收。工具默认使用发布标签，重复运行保留工程已有版本。行为依据见 [Unity Git 版本与锁定说明](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-git.html#git-locks)。

第三方依赖也保留版本声明。Framework 的 `package.json` 依赖值必须是具体 SemVer，不能写 `latest`、`*`、版本范围或第三方 Git URL；这是 [Unity 包清单规则](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-manifestPkg.html#dependencies)与 [Git 依赖限制](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-git.html)。在 **Install package by name** 界面留空 Version，是让 UPM 当次选择最新兼容发行版；它仍把选择写入工程清单并记录解析结果，不等于依赖从此没有版本。Unity 的兼容版本选择也不能代替 SSFramework 的实际验证，首次接入优先使用本文依赖基线。[按名称安装](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-ui-quick.html)、[工程清单](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-manifestPrj.html)。

### 合并 main 与发布的标准

不以“所有未来优化都做完”为合并条件。对准备合入的同一候选提交，在真实消费工程完成以下检查即可推进发布：

1. 审查源码、依赖声明与安装文档，完成 Unity 6.3 的依赖解析和 Runtime / Editor 编译。
2. 运行受影响的包内 EditMode / PlayMode 测试，并验证[最小运行路径](#安装验收)。
3. 以该场景完成 Windows x64 IL2CPP 构建并实际启动，记录 Unity 版本、候选 SHA、结果及未覆盖范围。
4. 检查结果后合入 main，发布对应最终提交、之后不再移动的 tag。若合并冲突改变代码或依赖，先复验受影响范围。

发布后同步推荐安装地址与安装工具默认标签。工具保留已有工程的版本，升级由使用者显式发起；后续候选也须完成上述验收，不能因工具成功写入清单就标为稳定版。

### v0.1.1 发布验证

2026-09-13，源码提交 `0d8e5b439a9e36b902330ef43358b3f86cc6853c` 在真实 Unity `6000.3.23f1` 消费工程完成：

- Editor **564 / 564**、PlayMode **769 / 769**，零失败、零跳过；含 15 项独立 YooAsset EditorSimulate 真实加载测试。
- 普通 Windows x64 IL2CPP Development Player 构建成功，BuildReport **0 错误 / 0 警告**。
- Player 在 RTX 3070 上启动；通过 UI 提交与点击事件验证 Command → System → Model → HUD 状态从 0 到 3，进程退出码 0。
- 安装工具的 30 组接入测试和 17 组包源测试分别在 Windows PowerShell 5.1 / PowerShell 7 通过，含真实消费工程的只读检查与清单副本写入验证。

发行提交仅追加安装入口与发布说明，Runtime / Editor / 测试源码与上述验证提交一致。此结果不覆盖 HybridCLR 热更新、YooAsset 离线 / Host 内容构建、全部渲染设备或大规模 ECS 仿真；真实鼠标 / 键盘体验仍由消费工程人工验收。

后续的真实离线 Player 回归发现：v0.1.1 通过 UPM 安装时，Package 内的保留规则没有进入 UnityLinker，Yoo Adapter 被裁剪，`AssetUtility` 启动时报“没有注册默认资源 Provider”。此问题已在 v0.1.2 修复；v0.1.1 的历史通过项不代表这条资源加载路径已通过。

### v0.1.2 发布验证

2026-09-13，在真实 Unity `6000.3.23f1` UPM 消费工程完成资源包与普通 IL2CPP 交付验收。Player 使用源码提交 `b07620359798f02e421f3a68b8f91a409bfa8c0d`；发行提交的 `src/` 与其一致，仅更新包版本、接入工具默认标签和文档。

- Editor 测试分批通过 **564 项既有测试 + 10 项新增裁剪回归，共 574 项**；15 项 YooAsset PlayMode 测试通过，均零失败、零跳过。v0.1.1 的 769 项 PlayMode 全量结果保留为历史证据，本次未声称重新全量运行。
- 实际构建响应文件包含 Framework 汇总的保留规则，裁剪后的输出保留 `Game.Framework.Asset.Yoo.dll`。消费业务 asmdef 虽声明了允许引用，但实际 Player DLL 的程序集引用表没有该 Adapter；Player 仍能发现注册并初始化。
- YooAsset 资源构建、全部内置复制和非 Development Windows x64 IL2CPP Player 构建成功，BuildReport **0 错误 / 0 警告**。交付 ZIP 解压后逐文件哈希与原文件一致。
- 解压后的 Player 在 RTX 3070 上以默认图形 API 启动，计数命令、Offline 包内文本与版本、URP 材质、加载 / 释放 / 再加载均通过，运行无错误、退出码 0。可见窗口截图经检查确认模型和界面正常显示；消费方也人工打开程序确认显示正常。
- 更新默认标签后的接入工具，30 组安装测试和 17 组包源测试分别在 Windows PowerShell 5.1 / PowerShell 7 通过，包含真实工程只读检查和清单副本写入验证。

使用完整 Framework Editor Module 的工程更新 UPM 包后即可获得修复，无需手动复制 `link.xml`。本轮不覆盖 HybridCLR 代码热更新、Host/CDN 更新、其他平台 / 显卡或大规模 ECS 仿真；选用这些能力时补对应验收。

### v0.1.3 接入改进与首次安装验证

v0.1.3 仅更新包版本、接入工具提示、消费工程 Git 模板与说明，`src/` 和第三方依赖声明与 v0.1.2 一致，沿用上面的目标 Player 验收。

2026-09-13，另用 Unity `6000.3.23f1` 创建独立空工程，使用公开 v0.1.2 工具 ZIP 配置包源与 C# 10，跳过 MCP，通过单独的空 UPM 全局缓存解析并下载依赖；没有复制已有游戏工程的场景、设置或包缓存。网络使用该验证进程的代理设置。额外编写的验证脚本修正两处命名冲突后通过：固定 Git revision、Framework 编译、业务 `record struct` / 关闭 Trace 的惰性求值、Context / Command / Event 链路、唯一 Provider 声明和 Package linker 回调均符合预期，Unity 退出码 0。

这轮实测同时确认：UPM 安装后工程根目录没有 `.gitignore`、`.gitattributes` 或 `AGENTS.md`。因此 v0.1.3 另提供可手动采用的 Git 模板，已在这个消费工程检查忽略与保留范围、换行属性和二进制资产保护；不自动覆盖项目规则。安装器的“待验收提交”旧提示和旧开发分支文档链接也已修正。

更新后的安装工具在 Windows PowerShell 5.1 / PowerShell 7 分别通过 30 组回归，包括对该独立消费工程的只读预览和清单副本写入检查。

此检查验证首次包安装与基础 API；不替代具体项目的场景、字体、输入、签名弹窗、热更新选择或 Player 构建验收。手动接入仍需配置 OpenUPM 和业务 `csc.rsp`，不做代码热更新时仍需关闭 HybridCLR 的 Enable。

## 版本升级流程

1. 阅读目标版本的 changelog、公共 API 变更和迁移说明，选定已发布的 tag 或已推送的完整 commit SHA。
2. 保存消费工程当前清单与锁文件。在 Package Manager 添加目标 Git URL，或修改 `Packages/manifest.json` 中的 revision，再由 UPM 重新解析并更新 `packages-lock.json`；不要把手改锁文件当成升级入口。
3. 复核工程自己显式指定的第三方版本，运行编译、受影响的测试、真实运行路径与必要的目标平台构建。
4. 把使用的 tag/SHA 与验证结果记录在消费方自己的当前说明中，并提交清单与锁文件；验证失败时回退这两份文件及相关业务迁移。

Framework 新提交不会自动改写使用它的 Unity 工程；批量升级可以由团队自己的脚本或 CI 编排，但每个消费方仍应显式记录版本和验证结果。

## 最小使用路径

1. 在场景中放置一个 `MonoGlobalContext` 子类作为根 Context。
2. 在 Context 子层级挂载 Model、System、Utility（由 Mono 基类自动注册），或在纯 C# 启动代码中注册这些服务。View 挂载后由 `MonoViewBase` 注入依赖并关联 Context，不注册进容器。
3. 让 View 通过 Command 表达用户意图；让 System 修改 Model 并发送 Event。
4. 让 View 订阅只读 `ReadOnlyReactiveProperty<T>` 或 Event，并把订阅交给 `DisposableBag` 管理。

详细 API 和生命周期语义见[框架使用指南](framework-guide.md)。

## 依赖前置条件

当前仓库将所有 Module 放在同一个 UPM 包中。下表统一列出根 [`package.json`](../package.json) 的直接依赖及声明版本；实际选择由 UPM 结合工程的其他依赖解析，结果以消费工程锁文件为准。安装完整包时，即使业务 asmdef 只引用 Core，其余无条件参与编译的 Module 仍需要满足自己的引用。

| UPM 包名 | 声明版本 | 来源 | 作用 |
|---|---|---|---|
| `com.cysharp.r3` | `1.3.1` | OpenUPM | R3 的 Unity 适配层，与 `org.nuget.r3` 配套 |
| `com.cysharp.unitask` | `2.5.11` | OpenUPM | 异步任务与 Unity 生命周期集成 |
| `com.code-philosophy.luban` | `1.2.0` | OpenUPM | 配置数据运行库 |
| `com.code-philosophy.hybridclr` | `8.14.1` | OpenUPM | Boot 与代码热更新构建所需的 Runtime、Editor 和自带 dnlib |
| `com.tuyoogame.yooasset` | `3.0.5` | OpenUPM | 资源 Adapter 与资源构建 |
| `org.nuget.r3` | `1.3.1` | OpenUPM | `R3.dll`，供 Core 与多个 Runtime Module 使用 |
| `org.nuget.observablecollections` | `3.3.4` | OpenUPM | UI 集合运行库 |
| `org.nuget.observablecollections.r3` | `3.3.4` | OpenUPM | UI 的 R3 集合绑定 |
| `org.nuget.google.protobuf` | `3.36.1` | OpenUPM | Network.Proto 的 Protobuf 运行库 |
| `org.nuget.microsoft.bcl.timeprovider` | `8.0.0` | OpenUPM | R3 与包内测试使用的时间提供器 |
| `com.unity.ugui` | `2.0.0` | Unity Registry | UGUI 与其 TextMeshPro 支持 |
| `com.unity.modules.uielements` | `1.0.0` | Unity 内置模块 | UI Toolkit 支持 |

`Microsoft.Bcl.AsyncInterfaces`、`System.Threading.Channels` 等传递依赖也通过 `org.nuget` Scope 自动安装。已有工程若曾通过 NuGetForUnity 或手动复制安装同名 DLL，应先整理为单一来源，避免重复程序集。

启用包内测试时，先在 Package Manager 核对工程已有适配当前 Unity 的 Test Framework；如缺少，从 Unity Registry 安装。在关闭 Editor 后，将 `"testables": ["com.liss.ssframework"]` 合并进工程 `Packages/manifest.json` 的顶层；若已有 `testables` 数组，只追加包名并保留其他项。重新打开工程后，在 **Window → General → Test Runner** 查看测试；`Game.Framework.Tests` 是 PlayMode 测试程序集，组件生命周期测试需要实际进入 PlayMode。Test Framework 不是游戏运行的前置依赖。测试使用 C# 10 的 `record struct` 与跨程序集插值处理器；旧包若尚未自带响应文件，应先按上面的业务配置步骤启用 C# 10，不能只通过删除语法来替代接入配置。

YooAsset 的真实加载测试只在 Editor PlayMode 运行：每例自动建立独占 Prefab、Sprite、场景副本和内存收集器，结束后恢复原收集器并删除临时资产。不要求导入旧 Collector XML、创建 `FrameworkSamplesPackage` 或手工修复测试 GUID。YooAsset 默认把模拟构建缓存与资源构建产物写入工程根 `Bundles/`，应将该生成目录加入消费工程的 `.gitignore`。Player 中的离线 / Host 内容构建与加载需由实际工程另行验收。

### 构建前选择是否启用热更新

**HybridCLR 当前候选包默认启用热更新。仅安装 UPM 包，还没有完成原生工具链安装时，其构建预处理会阻止 Player 构建。** 这与是否已经编写热更新业务代码无关。

- **暂不做热更新**：在 **Edit → Project Settings → HybridCLR Settings**（也可从 **HybridCLR → Settings** 进入）关闭 **Enable**。保留必需的 HybridCLR 包，业务启动使用直接注册服务的 Context 路径，不使用 `HotUpdateLauncher` 或依赖 HybridCLR 原生符号的 `RuntimeApi` 调用。关闭开关只表达构建选择，仍须用真实 IL2CPP 构建验证裁剪与启动路径。
- **需要热更新**：保持 Enable，在 **HybridCLR → Installer** 安装匹配当前 Unity 的原生工具链，配置热更新程序集并完成 Generate 流程；然后按框架的代码热更新工作台配置、构建与部署。升级 Unity 或 HybridCLR 后重新核对原生工具链与生成物。详细步骤见 [HybridCLR 官方包说明](https://www.hybridclr.cn/en/docs/8.5.0/basic/com.code-philosophy.hybridclr)。

Framework 和接入工具不自动更改这些项目设置。Luban / protoc 的外部生成工具和业务配置同样不由 DLL 依赖声明代替，使用对应生成功能时再配置。早期依赖验证证据见[审查记录](framework-review-2026-09-10.md#消费方接入复核)，完整模块边界见[模块地图](framework-module-map.md)。

## 兼容性边界

- 当前 Unity 基线：`6000.3.22f1`。
- Unity `6000.6` 当前被 YooAsset 3.0.5 Editor 的已移除 API 阻塞；框架自身的 EntityId 迁移不代表第三方包已经兼容。
- Package ID：`com.liss.ssframework`。
- `package.json` 中已声明的依赖由 Unity Package Manager 解析；额外构建工具链见上面的依赖前置条件。
- 场景、Prefab、业务资产和项目级 `ProjectSettings` 不属于 Framework 包。
- 需要替换第三方实现时，应接入公开 Interface/Adapter，不修改 `Library/PackageCache/`。

## 出现问题时先检查

- Package Manager 是否解析到了预期的 revision。
- 第三方包提示 `cannot be found` 时，先检查 OpenUPM URL、Scope 和网络连通性；包源可解析后若出现程序集缺失，再按依赖前置条件排查 DLL 和 HybridCLR。
- `Error searching for packages` / OpenUPM `ECONNRESET` 表示包源请求被中断，可能影响在线搜索或后续下载。先重试 Package Manager；持续发生时按 [Unity 网络配置说明](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-config-network.html)检查 UPM 所用网络与代理。工具预检成功只说明当前 PowerShell 请求可用，不能证明 Unity 的重试已成功。
- 搜索 `packages.unity.com` 返回 `504` 是 Unity 官方包源请求遇到网关超时，先稍后重试；它与 OpenUPM 是不同的请求目标，不应据此删除 Framework 依赖或重建工程。持续发生时继续检查服务状态与网络路径。
- `UnityConnectWebRequestException: Token Exchange failed` 来自 Unity 账号服务的令牌交换请求，见 [Unity 官方源码](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Editor/Mono/UnityConnect/ServiceToken/TokenExchange/TokenExchange.cs)。它可能影响需要账号的在线服务，本身不是 C# 编译失败；持续出现时检查网络及 Unity Hub 登录状态，必要时由使用者重新登录并重开 Editor。不要把它与 Framework 编译错误混为一项。
- Unity 版本和包依赖是否满足 `package.json`。
- Context 是否先于 Model/System/View 完成初始化。
- 订阅、异步操作、资源句柄和对象租借是否绑定到了正确的 Bag/Context 生命周期。
- 是否把应该留在业务工程的玩法状态或场景资产放进了 Framework。
