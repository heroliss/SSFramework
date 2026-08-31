---
name: improve-ssframework-architecture
description: 基于 CONTEXT、ADR、调用链和测试证据审查或改进 SSFramework 的 Module 深度、依赖方向、生命周期所有权、可删除边界与 AI 可导航性。用于架构评审、公共重构、耦合收口或模块化改进；普通局部整理、单一 bug 诊断或泛化“Clean Architecture”套层不触发。
---

# 深化 SSFramework 架构

寻找有证据的架构摩擦，把分散在调用方的复杂度收进能提供高杠杆、可验证行为的深 Module。目标不是增加 Interface 数量或追求目录对称，而是提高 Locality、删除能力、测试质量和 AI 对真实所有权的理解。

## 真值顺序

开始前按任务范围读取：

1. 根到目标目录的 `AGENTS.md`；
2. `CONTEXT.md` 中相关领域词汇；
3. `docs/adr/` 中覆盖该设计的 Accepted 决策；
4. 当前代码、调用方、asmdef、测试、运行证据和 Git 历史。

`CONTEXT.md` 负责稳定词义，ADR 负责已接受取舍，代码和测试负责当前行为。三者冲突时先报告冲突，不挑方便的一份当真值。评审任务只报告，不顺手改代码、ADR 或领域词汇；用户授权实现后才同步受影响文档。

架构词汇优先使用 Module、Interface、Implementation、Seam、Adapter、owner、lease 和物理终态，但不禁止 Unity / 项目中的合法术语，如 `Component`、Service、API、Composition Root 或 assembly boundary。术语服务于精确表达，不能反过来改写领域。

## 确定范围

- 从用户目标、当前 diff、失败契约或明确热点出发；开放式评审才使用 `git log -- <path>`、改动频率和 bug 历史缩小候选。
- 先画出一条真实调用、数据和所有权路径，再判断结构。不要只按文件数量、类长度、命名或静态依赖图给结论。
- 排除用户未授权区域、第三方 Package Implementation、未采用 Agent 的产品配置和与当前目标无关的“顺手现代化”。
- 如果缺少会改变公共 Interface 或兼容性的产品决定，展示证据和选项后请求用户选择；不要把架构审美冒充已授权需求。

## 六个审查镜头

| 镜头 | 寻找什么 | 常见假阳性 |
|---|---|---|
| 深度与 Locality | 调用方是否反复知道顺序、状态、错误、缓存或恢复细节 | 一行 Adapter、Composition Root 接线本来就应浅 |
| 所有权与物理终态 | caller、Context、session、handle、取消和迟到 continuation 是否只有一个 owner | 只比较最终字段而忽略旧任务仍在运行 |
| 依赖方向 | Core 是否知道可选 Module / 第三方具体类型，业务是否绕过稳定 Interface | 合法的下游 Adapter 依赖、Editor-only 组合层 |
| 删除与替换 | 删除可选 Module 后复杂度是消失、集中，还是散回 N 个调用方 | 为假想第二实现预建公共 Seam |
| 证据可信度 | 测试是否穿过真实 Interface，Editor 证据是否被误称为目标 Player | 仅为测试暴露内部状态或复制一份状态真源 |
| AI 可导航性 | 领域名、owner、终态和失败语义是否能在一处找到 | 为“AI 友好”添加重复索引、转发类或同步文档真值 |

Unity 特有风险要显式检查：`Awake/Start/OnDestroy` 与 Domain Reload、PlayerLoop 和主线程终态、Unity fake-null、场景 / Prefab 身份、asmdef / precompiled DLL、linker 根、HybridCLR 完整程序集部署、UPM 物理路径和 Editor / Player 证据差异。

## 探索候选

对每个可疑点执行以下检验：

1. **重复知识**：有多少调用方必须知道相同的不变量、顺序、重试、错误折叠或清理步骤？
2. **删除测试**：删除当前 Module 后，复杂度会消失，还是会原样散到多个调用方？前者可能是无价值转发，后者说明它在提供深度。
3. **替换测试**：是否真的存在生产 Adapter、平台 Adapter、测试 stand-in 或计划中的物理删除需求？只有测试 mock 并不自动证明公共 Seam 合理。
4. **接口测试面**：调用方可观察行为能否通过同一 Interface 测到？如果必须穿透私有状态，先怀疑 Interface 形状或 Oracle。
5. **终态测试**：成功、失败、取消、Dispose、重入、迟到完成和恢复是否都能收口，而不是只测 happy path。
6. **证据保真**：当前 Editor、测试 fake、隔离 Build 和目标 Player 各能证明什么，不能证明什么？

每项判断附精确文件、调用点、测试或运行证据。没有证据的猜想可列为待调查问题，不能排进重构清单。

## 排名并呈现

候选按“玩家 / 调用方影响 × 发生频率 × 证据置信度 ÷ 迁移风险”排序，通常只保留最强的 3–5 项。每项包含：

```text
范围：涉及的 Module、Interface、调用方和文件
观察：可复核的重复知识、耦合、所有权或证据缺口
建议：复杂度移到哪里，哪些 Seam 保留、收窄或删除
收益：Locality、调用方杠杆、删除能力和测试如何变化
代价：兼容性、序列化、性能、迁移和文档影响
ADR：遵循、需要澄清，或有充分证据建议重开哪条决策
验证：哪些定向契约、删除测试、场景、Player 或构建证据会证明改进
```

不要因为“可以抽象”就推荐；也不要把小文件合并、统一命名或增加层数本身写成收益。若当前形状已经是最窄且有意的 Adapter，明确建议保持现状同样是有效结论。

## 设计选中候选

只有用户选择候选，或请求本身已明确授权实现时，才进入接口设计与代码变更：

- 先列 Interface 的输入、输出、不变量、调用顺序、失败 / 取消、线程、场景和性能语义；签名只是其中一部分。
- 公共 Interface 变化风险较高时至少比较两个实质不同的形状；无需为形式主义固定生成三份方案或强制委派。
- 把变化性留在真实 Seam：第三方 / 平台能力用 Adapter，纯内部可测试点优先 internal Seam，不因测试方便扩大公共 API。
- 明确谁创建、拥有、取消和释放；逻辑状态与物理任务、socket、handle、下载或 Player Build 的终态分别表达。
- 保持现有五层、Context 权限和 Adapter-local 默认装配；若确需推翻 Accepted ADR，先把新证据、迁移与兼容代价交给用户决定。

## 实现和验证

- 变更保持围绕一个成立的架构命题，不把邻近清理塞进同一批。
- `Library/PackageCache` 不直接修改；场景和 Prefab 只经 Unity 编辑器修改并保存。
- 新领域概念只有跨代码、测试和文档都需稳定使用时才加入 `CONTEXT.md`；持久设计取舍才写 ADR。
- 旧测试只有在新 Interface 已覆盖同一可观察契约且旧测试只锁 Implementation 时才删除。
- 使用 `unity-validation-harness` 选择与风险相称的编译、定向、全量、视觉、性能或隔离构建证据。涉及可删除 Module 时优先使用现有 Module Audit / Build Size Probe；涉及目标平台声明时不能只用 Editor DLL 或 PlayMode 代替 Player。

最终说明改动让哪条知识获得了唯一 owner、哪些调用方知识被删除、哪些风险仍未验证。若实施后 Interface 更大、状态真源更多、删除测试更差或测试必须更了解 Implementation，应撤销或重新设计，而不是靠文档解释它“更架构化”。
