# ADR-0019：服务注册代码生成 —— 目录扫描生成显式安装器 + 构建期值绑定自动注入

**Status:** Accepted（2026-07-03）

## Context

纯 C# 服务（不挂场景节点的 Model / System / Utility）目前全靠手写 `InstallBindings`：每加一个服务写一行 `builder.RegisterValue(new XxxSystem(), typeof(IXxxSystem))`。项目变大后这段样板有三类问题：

- **忘注册 / 契约漏写**：新服务写完忘了去根 Context 加行，运行期才抛「not registered」；接口契约漏传一个，换实现时调用方解析不到。
- **手写契约与 Mono 路径口径漂移**：Mono 路径（`MonoXxxBase`）自动注册「具体类型 + 所有派生自层标记的接口」，手写路径全凭自觉，两条路径注册面不一致。
- **纯 C# 路径注册后无人注入**：Mono 路径 Awake 时自动 `Inject + AttachTo`，而 `InstallBindings` 注册的实例没有对应步骤——带 `[Inject]` 字段或实现 `IHasGameContext` 的服务必须调用方手动补 `ctx.Inject(s); ctx.AttachTo(s);`（见 `FrameworkSelfCheck`、guide §11），极易遗漏且遗漏后是**静默 null**。

roadmap 既定方向（2026-07 审查）：**编辑期扫描固定目录生成一份显式的安装器代码，刻意不做运行时反射扫描自动注册**——启动零扫描、AOT / 热更友好（HybridCLR 下运行时程序集遍历既慢又易踩裁剪坑）、注册关系落在 `.g.cs` 里 git diff 可见可审。

约束基线：

- Container 按**精确类型键**查找、构建期绑定「后注册覆盖先注册」（静默）；运行时覆盖层重复注册才抛异常。
- 框架已有两个代码生成器口径可对齐：UI 节点绑定（①，文件名=类名、`.g.cs` 覆盖式输出）与包名常量（③，profile 配输出、幂等不写盘、菜单+Inspector 按钮入口）。
- 层标记接口（`IModel` / `ISystem` / `IUtility`）不继承 `IHasGameContext`——后者是「需要 `this.GetXxx` 扩展方法」时才显式实现的可选接口。

## Decision

### 1. 映射约定：Profile 条目「扫描目录集合 → 安装器类」；安装器 → Context 的绑定是手写的一行

`ServiceInstallerProfile`（ScriptableObject）持有条目列表，每条 = **N 个扫描目录 → 1 个生成的安装器类**（输出路径 + 命名空间；类名 = 文件名去 `.g.cs`，与 ③ 同口径）。生成物形如：

```csharp
public static class MainServicesInstaller
{
    public static void Install(ContainerBuilder builder)
    {
        builder.RegisterOwned(new AudioSystem(), typeof(AudioSystem), typeof(IAudioSystem));
        builder.RegisterValue(new SaveUtility(), typeof(SaveUtility), typeof(ISaveUtility));
    }
}
```

Context 侧手写一行接线：

```csharp
protected override void InstallBindings(ContainerBuilder builder)
    => MainServicesInstaller.Install(builder);
```

**为什么生成器刻意不指认 Context**（「目录→Context 映射」的核心取舍）：Context 是运行时 / 场景概念——挂在场景节点、可嵌套、可动态创建，编辑期生成器无法可靠指认「哪个 Context 实例」；而「目录→安装器类」是纯编译期映射，生成器能完全负责。安装器→Context 的最后一跳收敛为**一行手写调用**，恰好是 git 可审的接线点，也天然支持复用——测试 Context、子 Context 想装同一批服务就再调一次，无需生成器理解场景结构。

### 2. 扫描口径：文件名=类名 + 程序集精确定位，候选过滤规则固定

对条目的每个扫描目录：`AssetDatabase` 枚举 `.cs` → 文件名即类名（项目既定口径，①③ 同）→ `CompilationPipeline.GetAssemblyNameFromScriptPath` 定位所属程序集 → 在该程序集内按短名找类型（跨命名空间同短名多命中 = 无法判定文件归属，警告跳过）。

类型入选条件（全部满足）：

- 顶层非抽象非泛型 `class`，实现**恰一个**层标记（`IModel` / `ISystem` / `IUtility` 派生；一个不实现=不是服务静默跳过，多于一个=设计错误警告跳过）；
- **非** `UnityEngine.Object` 派生（Mono 层走场景自动注册路径，SO 是数据资产，都不归安装器管）；
- 有**公共无参构造**（没有 = 需要显式接线，警告跳过、手写注册）;
- 未标 `[ExcludeFromInstaller]`（opt-out 特性，运行时程序集内定义，见 §5）。

### 3. 契约推导与注册形态：对齐 Mono 路径口径，直接 `new`

- **契约 = 具体类型 + 所有派生自对应层标记的接口（不含标记本身）**——与 `ContainerLayerExtensions.RegisterFor` 完全同口径，消除两条路径的注册面漂移。
- **`IDisposable` → `RegisterOwned`，否则 `RegisterValue`**；一律直接 `new`（服务启动即就绪，与 Mono 路径 Awake 注册的时序心智一致）。要懒构造 / 带参构造的服务 → opt-out 后手写工厂（工厂经 `c.Resolve` 显式接线）；产物应随 Context 释放时用 `RegisterOwnedFactory`，否则才用普通 `RegisterFactory`（所有权语义见 ADR-0035）。

### 4. 生成期查重复契约：同安装器内接口契约冲突 = 生成失败

构建期绑定的容器语义是「后注册覆盖先注册」**且静默**——手写时这是特性（子 Context 换实现），生成场景下却是事故温床：两个实现同一接口的服务被扫进同一安装器，谁覆盖谁取决于遍历顺序。故生成期检查：**同一安装器内两个类型推导出同一接口契约 → 该条目生成失败并列出冲突双方**，用户 opt-out 其一或拆目录。跨安装器 / 跨 Context 不查——那是合法的覆盖场景。

### 5. opt-out 特性放运行时内核

`[ExcludeFromInstaller]`（`Game.Framework.Context`，空特性）标在服务类上即被生成器跳过。放运行时程序集因为它要标注在运行时类型上；生成器（Editor）按类型读取。命名不带 "Generated" 前缀——它语义上就是「这个类不进安装器，注册我自己管」。

### 6. 内核对称性补齐：构建期值绑定实例在 GameContext 构造时自动 `Inject + AttachTo`

`ContainerBuilder.Build()` 收集**值绑定的去重实例列表**（`RegisterValue` / `RegisterOwned` 传入、且构建完成时仍生效的实例），`GameContext` 构造函数逐个 `Inject`（解析 `[Inject]` 字段）+ `AttachTo`（回写 `GameContext` 字段）。这不是生成器的私有便利，而是**修一个既有的不对称**：Mono 路径「注册即注入」，纯 C# 路径此前「注册后裸奔」。补齐后：

- 生成的安装器对带 `[Inject]` / `IHasGameContext` 的服务**开箱可用**，无需生成器发明第二阶段注入协议；
- 手写 `InstallBindings` 同等受益，guide 里「注册后手动 Inject/AttachTo」的样板只剩**运行时动态注册**（`ctx.RegisterXxx`）一处还需要；
- **工厂产物刻意不自动注入**：`RegisterFactory` / `RegisterOwnedFactory` 的签名 `Func<Container, object>` 本就是显式接线位（`c.Resolve` 拿依赖），且懒构造时机不可预期，自动注入反而把时序弄模糊。值绑定=自动，工厂=自管；是否 owned 是另一条正交轴（ADR-0035）。

注入时机在 Context 构造点：全部绑定已入容器，父链已可解析（`MonoGameContextBase.Initialize` 先递归初始化父级）；同容器互相 `[Inject]` 的两个值实例互见（实例先于注入全部存在，无环序问题）。

## Consequences

- **注册样板消失**：固定目录放服务类 → 生成 → Context 一行接线；新增服务重跑生成即入列，漏注册在生成 diff 里可见。
- **文件名=类名成为扫描前提**：一文件多类的次要类型扫不到（与 ①③ 同一约定，项目内一致）；违反约定的类型静默漏扫是已知代价，靠 code review 与约定兜底。
- **无参构造 / 单层标记 / 非 Unity 类型**是硬入选条件，不满足的服务回落手写——生成器不试图覆盖 100%，覆盖「常规服务」这 90% 即可（no-over-engineering）。
- **自动注入是全局语义变更**：此前「注册了但从未注入」的值实例开始走注入路径——若其 `[Inject]` 字段违反层权限（如 Model 注 System），以前静默 null、现在启动期报错。这是 fail-fast 修正而非回归；`AttachTo` 只作用于显式实现 `IHasGameContext` 的类型，无关类型零影响。
- **同容器多 GameContext / 重复构造**会重复注入（幂等覆盖，字段值相同）——不支持的用法，文档不鼓励。
- 生成器要求编译通过后才能扫描（基于反射类型而非语法树）——改了服务类先等编译再生成，与 Unity 心智一致。
- 生成物是普通 C# 文件，进版本控制、进热更程序集都照常；运行时零新增反射（注入用的 `InjectionPlan` 反射是既有机制，Mono 路径同源）。
