# 接入与升级 SSFramework

这份文档面向任何需要在 Unity 工程中使用 `com.liss.ssframework` 的团队。它只描述包本身的公开接入契约，不依赖某个游戏、教程工程或工作区布局。

安装包不会自动配置消费工程根目录的 Git / AI 规则、生成完整项目目录或提供中文字体。现有能力与建议的初始化流程见[新项目准备](project-startup.md)。

**完整包当前以 Unity 6.3 LTS 为接入目标，版本基线为 `6000.3.22f1`。Unity 6.6 暂不能完整编译此包**：YooAsset 3.0.5 的 Editor 使用了 Unity 6.6 已移除的 `UxmlFactory` / `UxmlTraits`。补齐 DLL 或切换同版本的 Git 来源不能解决这个 API 不兼容；新项目应优先在 6.3 LTS 中创建，不要直接把已经由 6.6 保存的工程降级打开。参见 [Unity 6.6 API 移除说明](https://unity.com/releases/editor/alpha/6000.6.0a5)。

## 首次接入：配置第三方包源

SSFramework 的 `package.json` 已声明第三方 UPM 包和 NuGet 运行库的 UPM 分发包。消费工程配置 OpenUPM 后，添加 SSFramework 时 Unity 会自动解析和下载这些依赖，无需逐个安装 DLL 或额外引入 NuGetForUnity。

### 推荐：使用配置工具

Windows 使用者可以关闭目标 Unity 工程，双击仓库中的 [`Tools~/Configure-OpenUPM.cmd`](../Tools~/Configure-OpenUPM.cmd)，粘贴工程根目录，查看待补齐的 Scope 后输入 `y`。然后重新打开 Unity，亲自在 Package Manager 中添加下面的 Git URL。

工具在 Unity 之外运行，不依赖 Framework 先安装成功。它保留现有依赖和其他包源，只合并缺少的 Scope；写入前备份原清单，已配置完整时重复运行不改文件。包源冲突会报告并停止，不自动改选其他来源。命令行预览、恢复与验证入口见[工具说明](../Tools~/README.md)。`Tools~` 被 Unity 忽略，不会随包导入自动执行。

### 手动配置

在 **Edit → Project Settings → Package Manager → Scoped Registries** 中点击 **+**，填写：

- **Name**：`OpenUPM`
- **URL**：`https://package.openupm.com`
- **Scope(s)**：下表的六项，每个单独一行；通过 Scope(s) 下方的 **+** 添加行。

| Scope | 当前声明版本 |
|---|---|
| `com.cysharp.unitask` | `2.5.11` |
| `com.cysharp.r3` | `1.3.1` |
| `com.code-philosophy.luban` | `1.2.0` |
| `com.tuyoogame.yooasset` | `3.0.5` |
| `com.code-philosophy.hybridclr` | `8.14.1` |
| `org.nuget` | 覆盖下文运行库表及其传递依赖 |

点击 **Save** 保存（修改已有 Registry 时为 **Apply**），再按下文添加 Framework 的 Git URL。`org.nuget` 是命名空间 Scope，用来覆盖 NuGet 运行库及其多层依赖；其他五项是精确包名。Unity 自带依赖继续使用默认 Unity Registry，不需要把 `com.unity` 加入 OpenUPM Scope。

**从早期版本升级**：如果已经配置最初四项 Scope，请补充 `com.code-philosophy.hybridclr` 和 `org.nuget`，再将 Framework 更新到包含 `0.1.1` 依赖修复的 Git commit。旧 commit `182d967493c90b9632bc3a6e5b3ce95a7eb496b1` 没有声明这些新增依赖，仅补 Scope 不会让旧包自动安装它们。

Registry 配置保存在**消费工程**的 `Packages/manifest.json` 中；Framework 的 `package.json` 不能代替工程设置 `scopedRegistries`。包内 Editor 安装脚本也不能作为解决首次依赖解析失败的前提。因此，工具或手动操作负责“每个工程配置一次包源”，之后由 UPM 自动安装已声明的依赖。相关规则见 [Unity Scoped Registry 文档](https://docs.unity.cn/6000.3/Documentation/Manual/upm-scoped-use.html)。

`com.cysharp.r3` 提供 Unity 适配层，`org.nuget.r3` 提供 R3 运行库；两者都需要。已通过消费工程副本验证新增依赖可以自动解析，R3、HybridCLR 和 Framework 的 10 个运行时程序集可编译。完整包仍受上面的 Unity / YooAsset 版本兼容边界约束；不能把“依赖下载成功”视为完整 Editor、测试和 Player 构建均已通过。

## 选择安装方式

### Unity Package Manager：Git URL

在 Package Manager 中选择 **Add package from git URL**：

```text
https://github.com/heroliss/SSFramework.git#cc1ada645083e53e014b9a81d65567d24df3737f
```

上面是包含 `0.1.1` 依赖修复的待验收提交，尚未完成 Unity 6.3 全部测试，也尚未合入 main。当前不要使用省略 revision 的地址，否则可能装到缺少依赖声明的旧 main。其他已审查版本同样固定到 tag 或 commit：

```text
https://github.com/heroliss/SSFramework.git#<commit-sha>
```

Git URL 的 `#revision` 是可复现边界；不要在需要稳定验证的项目中隐式跟随未经审查的分支头。

#### 安装时出现 Missing signature

支持包签名验证的 Unity 版本可能在解析第三方依赖时弹出 **Missing signature**。这表示列出的包没有可验证的 UPM 签名；包已能被找到，但安装仍等待用户决定，后续还需完成编译验证。Unity 对缺少签名与签名无效的区分见[官方说明](https://discussions.unity.com/t/package-manager-changes-package-signing-and-status-labels/1688660)。

2026-09-10 的早期版本安装反馈中，窗口列出了 R3.Unity、UniTask、Luban 和 YooAsset，Source 均为 OpenUPM；新增依赖后也可能列出 HybridCLR。使用本文配置的 `https://package.openupm.com`，并确认包名、版本和来源符合预期后，可以点击 **Install Anyway** 继续安装。缺少签名本身不代表包损坏，也不证明内容安全。如果提示的是 **Invalid signature**，应另行排查来源和包内容，不能直接沿用缺少签名的处理结论。

保留 Registry 安装方式时，要消除缺少签名的提示，需要采用带有效 UPM 签名的依赖发行版，并验证升级兼容性。OpenUPM 已支持发布作者签名的包，但不会代替作者签名；仅给 SSFramework 自身签名不能覆盖独立分发的第三方依赖。发行机制见 [OpenUPM 签名文档](https://openupm.com/docs/signing-upm-packages)。

直接从 Git 安装是另一种来源选择。[Unity 团队于 2026-06-29 的说明](https://discussions.unity.com/t/unity-core-standards-new-features-and-what-s-next/1723174?page=2)指出，Git 和 Embedded 包当前不参与签名验证，因此不应触发自身缺少签名的提示；这不代表它们已获得 UPM 签名。Git 包仍可依赖来自 Registry 的包，后者会单独接受检查，这也是通过 Git 安装 SSFramework 时仍会看到上述窗口的原因。

若将第三方依赖改为各作者的 Git 来源，需要由消费工程显式声明包路径和固定 revision，并验证兼容性。Unity 仅允许在工程 `Packages/manifest.json` 中声明 Git 依赖，不支持在 Framework 的 `package.json` 中声明另一 Git 包，见 [Unity Git 依赖文档](https://docs.unity3d.com/6000.6/Documentation/Manual/upm-git.html)。当前接入流程使用 OpenUPM 自动解析依赖；尚未在消费方验证逐个改用 Git 的方案。安装体验的优化应优先补齐依赖分发与验证，不以关闭签名检查作为接入前提。

### 本地开发包

调试 Framework 源码时，可以通过 Package Manager 的本地路径引用工作副本。消费方只需要重新导入包即可看到修改；准备提交前仍应切换回明确的 tag 或 commit，避免把本地未提交状态当成版本依赖。

## 版本升级流程

1. 阅读目标版本的 changelog、公共 API 变更和迁移说明。
2. 在 Framework 仓库完成修改、包内测试和必要的构建验证。
3. 发布一个 tag，或选定一个已经推送的 commit SHA。
4. 在 Unity 工程中更新 Package Manager 的 Git revision，或更新包锁定文件中的版本。
5. 运行消费方编译、受影响的测试和真实运行路径。
6. 把使用的 tag/SHA 与验证结果记录在消费方自己的兼容性文档中。

Framework 新提交不会自动改写使用它的 Unity 工程；这是为了让每个工程的验证结果可复现。批量升级可以由团队自己的脚本或 CI 编排，但每个消费方仍应显式记录版本和验证结果。

## 最小使用路径

1. 在场景中放置一个 `MonoGlobalContext` 子类作为根 Context。
2. 在 Context 子层级挂载 Model、System、Utility（由 Mono 基类自动注册），或在纯 C# 启动代码中注册这些服务。View 挂载后由 `MonoViewBase` 注入依赖并关联 Context，不注册进容器。
3. 让 View 通过 Command 表达用户意图；让 System 修改 Model 并发送 Event。
4. 让 View 订阅只读 `ReadOnlyReactiveProperty<T>` 或 Event，并把订阅交给 `DisposableBag` 管理。

详细 API 和生命周期语义见[框架使用指南](framework-guide.md)。

## 依赖前置条件

当前仓库将所有 Module 放在同一个 UPM 包中。`package.json` 声明下列直接运行库，传递依赖由 UPM 解析。安装完整包时，即使业务 asmdef 只引用 Core，其余无条件参与编译的 Module 仍需要满足自己的引用。

| UPM 包 / 版本 | 提供的程序集 | 使用位置 |
|---|---|---|
| `org.nuget.r3` / `1.3.1` | `R3.dll` | Core 与多个 Runtime Module；与 `com.cysharp.r3` 配套 |
| `org.nuget.observablecollections` / `3.3.4` | `ObservableCollections.dll` | UI Core 与后端 |
| `org.nuget.observablecollections.r3` / `3.3.4` | `ObservableCollections.R3.dll` | UI 的 R3 集合绑定 |
| `org.nuget.google.protobuf` / `3.36.1` | `Google.Protobuf.dll` | Network.Proto |
| `org.nuget.microsoft.bcl.timeprovider` / `8.0.0` | `Microsoft.Bcl.TimeProvider.dll` | R3 与包内测试 |
| `com.code-philosophy.hybridclr` / `8.14.1` | `HybridCLR.Runtime`、`HybridCLR.Editor`、`dnlib.dll` | Boot 与 Build.HybridCLR.Editor；dnlib 由该包自带 |

`Microsoft.Bcl.AsyncInterfaces`、`System.Threading.Channels` 等传递依赖也通过 `org.nuget` Scope 自动安装。已有工程若曾通过 NuGetForUnity 或手动复制安装同名 DLL，应先整理为单一来源，避免重复程序集。启用包内测试时，工程还需提供对应 Unity 版本的 Test Framework 并在 `testables` 中加入 `com.liss.ssframework`。

HybridCLR 包安装完成只满足托管程序集引用；实际热更新 Player 构建还需要匹配的原生工具链安装与生成步骤。Luban / protoc 的外部生成工具和业务配置同样不由 DLL 依赖声明代替。依赖版本及消费方验证证据见[审查记录](framework-review-2026-09-10.md#消费方接入复核)，完整模块边界见[模块地图](framework-module-map.md)。

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
- Unity 版本和包依赖是否满足 `package.json`。
- Context 是否先于 Model/System/View 完成初始化。
- 订阅、异步操作、资源句柄和对象租借是否绑定到了正确的 Bag/Context 生命周期。
- 是否把应该留在业务工程的玩法状态或场景资产放进了 Framework。
