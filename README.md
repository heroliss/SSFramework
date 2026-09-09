# SSFramework

**让 Unity 业务保持清晰、可替换、可测试。**

SSFramework 是一个面向长期生产的 Unity 模块化框架。它把游戏中最容易失控的几类基础问题——依赖到处寻找、View 直接修改状态、生命周期清理遗漏、第三方库侵入核心、场景逻辑难以测试——收敛到一套有边界的运行时模型中。

框架不规定你的游戏玩法，也不替你决定美术、数据或项目目录。它提供一组稳定的连接方式，让团队可以用自己熟悉的 Unity 工作流搭建业务，同时获得清晰的数据流、明确的依赖关系和可回归的测试入口。

![SSFramework 架构图](docs/SSFramework-architecture.png)

## 为什么选择 SSFramework

| 设计 | 实际收益 |
|---|---|
| **Context 组合根** | 依赖集中注册，Context 层级表达作用域；场景、功能和测试可以拥有清楚的边界。 |
| **Model / System / View + Command** | View 表达用户意图，System 承担规则，Model 持有状态；业务不会因为一个 MonoBehaviour 变成无法维护的巨型控制器。 |
| **类型化权限接口** | 通过 `IModel`、`ISystem`、`IUtility`、`ICommand` 等契约限制调用方向，让常见越界在编译期就可见。 |
| **响应式属性与事件分工** | 有当前值的状态用 `RP<T>`，瞬时发生用 Event；UI 能同时得到初值和后续变化，不必自己拼接两套通知机制。 |
| **Mono 与纯 C# 双路径** | 需要 Inspector 和场景生命周期时使用 `MonoXxxBase`；纯规则和服务可以脱离 Unity 编写，便于单元测试和批处理。 |
| **Bag 生命周期管理** | 订阅、异步操作、资源句柄和对象租借可以绑定到宿主生命周期，销毁时统一释放，减少泄漏与重复清理。 |
| **可替换的基础设施接缝** | 资源、存储、网络、日志、UI、配置和对象池通过 Interface/Adapter 隔离；替换实现不必改动业务规则。 |
| **可删除的模块边界** | 可选能力各自拥有程序集、依赖和测试边界；项目可以按实际需要裁剪，而不是被一整套工具链锁定。 |
| **面向验证的 API** | 公共逻辑可以放进纯 C# 测试，Unity 侧保留真实场景验证；问题更早暴露，重构更有底气。 |

这些设计组合在一起，带来的便利不只是“少写几行样板代码”：新功能更容易找到应该放置的位置，替换第三方实现时影响面更小，View 和业务规则可以并行开发，测试也不必每次都启动完整场景。

## 核心数据流

一次按钮操作通常沿着下面的路径流动：

```text
用户操作 → View → Command → System → Model
                                      ├→ ReactiveProperty → View
                                      └→ Event            → View / 音效 / 动画
```

- **Command** 描述“要做什么”，适合离散的用户意图；推荐使用 `readonly struct`，避免为简单操作制造额外分配。
- **System** 描述“如何做”，是修改 Model、执行规则和发布事件的主要位置。
- **Model** 保存可持续查询的状态；`RP<T>` 同时支持 Unity 序列化和响应式订阅。
- **View** 只观察只读状态、监听事件并发送 Command，不直接拿着 Model 写值。
- **Context** 提供依赖解析、事件总线、命令系统和生命周期作用域。

## 五分钟接入

### 1. 安装包

在 Unity Package Manager 中选择 **Add package from git URL**，输入：

```text
https://github.com/heroliss/SSFramework.git
```

生产项目建议固定到发布 tag 或经过验证的 commit，例如：

```text
https://github.com/heroliss/SSFramework.git#v0.1.0
```

也可以在开发阶段通过 Package Manager 的本地路径方式引用工作副本。Unity 版本要求见 `package.json`；当前基线为 Unity `6000.3.22f1`。

### 2. 建立最小 Context

```csharp
using System;
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
```

在场景中创建一个 `MainContext`，再把业务 Model、System 和 View 放在它的子层级中。框架会按 Context 的作用域和生命周期完成注册与注入。

### 3. 编写状态与规则

```csharp
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

public readonly record struct PlayerHurtEvent(int Amount) : IEvent;

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
```

### 4. 让 View 只表达意图

```csharp
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

这样得到的是一条可追踪的闭环：按钮只发出意图，System 修改状态，响应式属性刷新文字，Event 可以驱动动画或音效。更换 UI、输入方式或存档实现时，核心规则不需要跟着改写。

## 能力模块

核心包提供 Context、依赖注入、Command、Model、System、View、Event、响应式状态、生命周期、对象池、异步取消和常用诊断原语。按需启用的模块覆盖：

- UI 与 UGUI / UI Toolkit 接缝
- 资源加载与 YooAsset Adapter
- 本地存储、Flow、音频和本地化
- Luban 配置与 Protobuf 接入
- HybridCLR、构建与发布辅助
- 日志、网络和 Editor 工作台

模块之间通过 Interface、Adapter 和程序集边界连接。你可以从 Core 开始，只引入项目真正需要的能力；第三方包不需要直接进入业务层。

## 文档与下一步

- [框架使用指南](docs/framework-guide.md)：从理念、Context 到各层 API 的完整教程。
- [Framework 模块地图](docs/framework-module-map.md)：模块职责、依赖方向和裁剪边界。
- [接入与升级说明](docs/consuming-framework.md)：安装方式、版本固定和升级检查。
- [架构决策记录](docs/adr/README.md)：关键设计的背景、取舍与影响。
- [命名约定](docs/naming-conventions.md)：Package、程序集和 C# 命名的边界。
- [可选 Odin 集成](docs/optional-odin-integration.md)：如何在不把商业插件变成核心依赖的前提下使用它。

修改公共 API 或模块依赖前，先阅读根目录 `AGENTS.md`。验证应与改动风险相称：纯规则优先运行包内测试，涉及 Unity 生命周期或 Editor 行为时再补充真实工程验证。

## 版本与许可证

包版本遵循 SemVer。稳定版本使用 release tag，开发中的兼容变更先在集成分支验证。SSFramework 自有代码遵循根目录 `LICENSE`；第三方包、字体、插件和示例资产仍受各自许可证约束。
