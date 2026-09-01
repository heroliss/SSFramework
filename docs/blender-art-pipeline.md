# Blender 3D 资产管线：探索基线

> 状态：**Spike v0.1**，验证于 2026-09-01。当前目标是证明项目能以可复查、可替换、跨 Agent 的方式驱动 Blender，而不是立即生产正式人物和场景资产。本文记录的是已验证基线与候选边界；首个正式资产进入 Unity 后再升级为安装 / 配置指南。

## 1. 当前结论

本机 Blender **5.2.1 LTS** 已足够完成第一阶段，不需要为了“AI 自动化”先安装一堆插件：

- Blender 自带 Python 3.13.13 和 `bpy`，可用 `--background --factory-startup --python` 做版本化、可重复的生成、规范化、导出、预览和统计；
- FBX、glTF、Rigify、Node Wrangler 等所需模块随当前安装提供；Rigify 与 Node Wrangler 默认未启用，但已经用一次性 `factory-startup` 进程验证可加载，没有修改用户偏好；
- 项目首先采用 **CLI + 版本化 Python 脚本** 作为可重复基线；Codex、其他 Agent、人或 CI 都可以运行同一个入口；
- Blender MCP 暂时只作为未来的交互式迭代候选，不进入项目依赖。它适合“看着场景快速试形”，不适合替代版本化脚本、CI 或资产验收；
- 第一阶段不承诺全自动人物建模、骨骼放置、权重、表情和高质量动画。AI 可以显著加速概念、模块建模、规范化和变体，但英雄资产仍需要人工视觉判断与局部修正。

官方 [Blender 5.2 LTS](https://www.blender.org/releases/5-2/) 支持周期到 2028 年 7 月，适合作为稳定 DCC 基线。项目不把机器安装路径写成真值；脚本通过参数、环境变量、PATH 或 Windows 注册表定位可执行文件。

## 2. 已实现 Smoke Harness

入口：

```powershell
powershell -File Tools/ArtPipeline/Blender/run-blender-smoke.ps1
```

也可以显式指定路径和输出：

```powershell
powershell -File Tools/ArtPipeline/Blender/run-blender-smoke.ps1 `
  -BlenderPath "D:\Blender 5.2\blender.exe" `
  -OutputRoot "D:\Temp\SSFramework-Art"
```

脚本会在 `--factory-startup` 下生成一个废土风格储物箱，并验证：

```text
ArtPipelineOutput/BlenderSmoke/NW_StorageCrate_01/
├── NW_StorageCrate_01.blend
├── NW_StorageCrate_01.fbx
├── NW_StorageCrate_01_preview.png
└── manifest.json
```

`ArtPipelineOutput/` 是可重建实验产物，已被 Git 忽略；脚本不会修改 Blender 用户偏好，也不会向 `Assets/` 写入文件。

2026-09-01 实际结果：

| 项目 | 结果 |
|---|---|
| Blender / Python | 5.2.1 LTS / 3.13.13 |
| 输出 | `.blend`、FBX、512×512 PNG、JSON manifest 均存在且非空 |
| 几何 | 14 个 Mesh、784 顶点、1512 三角形 |
| 尺寸 | 约 1.235 × 0.958 × 0.945 米 |
| 坐标契约 | Blender 米制、+Z Up；FBX `-Z Forward / +Y Up`；Root Identity |
| 证据 | 单次输出文件大小与 SHA-256、源脚本 SHA-256、版本、Bounds、建议 BoxCollider、实际预览图 |

这只证明 **Blender → FBX 的本地生产探针成立**。它尚未证明 Unity Importer 的比例、材质映射、Prefab、Collider、LOD、目标平台性能和游戏镜头效果；这些必须在首个正式资产的 Unity Import Spike 中补齐。

这里的“可重复”指同一版本脚本能重建相同资产结构、命名、尺寸、几何统计和视觉意图，并不承诺 `.blend`、FBX 或 Eevee PNG 跨运行逐字节相同。manifest 中的 SHA-256 用来校验**这一轮运行**的证据完整性；跨运行回归应比较结构化字段和经批准的视觉 / Importer 指标，不能直接把二进制哈希变化判成资产回归。

## 3. 为什么先用 CLI，而不是先装 Blender MCP

CLI 基线具备以下特性：

- 输入脚本和参数可提交、审查、比较；
- `--factory-startup` 不依赖个人快捷键、插件开关和工作区；
- 可以固定 Blender 版本、Seed、输出路径和停止条件；
- 失败有进程退出码、日志和 manifest，不需要从 UI 猜测；
- 不要求 Agent 客户端支持某一种 MCP 配置。

[Blender 官方 MCP Lab](https://www.blender.org/lab/mcp-server/) 已支持 Blender 5.1+，但官方页面明确提醒：它会执行 LLM 生成的代码，没有内置安全护栏，并建议只在没有敏感数据的隔离环境中运行。主流社区实现 [ahujasid/blender-mcp](https://github.com/ahujasid/blender-mcp) 同样以 Socket + 任意 `bpy` Python 为主要能力；所谓“安全 Fork”即使加入 Token、AST 或网络限制，也不等于操作系统沙箱。

因此未来评估 MCP 时采用以下边界：

1. 只在可丢弃的 `.blend` 副本或隔离账号 / 虚拟机中评测；
2. 不向 Blender 进程暴露仓库外敏感目录、浏览器凭据或 API Key；
3. MCP 负责交互探索，确认后的重复步骤回写为版本化 Python；
4. 任何导出仍通过 manifest、预览和 Unity 验收；
5. 不同时安装多个功能重叠的 Blender MCP。

只有交互修改次数明显高于 CLI 脚本维护成本时，MCP 才填补了真实缺口。

## 4. 插件与扩展的最小策略

### 当前不需要额外安装

| 能力 | 当前入口 | 说明 |
|---|---|---|
| FBX / glTF 导入导出 | Blender 内置 I/O Add-on | 已在 `factory-startup` 下验证 FBX 导出 |
| 基础材质节点 | 原生 Shader Editor | 不需要额外节点包 |
| Humanoid 骨架原型 | 内置 Rigify | 已验证 Human Metarig 可创建；正式采用前要定义 Unity Avatar / Export 骨骼边界 |
| 常用节点操作 | 内置 Node Wrangler | 可按需启用；Smoke 不依赖它 |
| 批处理、命名、统计、预览 | `bpy` 脚本 | 项目优先使用可审查脚本，不先装“万能批处理”插件 |

### 有真实痛点再评估

- Blender Extensions 的 [Retarget](https://extensions.blender.org/add-ons/retarget/)：只有多个来源动画需要反复在 Blender 中重定向、Root Motion 或烘焙时再试；当前 Unity Humanoid 已能覆盖首轮共享动画验证。
- Auto-Rig Pro、UVPackmaster 等商业 Add-on：只有 Rigify / 原生 UV 成为可测量瓶颈且许可、版本与团队安装方式明确时考虑。
- 生成式 3D 平台：Meshy 等用于原型或底模，仍需经过拓扑、UV、材质、比例、骨架、动画和商业授权验收；不能把“能下载 FBX”当作 game-ready。

插件列表应保持短小。一次性方便、泛泛“最佳实践”或与原生功能高度重叠，不足以成为项目依赖。

## 5. 正式资产的目录与所有权

Smoke 阶段只提交脚本，不提交生成物。首个正式资产通过 Unity Import Spike 后，再按实际规模创建：

```text
ArtSource/NomadWorkshop/            # 可编辑源：.blend、贴图工作文件、资产 brief
Assets/Game/NomadWorkshop/Content/  # Unity Runtime 导出与配置
docs/asset-register.*               # 来源、许可、生成信息与验收状态（达到规模后）
```

建议的所有权链：

```text
Asset Brief
  → Source / 生成原始结果
  → Blender 规范化源文件
  → 中立导出（FBX / glTF + Texture）
  → Unity Import Settings / Prefab
  → 代表性游戏镜头中的验收
```

- `.blend` 是可编辑源，不应直接被运行时代码引用；
- Unity 只消费明确导出的 FBX / glTF 和贴图；
- 生成平台原始文件与人工清理后的源文件不是同一状态，必须保留关系；
- 第三方或生成资产记录来源、版本、许可证、修改和最终用途；
- 稳定路径服务于替换资产，游戏逻辑不依赖 Provider 文件名；
- 二进制源文件增长到真正需要时再引入 Git LFS，不为一个 Smoke 预建存储体系。

## 6. 每类资产的最小 Contract

不是所有字段都做成强制 Schema；实际资产至少要能回答：

| 类别 | 关键问题 |
|---|---|
| 身份 | 稳定 Asset ID、用途、负责人 / 来源、Prototype / Candidate / Approved 状态 |
| 几何 | 米制尺寸、Pivot、朝向、Root Transform、三角面、材质槽、是否需要 LOD |
| 交互 | Collider / NavMesh / 放置占地、访问面、手部 / 工具 / VFX / SFX 锚点 |
| 材质贴图 | 色彩空间、PBR 通道、分辨率、压缩、法线格式、是否可图集 / 复用 |
| 角色动画 | 骨架、Avatar、Bind Pose、Root Motion、Clip 范围、循环、Foot / Hand 接触 |
| 性能 | 目标镜头、实例数、GPU / CPU / 内存预算与目标平台 |
| 权利 | 原始输入权利、工具 / 模型 / 日期、Prompt / Seed、许可证、人工修改与可替换性 |
| 证据 | DCC 预览、Unity 固定镜头、Importer 数据、运行时 / 性能结果、人工未决问题 |

对于当前固定镜头、单层甲板的小人口游戏，尺寸、轮廓、交互锚点和共享骨架的价值高于隐藏面的细节密度。

## 7. 推荐制作工作流

### 环境模块和普通道具

1. 先写镜头内用途、尺寸、轮廓、模块接口和材质族；
2. 用 `bpy`、手工建模、AI 底模或合规第三方源取得候选；
3. Blender 统一 Scale / Rotation、Pivot、命名、材质槽和网格；
4. 输出 FBX / glTF、预览、统计与来源信息；
5. Unity 使用固定 Import Preset 或 Editor 工具导入；
6. 在真实车辆 / 停靠镜头检查比例、遮挡、碰撞、光照和批量实例成本；
7. 保留、返工或删除，不因已花生成费用而降低标准。

### 居民与动画

1. 先锁定一套共享 Humanoid 骨架、基础比例和约八个通用动作；
2. 头发、服装、背包、工具和材质做模块化差异，身体比例只在 Avatar 仍可靠的范围变化；
3. 设施声明站位、朝向、动作种类、手部目标和工具挂点；
4. Unity Humanoid 先验证 Walk / Carry / Repair / Rest 等共用动作；
5. 只有英雄级接触点不成立时，再回 Blender 做权重、IK、Root Motion 或专用 Clip；
6. 面部绑定、口型、布料和多套骨架继续延后。

完全自产的优势是可控和可修改，成本则集中在拓扑、UV、权重和动画打磨；全部依赖商店资产速度快，却容易风格割裂和受许可证 / 结构限制。当前推荐混合方式：**英雄轮廓与模块规范由项目拥有，通用动作和底模允许合规来源，AI 负责草图、变体和机械清理，最终接触点人工验收。**

## 8. 下一步证据

1. 将 Smoke FBX 临时导入一个隔离的 Unity Import Spike，核对 1 米、朝向、材质槽和建议 Collider；不直接把 Smoke 宣布为正式道具。
2. 用同一 Harness 生成第二种几何结构，确认脚本不是只对一个箱子偶然有效。
3. 为首个共享 Humanoid 建立 Blender / Unity 往返证据，比较 Rigify、现有 Quaternius 角色和未来自制模块的成本。
4. 只有上述流程重复出现并包含稳定判断分支后，再创建 `blender-asset-pipeline` Project Skill；在此之前，文档 + 脚本比一个新 Skill 更容易维护。

官方参考：[Command Line](https://docs.blender.org/manual/en/latest/advanced/command_line/index.html)、[Python API](https://docs.blender.org/api/current/)、[Rigify](https://docs.blender.org/manual/en/latest/addons/rigify/index.html)、[FBX Operator](https://docs.blender.org/api/current/bpy.ops.export_scene.html)。
