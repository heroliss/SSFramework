# 仓库集成与 Framework 同步

这份文档说明拆分后的仓库边界、`com.liss.ssframework` 的消费方式，以及如何在多个 Unity 工程之间有意地升级 Framework。它是同步流程的唯一入口；各消费仓库的 README 只保留项目特有说明。

## 仓库地图

| 仓库 | 职责 | 是否包含 Framework 源码 |
|---|---|---|
| [SSFramework](https://github.com/heroliss/SSFramework) | 可复用 UPM 包与公共文档 | 是，源码位于 `src/` |
| [Outpost](https://github.com/heroliss/Outpost) | Framework 教程游戏与真实消费示例 | 否，使用子模块 |
| [FrameworkTutorial](https://github.com/heroliss/FrameworkTutorial) | Framework 章节教程与教学场景 | 否，使用子模块 |
| [NomadWorkshop](https://github.com/heroliss/NomadWorkshop) | 独立开发中的正式游戏 | 否，使用子模块 |
| SSFramework-Archive | 旧单体工程的只读历史归档 | 历史副本，不再接收开发 |

`FrameworkTutorial` 是当前 GitHub 远端名称；产品语义上改为 `SSFrameworkTutorial` 更清楚，但 GitHub 仓库改名需要在 GitHub 设置中单独完成。改名之前不要擅自修改现有本地目录、资产路径或程序集名，以免把仓库迁移和 Unity 序列化迁移混在一起。

## 子模块关系

消费仓库的 `.gitmodules` 使用同一个远端地址：

```ini
[submodule "Packages/com.liss.ssframework"]
    path = Packages/com.liss.ssframework
    url = https://github.com/heroliss/SSFramework.git
```

子模块是一个独立 Git 仓库。消费仓库提交的不是 Framework 文件副本，而是 Framework 的一个确定 commit（gitlink）。因此每个游戏或教程都可以复现当时验证过的框架版本，也不会因为 Framework 的新提交突然改变运行结果。

## 升级 Framework 的标准流程

1. 在 `SSFramework` 的 `develop` 完成修改、测试和提交；形成可供消费的 commit 或 SemVer tag。
2. 将该 commit 推送到 `SSFramework` 的远端 `main`，并在需要时合并到 `develop`。
3. 在每个需要升级的消费仓库中更新子模块到明确的 commit：

   ```powershell
   git -C Packages/com.liss.ssframework fetch origin
   git -C Packages/com.liss.ssframework checkout <approved-sha-or-tag>
   git submodule update --init --recursive
   ```

4. 在消费仓库运行与本项目风险相称的编译、测试和运行时验证。
5. 提交新的 gitlink；需要同步兼容性说明时同时更新该仓库的 `docs/framework-compatibility.md`：

   ```powershell
   git add Packages/com.liss.ssframework docs/framework-compatibility.md
   git commit -m "chore(repo): 更新 Framework 子模块"
   git push origin main
   ```

`git submodule update --remote --merge` 可以用于主动跟踪某个分支，但发布和可复现验证优先使用明确 SHA 或 tag。Framework 的新提交不会自动写入消费仓库；必须由消费仓库提交新的 gitlink。这是有意设计的版本边界，而不是同步故障。

## `framework.git` 与 `source.git`

这两个目录是迁移过程中留下的本地 bare repository：

- `D:/SSFramework-Migration/framework.git` 是 Framework 拆分时的本地对象与引用存储，远端指向本地 `framework` worktree。
- `D:/SSFramework-Migration/source.git` 保存旧单体工程 `D:/SSFramework` 的迁移历史，远端指向旧工作区。

它们不是 Unity 项目，也不是消费仓库的运行时依赖。当前项目实际引用的是 GitHub 上的 `SSFramework.git`，路径和版本以各仓库的 `.gitmodules` 与 gitlink 为准。未来如需批量升级，可在 CI 或维护脚本中逐仓库更新并运行验证，但不应通过复制文件或隐式浮动分支制造“自动同步”。

## 分支与发布约定

- `main`：可供其他仓库消费的稳定线。
- `develop`：当前集成线；公共 API 或包布局变更应先在此完成验证。
- `feature/*`：短期工作分支，合并后删除。
- `vMAJOR.MINOR.PATCH`：可复现的 Framework 发布标签；消费仓库在兼容记录中写明实际 SHA 或 tag。

仓库改名、包 ID 改名、程序集/命名空间迁移和 Unity 场景序列化迁移分别执行，并分别验证；不要把它们混成一次不可回滚的“大迁移”。
