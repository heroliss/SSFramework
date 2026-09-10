# 🎮 SSFramework

**Unity 项目的模块化运行时与编辑器基础设施。**

SSFramework 将依赖注入、Context 作用域、Model / System / View 分层、Command / Event 数据流、响应式状态和生命周期管理放在同一套可追踪的运行时模型中；资源、UI、配置、网络、存储和构建工具则通过独立 Module 与 Adapter 接入。

它不规定游戏玩法、美术风格或项目目录。使用者可以从 Core 开始，按项目需要接入可选能力，并在需要替换第三方实现时保留自己的业务代码。

[📖 使用指南](docs/framework-guide.md) · [🧭 文档索引](docs/README.md) · [🔧 接入与升级](docs/consuming-framework.md) · [🧾 架构决策](docs/adr/README.md)

![SSFramework 架构图](docs/SSFramework-architecture.png)

## ✨ 核心特点

| 设计 | 能解决的问题 |
|---|---|
| **Context 组合根** | 把依赖注册、解析、事件总线、命令分发和取消边界集中到可命名的作用域中；场景、功能和测试可以各自拥有清楚的边界。 |
| **Model / System / View + Command** | View 表达“要做什么”，Command 连接意图与受限上下文，System 执行规则并修改 Model；复杂逻辑不会逐渐堆成一个巨型 MonoBehaviour。 |
| **类型化的层权限** | IModel、ISystem、IUtility、IView、ICommand 等接口表达访问方向，常见的越权会在编译期或注入期暴露。 |
| **状态与事件分工** | 有当前值、需要晚加入者立即读取的内容使用 RP<T> / ReadOnlyReactiveProperty<T>；只表示“某件事发生”的瞬时通知使用 IEvent。 |
| **单向、可追踪的数据流** | 常见路径是 View → Command → System → Model，变化再经响应式属性或 Event 返回 View；每个写入点都有明确的代码入口。 |
| **层级与平行 Context** | 子 Context 可继承父级服务，也可以保持自己的状态和事件范围；不同功能可以共享基础设施，同时避免互相读取局部状态。 |
| **Mono 与纯 C# 双路径** | 需要 Inspector、Hierarchy 和 Unity 生命周期时使用 Mono*Base；纯规则、服务和测试可以直接使用 C# 类型与 GameContext。 |
| **可替换的 Command 分发器** | ICommandSystem 是基础设施接缝，不是业务五层中的 ISystem；可替换为日志、回放、撤销、优先级或调试装饰器。 |
| **值类型 Command 路径** | 同步和异步 Command 都提供双泛型重载；readonly struct Command 可避免装箱，class Command 则可按需使用 [Inject]。 |
| **统一的生命周期 Bag** | 订阅、异步操作、资源句柄和对象租借可以登记到 DisposableBag；宿主销毁或 Context 释放时统一清理，并继续处理后续清理项。 |
| **取消沿宿主边界传递** | Context、Mono View 和调用方可以组成清晰的取消边界；异步 Command 收到已经决定好的 token，并在完成、失败或取消后回到 Unity 主线程交付。 |
| **资源与服务的 Adapter 边界** | 资源加载、存储、网络、日志、音频、配置和 UI 后端以 Interface / Adapter 连接，第三方类型不会反向渗入 Core 业务规则。 |
| **可裁剪的程序集边界** | 可选能力按 Runtime / Editor / Tests 划分程序集，依赖方向和删除阻塞记录在模块地图中；项目不需要的能力可以单独评估。 |
| **渲染中立的 UI 编排** | 窗口层级、栈、模态、过渡和增量列表绑定位于 UI Core，UGUI、UI Toolkit 和桥接能力分别作为后端接入。 |
| **编辑器工具有稳定入口** | 模块审计、配置与生成工具、资源构建、热更新构建和诊断窗口通过注册表接入通用工具中心；删除可选 Module 后对应入口会自然消失。 |
| **面向证据的工程接口** | 测试、预检、模块依赖报告和隔离体积探针都能留下可复核结果；“源码存在、参与编译、被消费、进入 Player”不会被混成一个状态。 |

这些设计的共同目标是让代码更容易定位和替换：新功能有明确落点，View 不需要了解基础设施实现，第三方升级的影响面可见，纯规则可以在不启动完整场景的情况下验证。

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

## 🚀 快速开始

### 1. 安装包

在 Unity Package Manager 中选择 **Add package from git URL**，输入：

```text
https://github.com/heroliss/SSFramework.git
```

需要可复现构建时，把 URL 固定到已经审查过的 commit SHA：

```text
https://github.com/heroliss/SSFramework.git#<commit-sha>
```

<commit-sha> 需要替换成实际提交；当前包没有假定某个固定 tag。开发框架本身时，也可以使用 Package Manager 的本地路径方式引用工作副本。当前 package.json 尚未覆盖完整包的全部依赖：HybridCLR 和部分预编译 DLL 需要消费方提供。安装前先检查[依赖前置条件](docs/consuming-framework.md#依赖前置条件)，并确认所需 Registry、Git 或本地依赖可被解析。

### 2. 为业务程序集显式引用 Framework

Core 与可热更新 Runtime 程序集使用 autoReferenced:false，业务 asmdef 应明确引用需要的程序集；AOT 启动薄壳 Game.Framework.Boot 是 autoReferenced:true 的例外。最小运行时通常引用 Game.Framework；使用 UI、YooAsset、Luban、Protobuf 或构建工具时，再按模块地图添加对应程序集。显式引用控制业务访问关系，不会让完整包里未被引用的 Module 自动停止编译。

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

public readonly struct PlayerHurtEvent : IEvent
{
    public readonly int Amount;
    public PlayerHurtEvent(int amount) => Amount = amount;
}

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

在场景中创建一个 MainContext，将 PlayerModel、PlayerSystem 和 HudView 放到它的子层级，并在 Inspector 中绑定文本和按钮。实际项目应把这些类型放进自己的 asmdef；完整的 Context、生命周期、异步 Command 和 UI 接入说明见[框架使用指南](docs/framework-guide.md)。

## 🧰 能力模块

| 模块 | 内容 | 适用边界 |
|---|---|---|
| **Core** | Context、容器、Model / System / Utility / View、Command / Event、响应式属性、生命周期、对象池、异步取消、存储 / 音频 / Flow / 本地化 / 日志 / 网络的稳定 Interface | 所有项目的基础运行时；不包含具体游戏玩法 |
| **基础依赖** | R3 提供响应式数据流，UniTask 提供异步任务与取消协作；Unity 原生 UI、Editor 和序列化能力通过公开边界接入 | 业务可以直接使用这些基础能力，但不需要把第三方类型扩散到所有模块 |
| **Asset.Yoo** | IAssetProvider 的 YooAsset Adapter、资源引用和运行时装配 | 项目选择 YooAsset 时接入；Core 不保存 YooAsset 类型 |
| **UI** | 渲染中立的窗口、层级、栈、模态、过渡和响应式列表绑定 | 业务 UI 的编排与生命周期 |
| **UI.UGui / UI.Toolkit / UI.Bridge** | UGUI、UI Toolkit 和内容嵌入桥的后端实现 | 只安装项目实际使用的渲染后端 |
| **Config / Network.Proto** | 配置运行时、Luban Editor 工具、Protobuf 序列化 Adapter 和生成入口 | 配置表与协议生成属于项目构建链 |
| **Fonts** | TMP 多语言字体 fallback、常用字集工具和相关测试 | 有多语言字体链需求时接入 |
| **Build / Boot** | 资源构建、HybridCLR 热更新构建、CodePackage 和启动薄壳 | 仅在项目采用对应发布链时接入 |
| **Editor** | 原生 Drawer / Inspector、诊断、模块审计、工具注册、输出声明和隔离体积探针 | 只编译到 Editor，不进入玩家运行时 |

模块职责、程序集引用和删除测试以 Framework 模块地图为准；第三方依赖的版本和来源以 package.json、项目 manifest 与锁文件为准。

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
