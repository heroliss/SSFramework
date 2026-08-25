# ADR-0040：UPM-aware Framework Module Source Catalog

**Status:** Accepted（2026-08-25）

## Context

Framework Module Audit、隔离 Build Size Probe 和源码门禁都需要读取 asmdef、`link.xml`、C# 源码或 Editor 模板。Unity 同时存在两种有效身份：

- `Assets/...`、`Packages/...` 是 AssetDatabase、Project 窗口和报告应使用的稳定 Asset Path；
- `System.IO` 需要真实 Physical Path。registry / Git 包的源码通常位于 `Library/PackageCache` 或外部缓存，并不在项目的 `Packages/<name>` 物理目录。

旧实现由各工具自行把相对路径拼到 Project Root。它在当前仓库的 `Assets/Game/Framework` 布局下可工作，但一旦框架抽成 UPM package，就会把存在的 asmdef 当成缺失、漏扫 package 内 `link.xml`，或让隔离构建复制不到源码。继续给每个调用点增加 `Assets` / `Packages` 分支只会复制浅路径知识，形成多个会漂移的 owner。

## Decision

### 1. 用一个 Catalog 拥有源码身份映射

`Game.Framework.Editor` 内部的 `FrameworkModuleSourceCatalog` 是 Editor 侧唯一 owner。它接受：

- `Assets/...` Asset Path；
- `Packages/...` Asset Path；
- 已解析的绝对 Physical Path。

输出 `SourceLocation`，同时给出 canonical Asset Path、真实 Physical Path、两种根目录、package 名称/版本/id 及文件所在目录。`Assets` 路径从 `Application.dataPath` 解析；Package 路径从 Unity Package Manager 的注册信息与 `resolvedPath` 解析。Package 根目录在部分 Unity 版本无法由 `FindForAssetPath` 直接命中，因此以当前已注册 package 表作确定性回退。输入中的重复分隔符与 `.` / `..` 只用于求出受根目录约束的物理路径，返回身份再从“物理路径 + 根”反算，保证同一资产只有一个稳定身份。

规范化后的路径必须仍位于声明根目录；`Assets/../Packages/...` 这类逃逸输入 fail-fast。AssetDatabase 已登记但无法解析或物理文件不存在的候选同样聚合报错，尤其不能从 `link.xml` 证据中静默过滤。Catalog 只描述已安装源码，不安装、卸载或解析 package 版本依赖。

### 2. Asset 身份与物理 I/O 各司其职

- AssetDatabase 选择、Project 窗口定位、用户可复制报告使用 `AssetPath`；
- `File` / `Directory` 读取和隔离工程复制使用 `PhysicalPath`；
- 诊断与构建证据保留 package 名称/版本，避免只留下某台机器的 PackageCache 绝对路径。

找唯一框架模板时先以程序集 asmdef 确定源码域，再在该域按文件名查找；缺失和域内重名都 fail-fast，无关第三方 Package 的同名文件不会误伤工具。`link.xml` 从 AssetDatabase 的项目与已注册 Package 资产集合枚举，再经 Catalog 严格打开真实文件。

### 3. Audit、Probe 与门禁共享同一个 Seam

- Module Audit 解析 CompilationPipeline 报告的 asmdef 路径，记录源码物理目录和 package 所有者；Module 内 `link.xml` 的所有权按物理目录判断，报告仍保存 Asset Path。
- Build Size Probe 从审计结构化结果复制真实 Module 目录，以程序集名作为隔离工程目录，避免多个 package 都使用 `Runtime/` 叶名而发生覆盖；开始前拒绝两个 Module 源目录相同或互相嵌套，否则物理复制会夹带未选 asmdef。JSON / Markdown 只保存 Asset 目录、package id 与“过滤 Editor/Test 后实际复制文件”的 SHA-256 内容指纹，Physical Path 仅存在于内存运行计划。Domain Reload 恢复会逐一校验报告档位仍存在于当前拓扑，并比较内容指纹；漂移时只允许重新附着已经启动的子进程，完成后停止，不会静默跳过已移除档位或混用两套源码。
- 模态弹窗审计与“通用 Framework 不硬编码当前项目”门禁先通过 Catalog 找到可复用源码，不依赖 `Assets/Game/Framework` 的物理位置。

Catalog 是窄 Editor Implementation，目前没有第二种源码注册机制，因此不制造公共 Runtime Interface。若未来需要支持非 Unity Asset 的生成源码，再以真实替换需求扩展输入 Adapter。

## Consequences

- ✅ UPM 抽包前先消除了工具链对仓库物理布局的耦合，迁移不再要求同时重写 Audit、Probe 和测试。
- ✅ Asset Path、Physical Path 与 Package 所有权成为同源证据；“能在 Project 窗口看到但 `File.Exists` 为 false”的误报被结构性消除。
- ✅ 项目 Assets 与已注册 Package 的 linker 根使用同一保留模型，Module 删除解释更完整。
- ✅ 隔离构建报告可以追溯实际使用的 package 版本，同时不把机器专属缓存路径当成稳定身份。
- ⚠ Catalog 依赖 Unity Editor 与 Package Manager API，只属于 Editor Module，不进入玩家运行时。
- ⚠ Package 内容发生变化后仍需 Unity 完成刷新 / 注册；Catalog 不绕过 AssetDatabase 生命周期，也不扫描未安装的缓存包。
- ⚠ 按文件名查唯一模板要求仓库内保持唯一；出现重名时应建立更明确的资产角色，而不是恢复硬编码根路径。

## Alternatives considered

- **继续使用 `Path.Combine(ProjectRoot, assetPath)`**：拒绝。只对项目内物理 Assets / embedded package 偶然成立，registry/Git PackageCache 不成立。
- **每个工具自行判断 Assets / Packages**：拒绝。重复 PackageInfo、路径逃逸与回转逻辑，错误会以不同方式出现。
- **把 Framework 立即移动到 UPM 再修工具**：拒绝。会把结构迁移和证据工具失效绑成一次难审查变更；先建立 Seam 能降低后续迁移风险。
- **在 Catalog 中增删 package 或改 manifest**：拒绝。那是 UPM 的职责，也会把只读诊断变成破坏性操作。

## Related

- ADR-0010（框架复用边界与 UPM 抽包路线）
- ADR-0038（隔离 Framework Build Size Probe）
- ADR-0039（Framework Module 选择与保留证据模型）
- `docs/framework-module-map.md`
