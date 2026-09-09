# SSFramework

SSFramework 是面向 Unity 项目的模块化基础框架，以 UPM 包形式提供可组合的运行时与编辑器能力。它负责稳定的通用抽象、模块边界和生命周期约定；游戏玩法、教程场景、美术资产和项目级 `ProjectSettings` 留在各自的消费仓库。

![SSFramework 架构图](docs/SSFramework-architecture.png)

## 这个仓库提供什么

- `src/`：`com.liss.ssframework` 的包源码、程序集和包内测试。
- `docs/framework-guide.md`：从核心概念到接入方式的使用指南。
- `docs/framework-module-map.md`：模块职责、依赖方向和可删除边界。
- `docs/adr/`：公共架构决策及其影响。
- `src/Branding/`：可替换的框架品牌图形；消费项目可以自行替换产品视觉，不应把游戏资产反向放回 Framework。

Framework 不包含 Outpost、FrameworkTutorial 或 NomadWorkshop 的运行时代码。它们通过 `Packages/com.liss.ssframework` 子模块固定消费某个 Framework commit。

## 快速接入

在 Unity 项目中初始化子模块：

```powershell
git submodule update --init --recursive
```

项目应使用 Unity `6000.3.22f1`，并确认 `Packages/com.liss.ssframework` 指向已经验证过的 commit。首次使用建议按[框架使用指南](docs/framework-guide.md)的快速开始章节建立最小 Context、Model、System、Command 和 View，再按需引入可选模块。

## 仓库与版本同步

Framework 使用 `main` 作为稳定消费线，`develop` 作为集成线，发布使用 SemVer 标签（例如 `v0.1.0`）。消费仓库不会自动跟随最新提交；更新流程是“Framework 提交并推送 → 消费仓库检出批准的 SHA/tag → 运行消费方验证 → 提交新的 gitlink”。完整流程、仓库地图以及 `framework.git` / `source.git` 的说明见[仓库集成与 Framework 同步](docs/repository-integration.md)。

## 开发与验证

修改公共 API、程序集或模块依赖前先阅读：

- [项目协作规则](AGENTS.md)
- [Framework 模块地图](docs/framework-module-map.md)
- [命名约定](docs/naming-conventions.md)
- [架构决策记录](docs/adr/README.md)

包仓库没有自己的游戏场景；验证以包内 EditMode/PlayMode 测试和真实消费方回归为准。第三方依赖通过包侧 Adapter 或 Interface 隔离，不直接修改 `Library/PackageCache/`。提交标题和正文按仓库协作规则记录问题背景、设计取舍与验证结果。

## 许可证与可替换资产

SSFramework 自有代码遵循根目录 `LICENSE`。第三方包、字体、插件和图片仍受各自许可证约束。`src/Branding/` 中的图形只是默认品牌资产，消费项目可以替换；业务项目的专属资产、场景和 UI 不属于本仓库。

