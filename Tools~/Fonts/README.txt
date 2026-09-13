SSFramework 可选简体中文入门字体

内容：Noto Sans SC Regular 2.004 原始 OTF、TMP 字体资产、TextCore 字体资产、OFL 许可与来源记录。
两套 Unity 字体资产分别使用，均为 Dynamic：48 px 采样、5 px Padding、SDFAA、1024×1024、允许多图集。
字体源必须保留。动态图集按实际文本增长，不预烘焙完整字库，也不依赖玩家电脑安装系统字体。

导入前
1. 安装 SSFramework 并等待编译完成，当前目标为 Unity 6.3。
2. 使用 TMP 时，先执行 Window > TextMeshPro > Import TMP Essential Resources。
   本字体包引用 TMP 的标准 Mobile Distance Field Shader，不重复分发或覆盖 TMP 资源。
3. 在 Assets > Import Package > Custom Package 选择本字体包，检查导入列表后导入。
   如果同路径已有资源，先核对版本与项目修改；不要用重新导入覆盖自己的字体调优。

最短使用路径
- TMP / UGUI：把 NotoSansSC-Regular TMP 赋给文本组件的 Font Asset。
- UI Toolkit：把 NotoSansSC-Regular TextCore 赋给 UI Builder 中的 Font Definition。
两套资产不能混用。只使用一个 UI 后端时，另一套资产可不使用。
本包不修改全局默认字体、场景或 TMP Settings，不会让所有现有文本自动切换字体。

作为 fallback
在项目主字体资产的 Fallback Font Asset Table 中追加对应类型的入门字体。
已采用 SSFramework 本地化时，也可在 MonoLocaleFonts 的 zh-CN 档案中配置 TMP / Toolkit 补充字体，
并列出实际使用的主字体；组件还需要所属 Context 提供 ILocalizationUtility。
两种方式按项目选择，避免对同一个字体重复管理。字体链被释放时会还原原表。

覆盖范围与开销
本包优先使用简体中文地区字形，不承诺完整中日韩、所有生僻字或 Emoji。
已验证的“月球基地”、中文标点、全角数字、“龘”“臺灣”可以显示；“𠮷”（U+20BB7）不在此字体源内。
缺字应增加合适的补充字体，不能通过放大图集弥补字体源不存在的字形。
原始 OTF 约 7.95 MiB。已引用的动态字体会把源文件带入 Player，字形生成也有运行时成本。
单张 1024×1024 Alpha8 图集像素数据约 1 MiB，多图集增长后另行评估；这不是总显存或最终安装包体积。
正式项目可用 SSFramework 字集工具为已知 UI 文案烘焙静态主字体，再保留本包作为补充。

验证时检查 Console 缺字提示、真实字体引用、不同字号与分辨率、重复进入 Play，以及目标 Player。
保留 OFL.txt 和 NOTICE.txt，并在游戏发行物中提供字体的版权与许可，不把此字体改标为框架的 MIT 许可。

来源与校验见 NOTICE.txt；完整安装说明：https://github.com/heroliss/SSFramework/blob/main/docs/starter-fonts.md
