# 接入与升级 SSFramework

这份文档面向任何需要在 Unity 工程中使用 `com.liss.ssframework` 的团队。它只描述包本身的公开接入契约，不依赖某个游戏、教程工程或工作区布局。

## 首次接入：配置第三方包源

SSFramework 的 `package.json` 已声明下面四个第三方 UPM 依赖。消费工程配置 OpenUPM 后，添加 SSFramework 时 Unity 会自动解析和下载这些包，无需逐个手动安装。

在 **Edit → Project Settings → Package Manager → Scoped Registries** 中点击 **+**，填写：

- **Name**：`OpenUPM`
- **URL**：`https://package.openupm.com`
- **Scope(s)**：下表的四个包名，每个单独一行；通过 Scope(s) 下方的 **+** 添加行。

| Scope | 当前声明版本 |
|---|---|
| `com.cysharp.unitask` | `2.5.11` |
| `com.cysharp.r3` | `1.3.1` |
| `com.code-philosophy.luban` | `1.2.0` |
| `com.tuyoogame.yooasset` | `3.0.5` |

点击 **Save** 保存（修改已有 Registry 时为 **Apply**），再按下文添加 Framework 的 Git URL。如果此前出现这四个包 `cannot be found`，配置后可重试同一个 URL，不需要更换 Framework commit。若工程已配置 OpenUPM，只需补齐缺少的 Scope。

上述四个版本已于 2026-09-10 通过 OpenUPM Registry 元数据核实存在。这验证了包的来源和版本可查询，不代表完整框架已通过干净工程编译。Unity 自带依赖继续使用默认 Unity Registry，不需要把 `com.unity` 加入 OpenUPM Scope。

Registry 配置保存在**消费工程**的 `Packages/manifest.json` 中；Framework 的 `package.json` 不能代替工程设置 `scopedRegistries`。包内 Editor 安装脚本也不能作为解决首次依赖解析失败的前提。因此，当前流程是“每个工程配置一次包源，之后自动安装已声明的 UPM 依赖”。相关规则见 [Unity Scoped Registry 文档](https://docs.unity.cn/6000.6/Documentation/Manual/upm-scoped-use.html)。

**这一步只解决 UPM 包源问题。** `com.cysharp.r3` 提供 Unity 适配层，R3 运行库等预编译 DLL 以及 HybridCLR 仍需按[依赖前置条件](#依赖前置条件)补齐；当前版本尚不支持仅粘贴一个 Git URL 就完成全部外部依赖安装。

## 选择安装方式

### Unity Package Manager：Git URL

在 Package Manager 中选择 **Add package from git URL**：

```text
https://github.com/heroliss/SSFramework.git
```

稳定构建建议固定到 tag 或 commit：

```text
https://github.com/heroliss/SSFramework.git#<commit-sha>
```

Git URL 的 `#revision` 是可复现边界；不要在需要稳定验证的项目中隐式跟随未经审查的分支头。

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

当前仓库将所有 Module 放在同一个 UPM 包中。`package.json` 已声明部分 UPM 依赖，**尚未形成干净工程可独立安装的完整依赖集合**。安装完整包时，即使业务 asmdef 只引用 Core，其余无条件参与编译的 Module 仍需要满足自己的引用。

| 源码中的直接依赖 | 使用位置 | 消费方需确认 |
|---|---|---|
| `UniTask`、`R3.Unity`、`R3.dll` | Core 与多个 Runtime Module | UPM 来源可解析，R3 的预编译运行库及传递依赖齐全；仅有 Unity 适配层不等于 DLL 已安装 |
| `ObservableCollections.dll`、`ObservableCollections.R3.dll` | UI Core 与后端 | 提供匹配版本的运行库与 R3 适配 DLL |
| `Google.Protobuf.dll` | Network.Proto | 提供对应运行库及其传递依赖 |
| `HybridCLR.Runtime`、`HybridCLR.Editor`、`dnlib.dll` | Boot 与 Build.HybridCLR.Editor | 当前 package.json 未声明 HybridCLR；消费方需提供这些程序集及匹配的构建工具链 |
| `nunit.framework.dll`、`Microsoft.Bcl.TimeProvider.dll` | 包内测试 | 启用测试时提供 Unity Test Framework 与测试所需 DLL |

这些是源码可证明的直接引用，不是完整的传递依赖安装清单。版本、来源、许可证与安装/删除验证应在真实消费工程中锁定，再回流到包的分发方案；不要凭程序集名臆造 UPM 包名或版本。已安装工程可用模块审计核对真实 DLL 依赖，干净安装验收仍需实际编译。完整边界见[模块地图](framework-module-map.md)。

## 兼容性边界

- 当前 Unity 基线：`6000.3.22f1`。
- Package ID：`com.liss.ssframework`。
- `package.json` 中已声明的依赖由 Unity Package Manager 解析；其余外部程序集见上面的依赖前置条件。
- 场景、Prefab、业务资产和项目级 `ProjectSettings` 不属于 Framework 包。
- 需要替换第三方实现时，应接入公开 Interface/Adapter，不修改 `Library/PackageCache/`。

## 出现问题时先检查

- Package Manager 是否解析到了预期的 revision。
- 第三方包提示 `cannot be found` 时，先检查 OpenUPM URL、Scope 和网络连通性；包源可解析后若出现程序集缺失，再按依赖前置条件排查 DLL 和 HybridCLR。
- Unity 版本和包依赖是否满足 `package.json`。
- Context 是否先于 Model/System/View 完成初始化。
- 订阅、异步操作、资源句柄和对象租借是否绑定到了正确的 Bag/Context 生命周期。
- 是否把应该留在业务工程的玩法状态或场景资产放进了 Framework。
