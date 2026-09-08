# Odin 依赖边界

SSFramework 的发布基线不包含 Odin Inspector，也不依赖 Sirenix 程序集。框架 Editor 使用 Unity 原生 Inspector、Drawer 和诊断接缝，因此没有购买 Odin 的项目可以直接安装并编译 Framework。

如果某个游戏项目自行购买并安装 Odin，可以在该项目内使用 Odin；这属于项目自己的 Editor 依赖，不属于 `com.liss.ssframework`，也不会进入 Framework 包。

当前仓库已移除 `Game.Framework.Odin.Editor` Adapter。以后只有在至少两个真实项目产生相同需求、并能证明原生基线仍然完整可用时，才重新评估独立的 Odin Adapter 包。
