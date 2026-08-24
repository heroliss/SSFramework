#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Framework.Logging;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 为无人值守的 Editor 操作准备一个可恢复、无交互的起点。AI/MCP 在启动 PlayMode 测试前调用
    /// <see cref="PreparePlayModeTests"/>，即可保存已有资产路径的脏场景，避免 Unity 的原生保存弹窗阻塞 MCP 主线程队列。
    /// </summary>
    /// <remarks>
    /// 这里刻意不监听所有 PlayMode 切换：全局自动保存会把开发者尚未决定保留的人工编辑静默落盘。
    /// 自动化必须显式选择这个入口；未命名场景也只会 fail-fast，不会打开“另存为”窗口。
    /// </remarks>
    public static class FrameworkAutomationPreflight
    {
        /// <summary>供 Unity MCP <c>unity_execute_menu_item</c> 调用的稳定菜单路径。</summary>
        public const string PlayModeTestsMenuPath =
            "SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）";

        /// <summary>
        /// 保存所有已加载、已有资产路径的脏场景，为随后进入 PlayMode 建立无弹窗前置条件。
        /// </summary>
        /// <returns>本次实际保存的场景资产路径；无脏场景时返回空数组。</returns>
        /// <exception cref="InvalidOperationException">
        /// Editor 正在编译、更新、进入/处于 PlayMode，存在未命名脏场景，或某个场景保存失败时抛出。
        /// 任一未命名场景会在开始保存前被发现，避免只保存一半后才弹窗。
        /// </exception>
        public static IReadOnlyList<string> PreparePlayModeTests()
        {
            EnsureEditorIsReady();

            var dirtyScenes = CollectDirtyScenes();
            var savedPaths = SaveDirtyScenesAfterValidation(dirtyScenes);

            string detail = savedPaths.Count == 0
                ? "没有脏场景"
                : $"已保存 {savedPaths.Count} 个场景：{string.Join(", ", savedPaths)}";
            Log.Info($"[SSFramework.Automation] READY — {detail}。现在可以启动 PlayMode 测试。",
                "EditorAutomation");
            return savedPaths;
        }

        /// <summary>
        /// 先验证整批场景都已有资产路径，再开始逐个保存。拆出这一层是为了让测试直接锁定
        /// “发现未命名场景时一个也不保存”的事务顺序，而不依赖 Editor 当前加载场景的偶然状态。
        /// </summary>
        internal static IReadOnlyList<string> SaveDirtyScenesAfterValidation(IReadOnlyList<Scene> dirtyScenes)
        {
            if (dirtyScenes == null) throw new ArgumentNullException(nameof(dirtyScenes));

            ValidateAllScenesHaveAssetPaths(dirtyScenes);

            var savedPaths = new string[dirtyScenes.Count];
            for (int i = 0; i < dirtyScenes.Count; i++)
            {
                var scene = dirtyScenes[i];
                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException(
                        $"PlayMode 自动化预检无法保存场景 '{scene.path}'，测试尚未启动。");

                savedPaths[i] = scene.path;
            }

            return savedPaths;
        }

        [MenuItem(PlayModeTestsMenuPath, priority = 200)]
        private static void PreparePlayModeTestsFromMenu()
        {
            try
            {
                PreparePlayModeTests();
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[SSFramework.Automation] BLOCKED — PlayMode 测试预检失败；测试未启动。",
                    exception,
                    "EditorAutomation");
                throw;
            }
        }

        private static void EnsureEditorIsReady()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException(
                    "PlayMode 自动化预检只能在 Edit Mode 执行；请先停止当前 PlayMode。");

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException(
                    "Unity 正在编译或刷新资产；请等待 Editor 空闲后再运行 PlayMode 自动化预检。");
        }

        private static List<Scene> CollectDirtyScenes()
        {
            var dirtyScenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                    dirtyScenes.Add(scene);
            }

            return dirtyScenes;
        }

        internal static void ValidateAllScenesHaveAssetPaths(IReadOnlyList<Scene> dirtyScenes)
        {
            var untitledNames = new List<string>();
            for (int i = 0; i < dirtyScenes.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(dirtyScenes[i].path))
                    untitledNames.Add(string.IsNullOrWhiteSpace(dirtyScenes[i].name)
                        ? $"第 {i + 1} 个未命名场景"
                        : dirtyScenes[i].name);
            }

            if (untitledNames.Count == 0) return;

            throw new InvalidOperationException(
                $"发现未命名的脏场景（{string.Join(", ", untitledNames)}）。" +
                "自动化不会猜测保存路径；请先人工命名并保存，再重新运行预检。");
        }
    }
}
#endif
