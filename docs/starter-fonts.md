# 可选简体中文入门字体

需要尽快显示中文时，可以采用独立的 [简体中文字体导入包](https://github.com/heroliss/SSFramework/releases/latest/download/SSFramework-Fonts-zh-CN.unitypackage)。它提供可直接使用的 TMP 与 UI Toolkit 字体资产，无需先学习字体图集烘焙；也可以完全自行选择字体。

这是可选的项目资源，普通 Framework UPM 安装和安装工具不会自动下载或应用它。导入后由项目维护，后续框架升级不会重写字体或全局设置。

## 导入与使用

1. 在 Unity 6.3 安装 SSFramework，等待包解析和编译完成。
2. 使用 TMP 时，先执行 **Window → TextMeshPro → Import TMP Essential Resources**。本字体包引用 TMP 标准 Shader，避免再分发一份会覆盖项目的 TMP 资源。
3. 下载字体包，在 **Assets → Import Package → Custom Package** 选择它，核对导入列表。目标为 `Assets/ThirdParty/SSFrameworkFonts/zh-CN`；已有同路径资源时先检查用户改动和版本，避免覆盖自己的字体调优。
4. 根据 UI 后端接线：

| UI 后端 | 使用哪个资产 | 最短操作 |
|---|---|---|
| TMP / UGUI | `NotoSansSC-Regular TMP` | 赋给 TMP 文本组件的 **Font Asset** |
| UI Toolkit | `NotoSansSC-Regular TextCore` | 赋给 UI Builder 的 **Font Definition**；代码中可用 `FontDefinition.FromSDFFont(fontAsset)` |

两套资产类型不同，不能混用。UI Toolkit 的 PanelSettings / Theme 和项目 UI 仍需正常配置；本包不生成界面或场景，也不修改 TMP Settings 和所有现有文本的默认字体。

字体源 `.otf` 必须保留。资产采用 **Dynamic**，运行时按实际文字生成图集，使用的是随包字体源；它与依赖电脑安装字体的 DynamicOS 不同。动态字体的源文件会随被引用的资产进入构建，且字形生成需要运行时开销，见 [Unity 动态字体说明](https://docs.unity3d.com/Packages/com.unity.textmeshpro@3.0/manual/FontAssetsDynamicFonts.html)。

## 用作补充字体

如果项目已有主字体，可在其 **Fallback Font Asset Table** 中追加对应类型的入门字体，保留原有链顺序。

已采用框架本地化时，也可使用 `MonoLocaleFonts`：列出实际使用的主字体，在 `zh-CN` 档案的 TMP / Toolkit 补充字体栏填入上述资产；所属 Context 需要提供 `ILocalizationUtility`。组件随 locale 切换链条，并在释放时还原原表。详细生命周期与双后端接线见[字体模块指南](framework-guide.md#22-字体多语言字体链)。

不要为同一主字体重复配置多套互相竞争的管理方式。包内没有默认 OS 字体候选；需要更多语言或用户输入字符时，按项目的覆盖需求补充字体。

## 来源、覆盖与体积

字体为 **Noto Sans SC Regular 2.004**，源文件未经修改，采用简体中文地区字形，来源为 Noto 官方仓库的地区子集。[官方格式说明](https://github.com/notofonts/noto-cjk/blob/main/Sans/README.md)区分了地区子集和更完整的语言字体；本包不承诺完整中日韩、所有生僻字或 Emoji。

- 原始 OTF：8,331,336 字节，约 **7.95 MiB**；固定来源和 SHA-256 见[来源清单](../Tools~/Fonts/source.json)。
- 图集设置：48 px 采样、5 px Padding、SDFAA、1024×1024、允许多图集，构建时清空动态数据。
- 单张 1024² Alpha8 图集像素数据约 **1 MiB**。实际字形增多可能生成更多图集，CPU 副本、材质和引擎开销另计，不能当作总显存或最终包体。
- 验证文案包含中文标点、全角数字、“龘”和“臺灣”；**“𠮷”（U+20BB7）不在这份字体源内**。放大图集不能补出源文件没有的字形，应添加合适的字体。

已知 UI 文案较多且稳定后，可以使用框架字集工具烘焙静态主字体，再保留动态字体作为补充；不建议在首次接入时烘焙所有汉字。

字体及其生成资产遵循 **SIL Open Font License 1.1**，不是框架 MIT 许可。导入包附带 OFL.txt、NOTICE.txt 和使用说明，游戏发行物也应提供版权与许可。字体原始版权记录为 © 2014–2021 Adobe，许可条件见[固定来源的 OFL 原文](https://github.com/notofonts/noto-cjk/blob/523d033d6cb47f4a80c58a35753646f5c3608a78/LICENSE)。

## 验证范围

接入后核对文字实际显示、Console 缺字提示、字号与分辨率、Play 重进、实际字体链和目标 Player。字体资源导入成功不能代替渲染验收。

当前发行的具体验证记录见[接入指南](consuming-framework.md#v015-可选中文字体资源)；维护者重建包的步骤见[字体发行配方](../Tools~/Fonts/README.md)。
