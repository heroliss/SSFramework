using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 读取 <see cref="SceneShortcutProfile"/>，把每条场景快捷入口注册成 <c>SSFramework/场景</c> 下的顶部菜单项——
    /// <b>配置驱动的动态菜单</b>。Unity 的 <c>[MenuItem]</c> 是编译期静态的，这里用 <c>UnityEditor.Menu.AddMenuItem</c>
    /// 在运行时按配置动态注册，于是「菜单里有哪些场景」由资产说了算、加场景不用改代码。
    /// </summary>
    /// <remarks>
    /// <b>刷新时机：</b><c>[InitializeOnLoad]</c> 在每次域重载后重建菜单；改了配置后点「↻ 刷新场景菜单」
    /// 或触发一次域重载（改任意脚本）即可刷新。动态项不随域重载持久化，故每次加载都重新注册（不会重复）。<br/>
    /// <b>打开安全性：</b>替换打开前走 <c>SaveCurrentModifiedScenesIfUserWantsTo</c> 保存提示；
    /// Play 模式下先询问退出（退出是异步的，故记下目标、在回到编辑态后再打开），不会静默丢改动。<br/>
    /// <b>Boot 启动覆盖：</b>「从 Boot 场景启动 Play」通过设置 <c>EditorSceneManager.playModeStartScene</c> 实现——
    /// 这是个不跨域重载持久化的编辑器状态，故在 <see cref="Rebuild"/>（每次加载）里按配置重新施加。
    /// </remarks>
    [InitializeOnLoad]
    public static class SceneShortcutMenu
    {
        private const string Root = "SSFramework/场景/";
        private const string PlaySub = "▶ 打开并 Play/";
        private const string BootToggle = Root + "从 Boot 场景启动 Play";

        // 本次注册的所有动态项名，刷新时先逐个移除、再重建（避免残留旧项 / 改名后的孤儿）。
        private static readonly List<string> _registered = new();

        // Play 模式下请求打开场景时，退出 Play 是异步的：记下目标，回到编辑态后再打开。
        private static (string path, bool additive, bool andPlay)? _pendingOpen;

        static SceneShortcutMenu()
        {
            // 延后到导入 / 域重载稳定后再建菜单（避免在导入阶段 CreateAsset / 改菜单报警告）。
            EditorApplication.delayCall += Rebuild;
        }

        // ── 固定菜单项（编译期静态；与动态场景项在同一「场景」子菜单里按 priority 合并）──

        [MenuItem(Root + "⚙ 编辑场景快捷入口", priority = 500)]
        private static void EditProfile()
        {
            var profile = SceneShortcutProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }

        [MenuItem(Root + "↻ 刷新场景菜单", priority = 501)]
        private static void RefreshMenu() => Rebuild();

        [MenuItem(BootToggle, priority = 400)]
        private static void ToggleBootPlay()
        {
            var profile = SceneShortcutProfile.Resolve();
            profile.PlayFromBootScene = !profile.PlayFromBootScene;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            if (profile.PlayFromBootScene && profile.BootScene == null)
                Debug.LogWarning("[场景快捷入口] 已开启「从 Boot 场景启动 Play」，但配置里没有指定 Boot 场景——" +
                                 "请在「⚙ 编辑场景快捷入口」里给 Boot Scene 赋值，否则不生效。");

            ApplyPlayFromBoot(profile);
        }

        [MenuItem(BootToggle, true)]
        private static bool ToggleBootPlayValidate()
        {
            // validate 频繁触发（菜单每次打开），只读不创建，用 Find。
            var profile = SceneShortcutProfile.Find();
            Menu.SetChecked(BootToggle, profile != null && profile.PlayFromBootScene);
            return true;
        }

        // ── 动态重建 ──

        private static void Rebuild()
        {
            if (!MenuBridge.Available)
            {
                Debug.LogError("[场景快捷入口] 反射内部菜单 API 失败，动态场景菜单不可用（固定项仍在）——" +
                               "多半是 Unity 升级后 UnityEditor.Menu 的方法签名变了，请核对 MenuBridge。");
                return;
            }

            // 先撤掉上一轮注册的动态项。
            foreach (var name in _registered)
                MenuBridge.Remove(name);
            _registered.Clear();

            var profile = SceneShortcutProfile.Resolve();

            int order = 0;
            foreach (var entry in profile.Entries)
            {
                if (entry.Scene == null) continue; // 空槽位忽略
                string path = AssetDatabase.GetAssetPath(entry.Scene);
                if (string.IsNullOrEmpty(path)) continue;

                string label = string.IsNullOrEmpty(entry.DisplayName) ? entry.Scene.name : entry.DisplayName;
                string group = string.IsNullOrEmpty(entry.Group) ? string.Empty : entry.Group + "/";
                bool additive = entry.OpenAdditive;

                // 打开：平铺（或按分组）挂在「场景」下。
                AddDynamic(Root + group + label, 100 + order,
                    () => OpenScene(path, additive, andPlay: false));

                // 打开并 Play：并列一个「▶ 打开并 Play」子菜单，同样支持分组。
                AddDynamic(Root + PlaySub + group + label, 300 + order,
                    () => OpenScene(path, additive, andPlay: true));

                order++;
            }

            // Boot 启动覆盖：每次加载按配置重新施加（该状态不跨域重载持久化）。
            ApplyPlayFromBoot(profile);
        }

        private static void AddDynamic(string name, int priority, Action action)
        {
            MenuBridge.Add(name, priority, action);
            _registered.Add(name);
        }

        // UnityEditor.Menu.AddMenuItem / RemoveMenuItem 在本 Unity 版本仍是 internal（公开的只有 SetChecked 等少数）——
        // 而「配置驱动的动态顶部菜单」没有任何公开 API，只能反射这两个多年稳定的内部方法。集中封装 + 缓存 MethodInfo，
        // 把脆弱面收敛到这一处；签名核对：AddMenuItem(name, shortcut, checked, priority, Action, Func<bool>) / RemoveMenuItem(name)。
        private static class MenuBridge
        {
            private static readonly System.Reflection.MethodInfo _add = typeof(Menu).GetMethod(
                "AddMenuItem",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string), typeof(bool), typeof(int), typeof(Action), typeof(Func<bool>) },
                null);

            private static readonly System.Reflection.MethodInfo _remove = typeof(Menu).GetMethod(
                "RemoveMenuItem",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);

            /// <summary>两个内部方法都反射到了才可用；否则动态菜单降级、只保留固定项。</summary>
            public static bool Available => _add != null && _remove != null;

            public static void Add(string name, int priority, Action execute) =>
                _add.Invoke(null, new object[] { name, string.Empty, false, priority, execute, null });

            public static void Remove(string name) =>
                _remove.Invoke(null, new object[] { name });
        }

        // ── Boot 启动覆盖 ──

        private static void ApplyPlayFromBoot(SceneShortcutProfile profile)
        {
            EditorSceneManager.playModeStartScene =
                profile != null && profile.PlayFromBootScene && profile.BootScene != null
                    ? profile.BootScene
                    : null;
        }

        // ── 打开场景（含未保存提示 / Play 模式处理）──

        private static void OpenScene(string path, bool additive, bool andPlay)
        {
            if (string.IsNullOrEmpty(path)) return;

            if (EditorApplication.isPlaying)
            {
                if (!EditorUtility.DisplayDialog(
                        "正在运行 Play",
                        "当前处于 Play 模式，需要先退出 Play 才能打开场景。\n\n是否退出 Play 并打开该场景？",
                        "退出并打开", "取消"))
                    return;

                // 退出 Play 是异步的：记下目标，回到编辑态（EnteredEditMode）后再打开。
                _pendingOpen = (path, additive, andPlay);
                EditorApplication.playModeStateChanged += OpenAfterExitPlay;
                EditorApplication.isPlaying = false;
                return;
            }

            DoOpen(path, additive, andPlay);
        }

        private static void OpenAfterExitPlay(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= OpenAfterExitPlay;
            if (_pendingOpen is { } pending)
            {
                _pendingOpen = null;
                DoOpen(pending.path, pending.additive, pending.andPlay);
            }
        }

        private static void DoOpen(string path, bool additive, bool andPlay)
        {
            // 替换打开会卸载当前场景——先走 Unity 的「是否保存」提示；用户点取消则中止。
            // 附加打开不动当前场景，无需保存提示。
            if (!additive && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var mode = additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
            var scene = EditorSceneManager.OpenScene(path, mode);
            if (!scene.IsValid())
            {
                Debug.LogWarning($"[场景快捷入口] 打开场景失败：{path}");
                return;
            }

            // Play 直启：开关若开着，playModeStartScene 会把起点重定向到 Boot（全局设定，符合预期）。
            if (andPlay) EditorApplication.isPlaying = true;
        }
    }
}
