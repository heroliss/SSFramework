# ADR-0041：Module 依赖完整性与 Adapter-local 默认装配

**Status:** Accepted
**Date:** 2026-08-26

## 背景

Odin 从 Framework Runtime 解耦后，源码和已编译 Player DLL 已经没有 Sirenix 引用，但 HybridCLR 的 `link.xml`、`AOTGenericReferences.cs` 和代码包中转清单仍保留旧 Sirenix 根。原 generation stamp 只比较包锁、设置、平台和热更程序集名单；当“包仍安装、名单没变、某个热更程序集不再引用它”时，旧产物会与旧中转清单一起自洽，现有诊断无法发现。

同一轮删除测试还发现，多份生产 asmdef 把 `R3`、`ObservableCollections`、`Google.Protobuf` 等预编译 DLL 名写进了 `references`，同时保持 `overrideReferences:false`。Unity 的 `references` 表示 asmdef 程序集边，预编译 DLL 只有在 `overrideReferences:true + precompiledReferences:["X.dll"]` 下才是显式依赖；此前编译实际依赖 PluginImporter 的 Auto Reference。Module Audit 又把两个字段合并，错误地把这类全局可见性报告为显式声明。

资源 Seam 也存在 Locality 缺口：`IAssetProvider` 位于 Core、Yoo Implementation 位于可删除 Adapter，但 Core 的 `AssetProviderFactory` 仍硬编码 Yoo 的程序集限定类型名。删除或替换 Adapter 仍要修改 Core，删除测试没有真正成立。

## 决策

### 1. 分离 asmdef Assembly 边与预编译 DLL 边

- `AssemblyInfo.DeclaredReferences` 只保存 asmdef `references`。
- `DeclaredPrecompiledReferences` 只保存启用 `overrideReferences` 后生效的 `precompiledReferences`。
- 校验真实 DLL 元数据引用时，根据目标是否存在于 Player asmdef 编译图选择对应声明集合；DLL 名写进 `references` 不再被当作有效声明。
- 第一方 Runtime、Demo、业务和可选 Odin Editor Adapter 的直接 DLL 依赖迁到带 `.dll` 后缀的 `precompiledReferences`。所有一方 Player asmdef 统一启用 `overrideReferences:true`，关闭预编译 DLL 的全局 Auto Reference；这样平台条件分支里的新 DLL 依赖若未显式声明，会在目标编译时报错而不会借全局可见性静默通过。全局门禁扫描 `Assets/Game` 的 asmdef，防止重新混用字段；第三方 Package / 插件资产只读，不替上游改写。

这不会修改 NuGet DLL 自身的 PluginImporter Auto Reference（第三方和包外消费程序集仍可按自己的策略选择），但第一方 Player 编译边界已全部主动退出这种全局可见性。发布阶段仍应在干净 UPM 消费工程决定第三方 DLL 的 importer 默认值与迁移策略。

### 2. generation stamp 分别证明热更目标 DLL 与 AOT 输入

Unity 6000 的 `CompilationPipeline.GetAssemblies(AssembliesType.Player)` 会给出 Player defines / sourceFiles，但 `outputPath` 仍可能指向 `Library/ScriptAssemblies` 的 Editor DLL；即使 defines 中没有 `UNITY_EDITOR` 也不能把该文件当作目标 Player 证据。因此 stamp v4 分两侧处理：热更侧先由 HybridCLR `CompileDll(target, development)` 产出真实目标 DLL，再用其自带 dnlib 规范化 TypeDef / MethodDef、字段顺序与布局、签名与泛型约束、TypeSpec / MethodSpec、Attribute、P/Invoke / calli 和 IL 元数据操作数；AOT 侧哈希所有非热更 Player 源文件、asmdef、defines、编译器选项、response file、Roslyn Analyzer / Source Generator 输入与非 Unity 内置预编译 DLL，任何变化都保守要求 Generate。

`StripAOTDllCommand` 的迷你 Player Build 还受 UnityLinker 根影响：source `link.xml`、启用的 Build Settings 场景、Resources / Preloaded 资产或序列化组件变化，都可能改变 stripped AOT DLL 和后续 MethodBridge，而程序集元数据本身不变。第三条指纹因此记录这些根及依赖图，并对 `.unity` / `.prefab` / `.asset` / `.uxml` / `.guiskin` 等可承载托管类型的序列化资产记录内容哈希；`Assets/HybridCLRGenerate/link.xml` 是派生产物，必须排除以免 stamp 自我引用。动态 `IUnityLinkerProcessor` 的类型与实现程序集也进入指纹；但 processor 可读取任意外部文件/环境，框架无法猜出这类隐式输入。自定义 processor 必须让其配置成为上述可见根，或在配置变化后主动重跑 Generate；stamp 不宣称覆盖任意构建期副作用。

规范化条目不去重，因为同签名 callback / MethodSpec 的数量也会改变生成结果；SHA-256 输入使用 UTF-8 长度前缀，避免程序集名、Attribute 字符串中的逗号或换行制造边界碰撞。AOT 源哈希比目标元数据更保守，但不会漏掉 `#if !UNITY_EDITOR` / 平台分支，也不会让日常热更算法修改被迫重跑 Generate。

普通算术、分支、数值与字符串常量不进入指纹，因此不改变元数据边界的热更逻辑仍可走 CompileDll；新增方法、签名、泛型实例、值类型布局、P/Invoke 或相关 Attribute 会主动要求重新 Generate。Generate 仍是 AOT / 泛型派生物的最终真源，stamp 只负责证明其输入未漂移。

Generate 内部的 Player 构建会清空启用了 `m_ClearDynamicDataOnBuild` 的动态字体源资产。构建器在运行前通用发现这些 Assets 字体并保存原始字节，无论 Generate 成败都逐文件尝试恢复；单个文件失败不阻止其余恢复，生成与恢复同时失败时聚合两边异常。该事务不写死 Demo 路径，也不回滚其它构建输出或用户文件。

### 3. 默认资源 Implementation 由 Adapter 注册

Core 提供公开 Assembly attribute：

```csharp
[assembly: DefaultAssetProvider(typeof(MyProvider))]
```

具体 Adapter 在自己的 `AssemblyInfo.cs` 声明。Core 扫描已加载 Assembly 的注册并要求恰好一个合法、非抽象、具有无参构造的 `IAssetProvider`；零注册解释安装方式，多注册列出冲突。Yoo Module 保留自己的 `link.xml`，对“自定义属性引用 + 反射构造”在不同 Unity linker 版本下采用保守保护。

Assembly attribute 只负责装配声明，并不天然构成 UnityLinker 根。新的资源 Adapter 必须自带对应 `link.xml`（或等价的静态可达根），保证程序集在创建 `AssetUtility` 前已加载，并用目标平台 AOT Player 验证“发现注册 → 反射构造 → 初始化”的完整链路。Core 的测试友元也不引用具体 Adapter 测试程序集；Adapter 契约测试通过反射访问 internal Composition Root，避免用测试便利重新制造反向名字依赖。当前 Editor 契约与 link.xml 已验证，Core 隔离 IL2CPP 构建也已验证，但“业务完全不静态引用 Asset.Yoo、仅靠 linker 根发现注册”的独立 AOT 启动 Smoke 仍是发布前验证项，不能由 Editor 测试代替。

没有采用运行期可变全局注册表：默认后端是应用级架构装配，不是每场景状态；可变注册会引入初始化顺序、测试残留和运行期换血所有权问题。也没有把 provider 序列化进场景：它是有状态服务 Implementation，不是 per-instance 数据。

## 结果

- Module Audit 的“显式外部依赖”现在对应 Unity 真正生效的声明，删除判断不再建立在 Auto Reference 假证据上。Unity 6000 的 CompilationPipeline `outputPath` 仍可能指向 Editor 变体，因此界面中的 DLL 闭包只称“当前已编译快照”；目标平台结论由 Auto Reference 门禁、HybridCLR 目标 DLL 与真实 Player Build 共同证明。
- Odin 依赖从源码、Player DLL、HybridCLR AOT/link 生成物和 CodePackage 清单同时消失；以后相同拓扑变化会由 stamp v4 主动拦截。
- Core 不再知道 Yoo 类型或程序集名。替换资源后端的删除测试成为“删除旧 Adapter + 安装一个新注册 Adapter”，而不是修改 Core 常量。
- Embedded NuGet 目前仍是一个聚合 UPM 源包，隔离体积探针也会复制它的完整物理目录；这只能证明链接后的 Player 上界，不能证明最小安装闭包。按 Core / UI / Proto 拆发布依赖及许可证清单仍属于 ADR-0010 的 UPM 分发阶段，不在框架内复制第二套 Package Manager。

## 验证

- asmdef 字段 / Auto Reference 门禁与 Module Audit 当前编译快照外部引用测试；
- 目标热更 DLL 元数据拓扑的排序、数量、边界编码、结构定义、布局、特性参数与 P/Invoke 测试；AOT Player 源输入和 UnityLinker 非代码根指纹测试；
- Yoo 默认注册、非法注册与多注册失败测试；
- Unity 编译、EditMode / PlayMode 全量、Core 隔离构建；
- 正式执行 Generate/All 与 CodePackage 构建，检查 Sirenix 不再出现在生成与中转产物；再次 Generate 时字体源资产字节保持不变。
