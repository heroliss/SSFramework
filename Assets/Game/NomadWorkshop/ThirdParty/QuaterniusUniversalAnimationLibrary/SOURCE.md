# Quaternius Universal Animation Library（Standard）

- 作者：Quaternius（@Quaternius）
- 官方页面：https://quaternius.com/packs/universalanimationlibrary.html
- 官方 Itch 页面：https://quaternius.itch.io/universal-animation-library
- 本次取得版本：页面 Changelog `v3.0`（2026-06-16），Unity 导出为 30 FPS，并同时提供 Root Motion / 无 Root Motion 版本
- 本次取得日期：2026-09-01
- 许可证：CC0 1.0 Universal；同目录保留标准包内 `License.txt` 的文字副本。

本次通过 Itch 免费下载流程取得 upload id `17958403` 的 `Universal Animation Library[Standard].zip`：

- 压缩包字节数：`15,904,933`
- 压缩包 SHA-256：`CC73FC4E495B82958207316596317A3F40B9FA38065BDE1027937452DA537724`
- 用于抽取的无 Root Motion 源：`Unity/UAL1_Standard.fbx`
- 源 FBX 字节数：`23,754,684`
- 源 FBX SHA-256：`21B32D912DA3CB93426D974FB945E86F5B2E86970ACD2CE89905E0FBF9F1DCC2`

完整源 FBX 曾在临时目录和项目暂存路径中完成 Unity Humanoid 审计：Avatar 有效且为 Humanoid，免费标准版导入出 43 个 30 FPS Human Motion。仓库最终只保留由 `NomadHumanoidAssetPipeline` 抽取的五个项目原生 `.anim`：

| 项目语义 | 上游 Clip | 项目资产 | Loop |
|---|---|---|---|
| Idle | `Armature\|Idle_Loop` | `Resident_Idle.anim` | 是 |
| Move | `Armature\|Walk_Loop` | `Resident_Walk.anim` | 是 |
| Pickup | `Armature\|PickUp_Table` | `Resident_Pickup.anim` | 否 |
| Work | `Armature\|Fixing_Kneeling` | `Resident_Work.anim` | 否 |
| Rest | `Armature\|Sitting_Idle_Loop` | `Resident_Rest.anim` | 是 |

完整 23.75 MB 动作源和下载压缩包不进入 Git。需要重新抽取时，从官方 Itch 页面下载 Standard 包，把无 Root Motion FBX 临时导入到 `Assets/Game/NomadWorkshop/ThirdParty/QuaterniusUniversalAnimationLibrary/Source/UAL1_Standard.fbx`，再执行菜单 `SSFramework/游牧工坊/配置并审计 Humanoid 资产`；抽取完成后应再次移除 `Source/`。
