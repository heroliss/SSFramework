# 入门字体的发行维护

使用者直接下载可选 `.unitypackage`，按[字体接入说明](../../docs/starter-fonts.md)导入；无需运行本目录的构建代码。字体源与生成资产通过独立 Release 附件分发，不放进 Framework Git 包，所以只安装 Framework 不会下载约 8 MiB 的 OTF。

本目录保留固定来源、校验值、许可、包内使用说明和经真实消费工程验证的 Unity 构建配方。`FontPackBuilder.cs` 位于 `Tools~`，UPM 不导入、不编译、不执行它；不是新的 Runtime API 或自动执行的安装脚本。

## 重建配方

1. 使用已安装 Framework 和 TMP Essential Resources 的 Unity 6.3 验证工程；保存现场，保持 Edit mode。
2. 从 `source.json` 的固定地址下载 OTF 到工程外的临时输入目录。把本目录的 `OFL.txt`、`NOTICE.txt`、`README.txt` 一并放入；保留原始文件字节，创建器校验字体和许可的 SHA-256。
3. 在验证工程的 Editor 程序集中临时编译 `FontPackBuilder.cs`，该程序集需引用 `Unity.TextMeshPro`，并配置 C# 10。通过 Unity API 创建脚本及 meta；不修改 PackageCache。
4. 调用 `SSFramework.FontDistribution.FontPackBuilder.Create(inputDirectory, "Assets/ThirdParty/SSFrameworkFonts/zh-CN")`。必须使用尚不存在的目标目录；创建器不覆盖或合并已有字体。现有发行资产应保留自己的 meta / GUID，不用重新生成 GUID 的方式升级用户资产。
5. 验证实际字符覆盖、两套后端、fallback 应用 / 释放与目标 Player。已存在字形先用 `HasCharacter` 检查；当前 TMP 的 `TryAddCharacters` 在无需新增时也可能返回 false，不能单凭它判断缺字。
6. 调用 `FontPackBuilder.Export(assetDirectory, absoluteUnityPackagePath)`。导出前清空两套资产的动态字形数据；只导出四份来源 / 说明文件和两份字体资产，不导出工程场景、PanelSettings、TMP Settings、Shader 或框架源码。同名输出不会覆盖。
7. 检查归档实际内容、源字体哈希、GUID 与字体依赖。在消费工程重新导入并构建普通 IL2CPP Player，确认图集初始为空时仍能动态生成字形；分发包及最终 Player 都应保留字体许可与版权记录。

归档检查可使用 Python 3 标准库运行 `python validate_font_package.py <导入包路径> --report <报告路径>`；不需要安装额外库，也不会解压到工程。它检查六项资产范围、源字体与许可哈希、GUID、源引用及空动态图集声明，不能替代 Unity 渲染验收。

资产参数固定为 48 px、5 px Padding、SDFAA、1024²、Dynamic、多图集及构建时清空动态数据。它是入门设置，不能代替项目字号、描边、分辨率和性能预算的评估。两个后端的资产不能互换；动态图集与源 OTF 都归字体许可范围，生成器自身使用仓库 MIT 许可。

失败保护覆盖已有目标目录、相对路径逃逸、目录链接、源文件缺失、哈希不匹配、Editor 忙碌及已有输出。Unity 创建过程中失败时清理本次新建的叶目录；已存在的父目录、场景和全局文本配置不修改。该维护入口不联网；下载与字体升级需单独审查来源和再分发条件。
