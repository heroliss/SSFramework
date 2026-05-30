# Unity MCP 工具调用陷阱与最佳实践

这份文档汇总在 SSFramework 项目里使用 `unityMCP` 工具时踩过的坑——LLM 调用 MCP 时容易反复踩同样的坑，集中记录避免重复。

截图相关单独写在 [.claude/skills/unity-screenshot/SKILL.md](../.claude/skills/unity-screenshot/SKILL.md)。

---

## 1. `manage_gameobject create` 的 `component_properties` 经常不生效

**现象**：创建 GameObject 时通过 `component_properties` 同时设置组件属性，发现 `Image.color`、`Canvas.renderMode`、`TextMeshProUGUI.text` 等枚举/颜色/字符串字段**完全没生效**——值还是默认值。

**原因**：工具对枚举字符串映射、嵌套 Color 对象的解析不可靠。

**对策**：分两步——
1. `manage_gameobject create` 只用来挂组件
2. 紧接着用 `manage_components set_property` 或 `execute_code` 显式设属性

```
// ❌ 不可靠
manage_gameobject create components_to_add=[Image] component_properties={Image:{color:{r:0.1,...}}}

// ✅ 可靠
manage_gameobject create components_to_add=[Image]
manage_components set_property component_type=Image property=color value={r:0.1,...}
```

---

## 2. `manage_scriptable_object` 不支持 `Array.size`

**现象**：用 `patches=[{path:"_list.Array.size", value:7}]` 设置数组大小，工具返回 `Unsupported SerializedPropertyType: ArraySize`。直接写 `_list.Array.data[0]._field` 也会失败（数组还没扩展到那个 index）。

**对策**：先用 `execute_code` 扩展数组，再用 `manage_scriptable_object modify` 填字段：

```csharp
var so = new UnityEditor.SerializedObject(asset);
var list = so.FindProperty("_chapters");
list.arraySize = 7;
// 嵌套数组也一样
for (int i = 0; i < 7; i++)
    list.GetArrayElementAtIndex(i).FindPropertyRelative("_codeSnippets").arraySize = 2;
so.ApplyModifiedProperties();
UnityEditor.AssetDatabase.SaveAssets();
```

---

## 3. `execute_code` 是 method body 模式，不允许 `using` 和顶层别名

**现象**：`using UnityEngine.UI;` 或 `using GO = UnityEngine.GameObject;` 编译失败。

**对策**：所有类型全限定写：

```csharp
// ❌ 不行
using UnityEngine.UI;
var img = bg.AddComponent<Image>();

// ✅ 全限定
var img = bg.AddComponent<UnityEngine.UI.Image>();
```

可以用 `System.Func` / `System.Action` 在方法体内当 lambda 别名复用代码片段。

---

## 4. `Game.Framework.System` 命名空间冲突会让 `System.X` 解析到错处

**现象**：项目里有 `Game.Framework.System` 命名空间。`execute_code` 里写 `System.Threading.Thread` 不会被解析到 `global::System.Threading.Thread`——会去找 `Game.Framework.System.Threading.Thread`（找不到）。

**对策**：
- 顶层 `System.Xxx` 一般 OK（顶层 `System` 是 global namespace 别名）
- 嵌套时用 `global::System.X` 兜底
- 或者把代码片段放在 `manage_components` 等不需要写代码的工具里
- 框架源码里参考 [AGENTS.md §6](../Assets/Game/AGENTS.md) 的处理

---

## 5. `manage_components set_property` 设对象引用要走 instance ID / GUID / 资产路径

**现象**：`set_property value="TitleText"` 给 TMP_Text 字段——这会把字符串当对象名查找，但跨组件类型时不可靠。

**对策**：对象引用走 GUID 或 execute_code：

```csharp
// ScriptableObject 引用：value={"guid": "366179623a..."}
manage_scriptable_object modify patches=[{path:"_copy", value:{"guid":"..."}}]

// Scene 内 GameObject/Component 引用：用 execute_code + SerializedObject
var so = new UnityEditor.SerializedObject(card);
so.FindProperty("_title").objectReferenceValue = titleGo.GetComponent<TMPro.TMP_Text>();
so.ApplyModifiedProperties();
UnityEditor.EditorUtility.SetDirty(card);
```

---

## 6. Editor 模式下 LayoutGroup 不会自动重绘

**现象**：通过 execute_code 修改 TMP.text 或调 `ConceptCardView.Render()` 之后，Scene 看起来 UI 内容消失或没变化。

**原因**：Unity Editor 的 Canvas / LayoutGroup 不会在外部代码修改后立即重绘，需要事件触发（如鼠标点击 Editor 窗口）。

**对策**：execute_code 末尾加一组强制刷新：

```csharp
UnityEngine.Canvas.ForceUpdateCanvases();
UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(content.GetComponent<UnityEngine.RectTransform>());
UnityEditor.SceneView.RepaintAll();
UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
```

**注意**：PlayMode 下没这个问题——Awake/Update 会自动触发刷新。如果只关心 PlayMode 行为，Editor 显示延迟可以忽略。

---

## 7. `manage_scene save` 后场景仍可能被 Editor 端 Discard

**现象**：MCP 一连串改动 + `save` 之后，用户在 Unity Editor 里点了 "Discard Changes"，导致部分改动丢失。返回后 hierarchy 出现：旧物体没删干净 + 部分新物体留下（不一致状态）。

**对策**：
1. 每次大段改动后立即 `manage_scene save`
2. 用 `manage_scene get_hierarchy` 校验现状再继续
3. 提醒用户：MCP 修改之后请勿 Editor 端 Discard

---

## 8. UnityEditor 命名空间的 `EditorSceneManager` 在 `UnityEditor.SceneManagement`

**现象**：execute_code 写 `UnityEditor.EditorSceneManager.SaveOpenScenes()` 编译失败。

**对策**：全限定 `UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes()`。

---

## 9. 用文件工具新建 .cs 偶发"不进编译列表"，宁可用 `create_script`

**现象**：用 Write/外部工具一次性新建多个 .cs 后 `refresh_unity` 编译，偶尔**有一个文件没被加入程序集编译**——同命名空间、同文件夹的其它文件报 `CS0246 type not found`，但那个文件**自身没有任何报错**。查 Unity 生成的 `*.csproj` 会发现它不在 `<Compile Include>` 列表里（其它新文件都在）。

**已排除**：源码正确、namespace 一致、GUID 无冲突、编码无 BOM。删 `.meta`、删文件重建、`refresh_unity scope:all mode:force` 都**修不好**——它跟着"这个文件"走。

**对策**：
1. **优先用 MCP `create_script` 新建脚本**（走 Unity 脚本创建 API，导入更可靠），而不是 Write + refresh。
2. 已经踩坑：把该类型**并入一个确认在编译列表里的同命名空间文件**即可绕过（实测有效）。
3. 判定方法：`grep '<Compile Include' *.csproj` 看缺哪个文件，对照 `read_console` 的 CS0246。

**根因推测**：MCP 的 `refresh_unity` 对"新增资产"的导入不如 Unity 原生 `AssetDatabase.Refresh` 彻底，批量新增脚本时偶发漏掉一个的资产导入登记。

## 10. 测试是 PlayMode 程序集，必须 `run_tests mode:PlayMode`

`Game.Framework.Test` asmdef 只引用 `UnityEngine.TestRunner`、不含 Editor 平台 → 整个程序集按 PlayMode 处理，连 `[Test]` 方法也在 PlayMode 跑。`mode:EditMode` 会返回 `total:0 / Passed`（假"全过"，实为没发现测试）。用 `mode:PlayMode` + `init_timeout:120000`。

## 维护说明

这份文档是"经验沉淀"。每次踩到新坑、确认调用方式后，追加一节即可。已经写进 [.claude/skills/unity-screenshot/SKILL.md](../.claude/skills/unity-screenshot/SKILL.md) 的截图细节不在这里重复。
