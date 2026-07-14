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
    /// 全工程单例（首次使用自动创建到 <c>Assets/Game/Settings/</c>，项目配置不进框架包——ADR-0011）；
    /// 误建多份时 <see cref="Resolve"/> 取第一份并警告（同 <c>FontCharsetProfile</c> 等的单例语义）。<br/>
    /// 首次创建会按「开箱默认」<see cref="SeedDefaults"/> 种入工程里已知的几个场景（DemoScene / Outpost）——
    /// 缺失的自动跳过，用户可自由增删 / 清空。场景存 <see cref="SceneAsset"/> 引用（内部是 GUID），改名 / 移动不断链。
    /// </remarks>
    public sealed class SceneShortcutProfile : ScriptableObject
    {
        /// <summary>一条场景快捷入口 = 菜单里的一个「打开场景」项。</summary>
        [Serializable]
        public sealed class SceneEntry
        {
            [Tooltip("要打开的场景资产（存 GUID，场景改名 / 移动不断链）。留空的项会被菜单忽略。")]
            public SceneAsset Scene;

            [Tooltip("菜单显示名（留空 = 用场景文件名）。")]
            public string DisplayName;

            [Tooltip("分组子菜单名（留空 = 直接挂在「场景」下）。\n填 Outpost → 菜单落到 SSFramework/场景/Outpost/xxx，条目多了不乱。")]
            public string Group;

            [Tooltip("勾选 = 附加打开（Additive，多场景编辑、不卸载当前场景，如 Boot + 玩法场景同开）；\n不勾 = 替换打开（Single，先按提示保存当前场景）。")]
            public bool OpenAdditive;
        }

        [Tooltip("菜单里要显示的场景快捷入口。加一行即多一个菜单项——" +
                 "改完点「SSFramework/场景/↻ 刷新场景菜单」或触发一次域重载（改任意脚本）即可生效。")]
        [SerializeField] private List<SceneEntry> _entries = new();

        [Space(6)]
        [Header("从 Boot 场景启动 Play（贴合本框架 Boot / 热更架构）")]
        [Tooltip("勾选后：从任何场景按 Play 都先跑 Boot 场景（HybridCLR 引导流程）再进——" +
                 "无需每次手动切回 Boot。取消勾选恢复 Unity 默认（从当前场景直接 Play）。\n" +
                 "也可在菜单 SSFramework/场景/从 Boot 场景启动 Play 里一键切换（带勾选态）。")]
        [SerializeField] private bool _playFromBootScene;

        [Tooltip("Boot 场景资产：上面开关打开时，它就是 Play 的起始场景。")]
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
            var guids = AssetDatabase.FindAssets("t:" + nameof(SceneShortcutProfile));
            if (guids.Length == 0) return null;
            // CreateAssetMenu 未开放（单例，不该手建多份）；万一 FindAssets 命中多份仍明确警告，避免「改了不生效」难排查。
            if (guids.Length > 1)
                Debug.LogWarning("[场景快捷入口] 找到多个配置，仅第一个生效，请删到只剩一个：\n  " +
                                 string.Join("\n  ", guids.Select(AssetDatabase.GUIDToAssetPath)));
            return AssetDatabase.LoadAssetAtPath<SceneShortcutProfile>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        /// <summary>定位配置；不存在则按默认值自动创建并种入工程已知场景（同其它单例 profile 的语义）。</summary>
        public static SceneShortcutProfile Resolve()
        {
            var existing = Find();
            if (existing != null) return existing;

            var profile = CreateInstance<SceneShortcutProfile>();
            profile.SeedDefaults();

            const string dir = "Assets/Game/Settings"; // 项目配置位，不在 Framework/ 内（ADR-0011）
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/Game", "Settings");
            string path = dir + "/SceneShortcutProfile.asset";
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[场景快捷入口] 未找到配置，已自动创建并种入工程已知场景：{path}");
            return profile;
        }

        // 开箱默认：把工程里已知的几个场景种进菜单，缺失的自动跳过。
        // 这些是「项目已知路径」——与 FrameworkConfigOverviewWindow 里登记具体模块同属编辑器层的项目知识；
        // 框架抽成独立包时清空本方法体即可（用户自己的项目照旧在 Inspector 里加自己的场景）。
        private void SeedDefaults()
        {
            void Add(string assetPath, string group)
            {
                var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(assetPath);
                if (scene != null) _entries.Add(new SceneEntry { Scene = scene, Group = group });
            }

            Add("Assets/Game/Framework/Demo/Scenes/DemoScene.unity", null);
            Add("Assets/Game/Outpost/Scenes/OutpostGame.unity", "Outpost");
            Add("Assets/Game/Outpost/Scenes/OutpostBattle.unity", "Outpost");

            // Boot 场景先备好、但开关默认关：不擅自改 Play 行为，用户想要一键开即可。
            _bootScene = AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Game/Scenes/BootScene.unity");
        }
    }
}
