using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 场景快捷入口配置（全工程单例）：声明「哪些场景要出现在 <c>SSFramework/场景</c> 顶部菜单里」。
    /// 由 <see cref="SceneShortcutMenu"/> 在编辑器加载时读取、逐条注册成菜单项——
    /// <b>加一个快捷入口 = 往这里加一行，不用改任何代码</b>。以后想加自己项目的场景直接编辑本资产即可。
    /// </summary>
    /// <remarks>
    /// 全工程单例（只在场景工作台明确点击创建，落到通用项目配置目录；项目配置不进框架包——ADR-0011）；
    /// 误建多份时 <see cref="Resolve"/> 取第一份并警告（同 <c>FontCharsetProfile</c> 等的单例语义）。<br/>
    /// 首次创建会把 Build Settings 中已启用的有效场景作为初始快捷入口，用户可自由增删 / 清空。
    /// 场景存 <see cref="SceneAsset"/> 引用（内部是 GUID），改名 / 移动不断链。
    /// </remarks>
    public sealed class SceneShortcutProfile : ScriptableObject
    {
        private static int _duplicateWarningRevision = -1;

        /// <summary>一条场景快捷入口 = 菜单里的一个「打开场景」项。</summary>
        [Serializable]
        public sealed class SceneEntry
        {
            [Tooltip("要打开的场景资产（存 GUID，场景改名 / 移动不断链）。留空的项会被菜单忽略。")]
            [InspectorName("场景")]
            public SceneAsset Scene;

            [Tooltip("菜单显示名（留空 = 用场景文件名）。")]
            [InspectorName("菜单显示名")]
            public string DisplayName;

            [Tooltip("分组子菜单名（留空 = 直接挂在「场景」下）。\n填 Gameplay → 菜单落到 SSFramework/场景/Gameplay/xxx，条目多了不乱。")]
            [InspectorName("分组子菜单")]
            public string Group;

            [Tooltip("勾选 = 附加打开（Additive，多场景编辑、不卸载当前场景，如 Boot + 玩法场景同开）；\n不勾 = 替换打开（Single，先按提示保存当前场景）。")]
            [InspectorName("附加打开（Additive）")]
            public bool OpenAdditive;
        }

        [Tooltip("菜单里要显示的场景快捷入口。加一行即多一个菜单项——" +
                 "改完到 SSFramework/开发辅助/场景快捷入口 点“刷新菜单”，或触发一次域重载即可生效。")]
        [InspectorName("场景快捷入口")]
        [SerializeField] private List<SceneEntry> _entries = new();

        [Space(6)]
        [Header("从 Boot 场景启动 Play（贴合本框架 Boot / 热更架构）")]
        [Tooltip("勾选后：从任何场景按 Play 都先跑 Boot 场景（HybridCLR 引导流程）再进——" +
                 "无需每次手动切回 Boot。取消勾选恢复 Unity 默认（从当前场景直接 Play）。\n" +
                 "也可在 SSFramework/开发辅助/场景快捷入口 工作台切换。")]
        [InspectorName("从 Boot 场景启动 Play")]
        [SerializeField] private bool _playFromBootScene;

        [Tooltip("Boot 场景资产：上面开关打开时，它就是 Play 的起始场景。")]
        [InspectorName("Boot 场景")]
        [SerializeField] private SceneAsset _bootScene;

        /// <summary>菜单要渲染的场景快捷入口（只读视图；编辑请在 Inspector 改本资产）。</summary>
        public IReadOnlyList<SceneEntry> Entries => _entries;

        /// <summary>是否从 Boot 场景启动 Play。菜单开关会写它并落盘。</summary>
        public bool PlayFromBootScene
        {
            get => _playFromBootScene;
            set => _playFromBootScene = value;
        }

        /// <summary>Boot 场景资产（<see cref="PlayFromBootScene"/> 为真时作为 Play 起始场景）。</summary>
        public SceneAsset BootScene => _bootScene;

        /// <summary>定位全工程唯一配置；不存在返回 <c>null</c>（不创建，供 validate 等只读场景用）。</summary>
        public static SceneShortcutProfile Find()
        {
            if (!FrameworkEditorProfileCatalog.TryResolveFirst(
                    out SceneShortcutProfile profile, out IReadOnlyList<string> paths))
                return null;

            // CreateAssetMenu 未开放（单例，不该手建多份）；万一按类型命中多份仍明确警告，避免「改了不生效」难排查。
            int revision = FrameworkEditorProfileCatalog.Revision;
            if (paths.Count > 1 && _duplicateWarningRevision != revision)
            {
                _duplicateWarningRevision = revision;
                Debug.LogWarning("[场景快捷入口] 找到多个配置，仅第一个生效，请删到只剩一个：\n  " +
                                 string.Join("\n  ", paths));
            }
            return profile;
        }

        /// <summary>定位配置；不存在则创建并从 Build Settings 导入初始场景。此写入 API 只供工作台明确创建动作调用。</summary>
        public static SceneShortcutProfile Resolve()
        {
            var existing = Find();
            if (existing != null) return existing;

            FrameworkEditorProfileCatalog.Refresh(typeof(SceneShortcutProfile));
            existing = Find();
            if (existing != null) return existing;
            string dir = FrameworkProjectSettingsLocation.EnsureDirectory();
            string path = dir + "/SceneShortcutProfile.asset";
            existing = FrameworkProjectSettingsLocation.GetExistingProfileOrThrow<SceneShortcutProfile>(path);
            if (existing != null) return existing;

            var profile = CreateInstance<SceneShortcutProfile>();
            profile.SeedDefaults();
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            FrameworkEditorProfileCatalog.Refresh(typeof(SceneShortcutProfile));
            SceneShortcutProfile effective = Find();
            if (effective == null || effective != profile)
                throw new InvalidOperationException(
                    $"场景快捷入口配置已写入但未成为稳定排序后的生效项：{path}。请检查重复配置后重试。");
            Debug.Log($"[场景快捷入口] 已按用户请求创建配置：{path}。" +
                      $"从 Build Settings 导入了 {profile.Entries.Count} 个已启用场景；可在 Inspector 自由增删。");
            return effective;
        }

        // Build Settings 是 Unity 自己维护的项目场景清单，能提供通用且可解释的初始值；
        // 不猜测项目目录、场景命名或业务分组。第一项仅作为 Boot 候选，开关仍默认关闭。
        private void SeedDefaults() => SeedFromBuildSettings(EditorBuildSettings.scenes);

        internal void SeedFromBuildSettings(IEnumerable<EditorBuildSettingsScene> scenes)
        {
            _entries.Clear();
            _bootScene = null;
            _playFromBootScene = false;
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in scenes ?? Enumerable.Empty<EditorBuildSettingsScene>())
            {
                if (setting == null || !setting.enabled || string.IsNullOrWhiteSpace(setting.path) ||
                    !addedPaths.Add(setting.path))
                    continue;

                var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(setting.path);
                if (scene == null) continue;
                _entries.Add(new SceneEntry { Scene = scene });
                _bootScene ??= scene;
            }
        }
    }
}
