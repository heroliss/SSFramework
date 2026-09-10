# SSFramework 包源配置工具

首次安装前运行，自动补齐工程的 OpenUPM Scope。无需先安装 SSFramework、Node.js、OpenUPM CLI 或其他 PowerShell 模块。工具只配置包源，Framework 的 Git URL 仍由使用者在 Unity Package Manager 中添加。

当前工具没有可选包选择器，也不安装 Unity MCP。Framework 根 `package.json` 中的依赖由 UPM 自动解析；按功能模块选择依赖与可选开发工具属于[初始化规划](../docs/project-startup.md#依赖与可选能力)，尚未实现。

## Windows 双击运行

1. 关闭需要配置的 Unity 工程。
2. 双击 `Configure-OpenUPM.cmd`。
3. 粘贴 **Unity 工程根目录**，例如 `D:\Games\MyGame`，然后按 Enter。
4. 查看工程路径、Registry URL 和待补充的 Scope，输入 `y` 应用；其他输入只预览。
5. 重新打开 Unity，在 Package Manager 中添加已审查的 Framework Git URL。

保留 `.cmd` 和同目录的 `.ps1` 文件。启动器使用 Windows 自带 PowerShell 5.1；执行策略选项只作用于本次进程，不修改系统执行策略，也不下载或执行远端脚本。PowerShell 7 也可以直接运行下列命令。

## 命令行

默认只读预览：

```powershell
& 'D:\Source\SSFramework\Tools~\Configure-OpenUPM.ps1' -ProjectPath 'D:\Games\MyGame'
```

加 `-Apply` 才写入；`-Apply -WhatIf` 仍只预览。`-Interactive` 提供与双击入口相同的路径输入和确认流程。

## 修改范围与恢复

- 只合并 `Packages/manifest.json` 中的 `scopedRegistries`；JSON 缩进可能变化。
- 保留依赖版本、其他 Registry、已有 Scope、`testables` 和其他配置字段；不写 `packages-lock.json` 或 `ProjectSettings`。
- 已有 OpenUPM 时只补缺少的 Scope；已有命名空间 Scope 足以覆盖依赖时不重复添加。
- 原文件通过原子替换备份到 `UserSettings/SSFrameworkSetup/manifest-<唯一编号>.json`，工具会输出具体位置。重复运行且配置已完整时，不写文件、不创建备份。
- 若其他 Registry 已声明相同包名或 `org.nuget` 的更具体 Scope，先在 Package Manager 中解决来源冲突；工具不会擅自覆盖该选择。
- Unity 持有工程锁时，写入会失败；关掉该工程再运行即可。配置工具不关闭编辑器、不删除锁文件。

如需撤销，关闭 Unity，对比备份与当前 `manifest.json`：仅撤销工具添加的 Registry / Scope；只有确认之后没有其他清单改动时，才用完整备份覆盖。备份只保存在本机，Unity 后续的包下载、签名提示、编译与安装验收仍由 Package Manager 负责。

## 工具维护与验证

在 PowerShell 中运行，无需 Pester 或 Unity：

```powershell
& './Tools~/Test-Configure-OpenUPM.ps1'
```

可选 `-ConsumerProject '<工程根目录>'` 会对真实工程做只读预览，再对原始清单的副本验证写入；不会安装包或改真实工程的清单。测试夹具写入系统临时目录，可通过 `-OutputDirectory` 指定位置。测试同时核对根 `package.json` 的所有非 Unity 依赖都有 Scope 覆盖，依赖变更后应重跑。

`Tools~` 是包外引导工具的分发目录；Unity 忽略以 `~` 结尾的目录，不为这些文件生成 `.meta`，也不会将脚本编译或自动执行。
