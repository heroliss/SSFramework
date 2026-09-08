# SSFramework

SSFramework 是可嵌入 Unity 项目的模块化框架。源码位于 `src/`，作为 UPM 包安装时包根会映射为 `Packages/com.heroliss.ssframework`。

框架不包含任何游戏、教程场景或项目级 `ProjectSettings`。教程与游戏是独立仓库，通过固定版本或 Git submodule 消费本包。

## 开发

- Unity 版本：6000.3.22f1
- 稳定分支：`main`
- 集成分支：`develop`
- 发布使用 SemVer Tag，例如 `v0.1.0`

请先阅读 `docs/framework-module-map.md` 与根目录 `AGENTS.md`。可选 Module 保持独立依赖和删除边界；修改模块、公共 API 或包布局时同步 ADR 与验证证据。
