# 🎮 SSFramework

**Unity 项目的模块化运行时与编辑器基础设施。**

SSFramework 将依赖注入、Context 作用域、Model / System / View 分层、Command / Event 数据流、响应式状态和生命周期管理放在同一套可追踪的运行时模型中；资源、UI、配置、网络、存储和构建工具则通过独立 Module 与 Adapter 接入。

它不规定游戏玩法、美术风格或项目目录。使用者可以从 Core 开始，按项目需要接入可选能力，并在需要替换第三方实现时保留自己的业务代码。

[📖 使用指南](docs/framework-guide.md) · [🧭 文档索引](docs/README.md) · [🔧 接入与升级](docs/consuming-framework.md) · [🧾 架构决策](docs/adr/README.md)

![SSFramework 架构图](docs/SSFramework-architecture.png)

## ✨ 核心特点

| 设计 | 能解决的问题 |
|---|---|
| **清晰的业务分层** | Model 保存状态、System 执行规则、View 表达交互意图，Command 连接操作与规则；各层通过类型化接口约束访问方向。 |
| **可追踪的数据流** | 用户操作沿 View → Command → System → Model 传递，变化再经响应式状态或事件反馈到界面，便于定位行为的来源。 |
| **有边界的功能组织** | Context 管理依赖与生命周期，支持层级和并列作用域；场景、功能和测试可以共享服务并保留自己的局部状态。 |
| **统一的资源清理** | 订阅、资源句柄和对象租借可交给 DisposableBag 管理，异步操作可随宿主生命周期取消，减少遗漏清理。 |
| **Mono 与纯 C# 双路径** | 组件使用 Unity 的 Inspector 和生命周期；纯规则与服务可以使用普通 C# 类型，便于独立测试。 |
| **模块与第三方实现可替换** | 资源、UI、配置、网络等能力通过独立模块与 Adapter 接入，业务按需引用，依赖与裁剪边界有文档可查。 |
| **集中的编辑器工具** | 配置生成、资源构建、热更新构建和诊断通过通用工具中心提供入口，便于发现和使用。 |
| **支持人工与自动化验证** | 包内测试、运行前检查和模块依赖报告提供可复核结果，可接入 Unity MCP、CI 或其他自动化工具。 |

这些设计的共同目标是让代码更容易定位和替换：新功能有明确落点，View 不需要了解基础设施实现，第三方升级的影响面可见，纯规则可以在不启动完整场景的情况下验证。各层 API、Command 分发与性能细节见[框架使用指南](docs/framework-guide.md)。

## 📐 架构与数据流

```text
依赖 / 生命周期：  Context ──→ Container ──→ Model / System / Utility / View
                                     │
用户操作：          View ──→ Command ──→ System ──→ Model
                                      │          ├→ RP<T> ──→ View
                                      │          └→ IEvent ─→ View / 音效 / 动画
```

- **Context** 是作用域和组合根，负责依赖解析、事件范围、命令分发和生命周期取消。
- **Model** 保存可以持续查询的状态；**System** 修改状态、协调规则和发送事件。
- **View** 观察只读状态、监听事件并发送 Command，不直接写 Model，也不发送 Event。
- **Command** 通过 ICommandContext 获取允许的 Model / System / Utility，避免为了一个动作拿到完整容器。
- **RP<T>** 适合有当前值的状态；Event 不保存历史，也不会向新订阅者回放过去消息。

事件和响应式状态都应交给宿主的 DisposableBag 管理。Context 释放时会取消其生命周期 token，MonoViewBase 销毁时会释放自己的 Bag；业务代码仍需对自己拥有的外部资源负责。

## 🧰 能力模块

| 模块 | 内容 | 适用边界 |
|---|---|---|
| **Core** | Context、容器、Model / System / Utility / View、Command / Event、响应式属性、生命周期、对象池、异步取消、存储 / 音频 / Flow / 本地化 / 日志 / 网络的稳定 Interface | 所有项目的基础运行时；不包含具体游戏玩法 |
| **基础依赖** | R3 提供响应式数据流，UniTask 提供异步任务与取消协作；Unity 原生 UI、Editor 和序列化能力通过公开边界接入 | 业务可以直接使用这些基础能力，但不需要把第三方类型扩散到所有模块 |
| **Asset.Yoo** | IAssetProvider 的 YooAsset Adapter、资源引用和运行时装配 | 项目选择 YooAsset 时接入；Core 不保存 YooAsset 类型 |
| **UI** | 渲染中立的窗口、层级、栈、模态、过渡和响应式列表绑定 | 业务 UI 的编排与生命周期 |
| **UI.UGui / UI.Toolkit / UI.Bridge** | UGUI、UI Toolkit 和内容嵌入桥的后端实现 | 业务只引用和装配实际使用的后端；当前仍随完整 UPM 包分发 |
| **Config / Network.Proto** | 配置运行时、Luban Editor 工具、Protobuf 序列化 Adapter 和生成入口 | 配置表与协议生成属于项目构建链 |
| **Fonts** | TMP 多语言字体 fallback、常用字集工具和相关测试 | 有多语言字体链需求时接入 |
| **Build / Boot** | 资源构建、HybridCLR 热更新构建、CodePackage 和启动薄壳 | 仅在项目采用对应发布链时接入 |
| **Editor** | 原生 Drawer / Inspector、诊断、模块审计、工具注册、输出声明和隔离体积探针 | 只编译到 Editor，不进入玩家运行时 |

模块职责、程序集引用和删除测试见[模块地图](docs/framework-module-map.md)；第三方依赖的版本和来源以 package.json、项目 manifest 与锁文件为准。

## 🚀 快速开始

### 1. 安装包

完整包当前以 **Unity 6.3 LTS** 为接入目标；**Unity 6.6 暂被 YooAsset 3.0.5 的 Editor API 兼容问题阻塞**。安装前准备 Git，并确认能访问 GitHub、Unity Registry 和 OpenUPM。

两种方式使用相同的框架与依赖，选择其中一条即可。手动方式完全不需要运行安装工具；也可以只让工具配置包源，再手动添加框架。

| 方式 | 适合的情况 | 完整说明 |
|---|---|---|
| 手动接入 | 希望理解每项包源、依赖与可选工具，或需要自己管理版本 | [手动安装步骤](docs/consuming-framework.md#手动安装无需工具) |
| 自动接入工具 | 希望一次预览、配置并备份工程清单 | [工具使用、默认项与恢复](Tools~/README.md) |

#### 方式 A：手动接入

1. 打开 Unity 工程，在 **Edit → Project Settings → Package Manager → Scoped Registries** 添加 `OpenUPM`，URL 填 `https://package.openupm.com`。按[包源配置](docs/consuming-framework.md#1-手动配置包源)添加六项 Scope 并保存；已有配置只补缺项。
2. 必需依赖通常由 UPM 随框架自动解析。如果希望逐个安装和检查，在 Package Manager 中使用 **Install package by name / Add package by name**，按[依赖清单与手动步骤](docs/consuming-framework.md#依赖前置条件)填写包名及固定版本。无需再复制 DLL 或安装 NuGetForUnity。
3. 在 Package Manager 中选择 **Install package from git URL / Add package from git URL**，输入以下地址：

```text
https://github.com/heroliss/SSFramework.git
```

4. 等待依赖解析、签名确认和编译完成，再按需手动安装 [Unity MCP、Odin 等可选开发工具](docs/consuming-framework.md#可选开发工具)。这些工具不影响 Framework 是否必须安装；YooAsset、HybridCLR 等根包依赖目前仍随完整框架安装，不能取消。
5. 完成[安装验收](docs/consuming-framework.md#安装验收)：核对编译、业务程序集与最小场景，再测试和构建。**不做热更新时仍需显式关闭 HybridCLR 的 Enable**；安装其 UPM 包与启用热更新构建是两项不同的选择。

#### 方式 B：自动接入工具

1. 下载并完整解压[接入工具 ZIP](https://github.com/heroliss/SSFramework/releases/latest/download/SSFramework-Setup.zip)。无需先安装 Framework，也无需克隆整个仓库。
2. 关闭目标 Unity 工程，双击 `Setup-SSFramework.cmd`，输入工程目录，选择 Framework 安装方式、可选 MCP，以及是否创建缺失的 Git / AI 规则；每一步都显示推荐默认项，已有规则保留。
3. 核对变更预览与联网检查，输入 `y` 才应用并备份。默认自动加入固定 Framework Git 地址和所选包；手动模式只显示相应安装指引。
4. 打开 Unity，等待 UPM 解析、签名确认与编译；自动模式无需再次粘贴 Git 地址。可运行 `Check-SSFrameworkProject.cmd` 做[只读配置自检](Tools~/README.md#安装后配置自检只读)，再完成与手动方式相同的[安装验收](docs/consuming-framework.md#安装验收)。

Scope 明细、MCP 连接命令和来源可通过 `-Details` 查看；命令行、保护机制和撤销方法见[工具说明](Tools~/README.md)。工具不会配置渲染管线、生成业务场景或自动完成 Player 构建。

新工程可通过工具或手动采用[消费工程 Git 模板](Tools~/README.md#消费工程的-git-配置)和[AI 协作入口模板](Tools~/README.md#项目-ai-协作入口)。需要中文显示时，可另行导入[可选简体中文字体包](docs/starter-fonts.md)，直接使用 TMP / UI Toolkit 资产。UPM 安装本身不会应用这些项目文件或字体；完整项目目录由项目按需准备。

#### 版本固定与升级

上面的普通 Git 地址解析默认分支 main，UPM 用锁文件记录实际提交，不会随每次启动自动升级。希望固定本次发行版时使用下面的地址；接入工具默认也使用这个标签：

```text
https://github.com/heroliss/SSFramework.git#v0.1.5
```

发布标签不再移动；也可使用 `#<完整 commit SHA>` 固定其他已审查提交。开发框架本身时，可以使用 Package Manager 的本地路径方式引用工作副本。从早期版本升级到 `0.1.1` 时，还需补充 `org.nuget` 和 `com.code-philosophy.hybridclr` 两项 Scope。安装前先检查[依赖前置条件](docs/consuming-framework.md#依赖前置条件)；自己的场景、平台及所用内容构建链仍须在消费工程验收。

### 2. 配置业务程序集

#### 语言版本：C# 10.0

Framework 与本文示例默认使用 **C# 10.0**：`record struct` 简化事件等数据类型的声明，日志插值处理器让被关闭的 Trace 日志跳过消息构造。

包内各程序集已自带编译配置；业务工程使用自动接入工具时会配置，手动接入时需按[说明添加 `csc.rsp`](docs/consuming-framework.md#c-10-默认约定与原因)。请在运行下方示例前完成业务程序集的语言配置。语言版本设置不会升级 Unity 的 .NET 运行库；选择原因、配置作用范围和兼容性限制也见该说明。

#### 显式引用所需程序集

Core 与可热更新 Runtime 程序集使用 autoReferenced:false，业务 asmdef 应明确引用需要的程序集；AOT 启动薄壳 Game.Framework.Boot 是 autoReferenced:true 的例外。最小运行时通常引用 Game.Framework；使用 UI、YooAsset、Luban、Protobuf 或构建工具时，再按模块地图添加对应程序集。显式引用控制业务访问关系，不会让完整包里未被引用的 Module 自动停止编译。

下面的按钮示例还直接使用 R3、TMP 和 UGUI。可在自己的业务目录创建 `Game.Main.asmdef`，使用以下引用配置，并将示例脚本放在它的目录范围内：

```json
{
  "name": "Game.Main",
  "references": [
    "Game.Framework",
    "R3.Unity",
    "UniTask",
    "Unity.TextMeshPro",
    "UnityEngine.UI"
  ],
  "overrideReferences": true,
  "precompiledReferences": ["R3.dll"]
}
```

这是本文示例的引用集合；后续使用其他 Module 时再添加相应引用。已用 Unity `6000.3.23f1` 消费工程中的实际程序集离线编译此示例；场景接线、Play 和 Player 仍需在工程中验证。

### 3. 建立 Context、状态和规则

下面的示例展示一条最小的“按钮造成伤害、状态刷新 UI”路径。示例中的类型属于消费方程序集，Framework 包本身不会替项目生成这些业务类型。

```csharp
using System;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Event;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.View;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class MainContext : MonoGlobalContext
{
    protected override void InstallBindings(ContainerBuilder builder)
    {
        builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
    }
}

public sealed class PlayerModel : MonoModelBase
{
    [field: SerializeField]
    public RP<int> Health { get; private set; } = new(100);
}

public interface IPlayerSystem : ISystem
{
    ReadOnlyReactiveProperty<int> Health { get; }
    void TakeDamage(int amount);
}

public record struct PlayerHurtEvent(int Amount) : IEvent;

public sealed class PlayerSystem : MonoSystemBase, IPlayerSystem
{
    [Inject] private PlayerModel _model;

    ReadOnlyReactiveProperty<int> IPlayerSystem.Health => _model.Health;

    public void TakeDamage(int amount)
    {
        _model.Health.Value = Math.Max(0, _model.Health.Value - amount);
        this.SendEvent(new PlayerHurtEvent(amount));
    }
}

public readonly struct TakeDamageCommand : ICommand
{
    public void Execute(ICommandContext context)
        => context.GetSystem<IPlayerSystem>().TakeDamage(10);
}

public readonly struct GetHealthCommand : ICommand<ReadOnlyReactiveProperty<int>>
{
    public ReadOnlyReactiveProperty<int> Execute(ICommandContext context)
        => context.GetSystem<IPlayerSystem>().Health;
}

public sealed class HudView : MonoViewBase
{
    [SerializeField] private TMP_Text _healthText;
    [SerializeField] private Button _damageButton;

    protected override void Awake()
    {
        base.Awake();

        var health = this.ExecuteCommand(new GetHealthCommand());
        Bag.Subscribe(health, value => _healthText.text = value.ToString());
        Bag.Subscribe(_damageButton.onClick,
            () => this.ExecuteCommand(new TakeDamageCommand()));
    }
}
```

示例为集中展示；实际创建脚本时，将 `MainContext`、`PlayerModel`、`PlayerSystem` 和 `HudView` 各自保存到同名 `.cs` 文件，保留所需 using，才能分别挂载组件。接口、事件和 Command 可放在同一个普通 C# 文件中。

在场景中创建一个 MainContext，将 PlayerModel、PlayerSystem 和 HudView 放到它的子层级，在 Canvas 中创建 TMP 文本和按钮，并在 Inspector 中绑定引用。新 Input System 工程的 EventSystem 使用 InputSystemUIInputModule，避免按钮只显示却收不到输入。保存场景，进入 Play 后点击按钮，应看到数值从 100 逐次减 10。实际项目应把这些类型放进自己的 asmdef；完整的 Context、生命周期、异步 Command 和 UI 接入说明见[框架使用指南](docs/framework-guide.md)。

## 🤖 AI 友好与验证 Harness

SSFramework 把“让工具能找到事实”作为工程能力的一部分，但不绑定某个 AI 客户端：

- 根目录 AGENTS.md、src/AGENTS.md、模块地图、使用指南和 ADR 分别记录协作边界、源码规则、模块依赖和设计取舍。
- Editor 工具注册表、源码目录 Catalog 和命令元数据 Catalog 能把工具入口、类型标识、Package 路径和物理文件位置连接起来；诊断信息可以继续定位到源码，而不是只显示一段字符串。
- FrameworkAutomationPreflight 在自动化启动 Unity EditMode / PlayMode 测试前，显式保存有路径的脏场景，拒绝未命名脏场景、编译中、PlayMode 和其他不可安全运行的状态，并输出机器可读的 READY / BLOCKED 结果。它不会改写第三方 Test Runner，也不会替人工点击 Play。
- FrameworkModuleAudit 结合 asmdef、编译图、DLL 引用、场景 / 资源 / link.xml 和热更新配置，报告“为什么仍被保留”；它是证据整理工具，不把 autoReferenced:false 误解为自动裁剪。
- FrameworkBuildSizeProbe 在隔离子工程中按选择的 Module 组合生成构建报告，结果用于同一环境下的相对比较和依赖调查，不直接承诺最终压缩包体。
- 包内 Runtime / Editor / UI / Adapter 测试覆盖正常路径、边界条件、异常隔离、取消、生命周期、依赖方向和删除边界；宿主工程仍应为自己的场景、平台和发布链补充验证。

这些能力可以由 Unity MCP、CI 或其他自动化宿主调用；宿主的 CLI 适配脚本不属于 Framework Package，本 README 也不要求项目采用某一个客户端。

## 🧪 验证方式

`v0.1.2` 已在真实 Unity `6000.3.23f1` 消费工程通过 **574 项 Editor 测试（564 项既有测试 + 10 项新增裁剪回归，分批运行）和 15 项 YooAsset PlayMode 测试**。资源包与非 Development Windows x64 IL2CPP Player 已完成构建、解压运行、交互、Offline 加载 / 释放 / 再加载及可见画面验收。具体环境、历史结果与未覆盖范围见[接入指南](docs/consuming-framework.md#v012-发布验证)。

`v0.1.3` 的框架源码与上述版本一致，补充消费工程 Git 模板并修正安装指引。另以独立 Unity 6.3 空工程和单独的空 UPM 缓存验证了首次安装及基础 API；流程与边界见[首次安装验证](docs/consuming-framework.md#v013-接入改进与首次安装验证)。

`v0.1.4` 补充安装后只读自检及可选 Git / AI 文件初始化，已验证真实消费工程的文件保留和工具回归。框架源码与依赖继续保持一致；文件检查不会代替 Unity 编译、场景或 Player 验收，详见[工具验证范围](docs/consuming-framework.md#v014-配置自检与可选项目初始化)。

`v0.1.5` 候选增加独立可选的简体中文字体资源，Editor 显示和普通 IL2CPP 构建已通过；可见 Player 画面仍待验收，完成后再发布，见[字体验证范围](docs/consuming-framework.md#v015-可选中文字体资源)。

接入或修改框架后，按改动涉及的范围选择验证方式：

| 改动类型 | 建议证据 |
|---|---|
| 纯 C# 规则、Command、Model 或 Utility | 运行对应 Runtime / EditMode 测试，检查异常、取消和所有权语义 |
| Mono 生命周期、Context、View 或 UI 后端 | 运行包内 PlayMode / UI 测试，再在真实消费工程走一次场景路径 |
| asmdef、Adapter 或第三方依赖 | 运行架构与删除测试，查看模块审计中的编译图和外部依赖证据 |
| 构建、热更新或包体结构 | 运行目标平台构建和隔离体积探针；把 Player BuildReport 与探针报告分开记录 |
| 自动化测试入口 | 先调用 FrameworkAutomationPreflight.PreparePlayModeTests() 或对应菜单，再启动 Test Runner；遇到 BLOCKED 先处理现场状态 |

## 🧱 目录与文档

```text
package.json                 # Package ID、版本、Unity 基线与依赖
src/Core/                    # Runtime Core 与稳定 Interface
src/UI/                      # UI Core
src/Asset.Yoo/               # YooAsset Adapter
src/Config/                  # 配置运行时与 Editor 工具
src/Network.Proto/           # Protobuf Adapter 与生成工具
src/Build/                   # 资源 / 热更新构建工具
src/Editor/                  # 通用 Editor 工具、审计与诊断
src/**/Tests/                # 与能力同目录的测试
docs/framework-guide.md      # API 与生命周期教程
docs/framework-module-map.md # 程序集、依赖方向和删除边界
docs/consuming-framework.md  # 安装、固定版本和升级检查
docs/adr/                    # 关键架构决策
```

- [框架使用指南](docs/framework-guide.md)：从理念、Context 到各层 API。
- [接入与升级说明](docs/consuming-framework.md)：Git / 本地包、版本固定和升级步骤。
- [架构决策记录](docs/adr/README.md)：公共契约背后的背景、取舍与影响。
- [命名约定](docs/naming-conventions.md)：Package、程序集和 C# 命名边界。
- [可选 Odin 集成](docs/optional-odin-integration.md)：在不把商业插件变成核心依赖的情况下接入 Odin。
- [许可证](LICENSE)：SSFramework 自有代码使用 MIT；第三方包、字体、插件和示例资产仍受各自许可证约束。

修改公共 API、模块依赖或自动化入口前，请同时更新受影响的文档和测试。规则、验证范围和证据口径以仓库中的 AGENTS.md、源码、测试和文档为准。
