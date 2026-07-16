using System;
using System.Collections.Generic;
using Game.Framework.Utility;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Game.Framework.Audio
{
    /// <summary>
    /// 音频服务的 Mono 版：挂在 Context 子节点上，可在 Inspector 配初始主音量 / 各组音量，
    /// 底层复用纯 C# 的 <see cref="AudioUtility"/>（同一套逻辑）。随宿主 GameObject / 场景销毁自动全停。
    /// </summary>
    /// <remarks>
    /// <b>何时用：</b>想在 Inspector 配置初始音量并在运行时观察音频状态、或希望音频随某个 Context 节点生命周期释放时用本类；
    /// 全局共享、纯代码配置用 <c>builder.RegisterOwned(new AudioUtility(), typeof(IAudioUtility))</c>（纯 C# 路径）。<br/>
    /// <b>生命周期：</b>继承 <see cref="MonoUtilityBase"/>——Awake 注册为 <see cref="IAudioUtility"/>（+ <c>IUtility</c>），
    /// OnDestroy 反注册并 Dispose 底层实现（销毁音频根节点，全部停声）。<br/>
    /// <b>实现：</b>组合而非继承底层（同 <c>MonoPoolUtility</c> / <c>MonoStorageUtility</c> 模式），
    /// 全部成员转发给内部 <see cref="AudioUtility"/>。Inspector 音量只是初始值，运行期改音量走 <see cref="SetGroupVolume"/>。
    /// </remarks>
    public sealed class MonoAudioUtility : MonoUtilityBase, IAudioUtility
    {
        /// <summary>单个音量组的 Inspector 初始配置。</summary>
        [Serializable]
        public struct GroupVolumeConfig
        {
            [Tooltip("组名（AudioGroups 常量或业务自定义常量）")] public string Group;
            [Range(0f, 1f), Tooltip("初始音量")] public float Volume;
        }

        [SerializeField, Range(0f, 1f), Tooltip("初始主音量（乘在所有组之上）")]
        private float _masterVolume = 1f;

        [SerializeField, Tooltip("各组初始音量；未列出的组默认 1。运行期改音量走 SetGroupVolume（业务通常从设置存档回灌）")]
        private List<GroupVolumeConfig> _groupVolumes = new();

        private AudioUtility _impl;

        // 运行时只读诊断：主/组音量、当前音乐、活动声音数。仅 Play 显示、Build 零成本。
        [FoldoutGroup(DiagGroup), ShowInInspector, ReadOnly, HideInEditorMode, LabelText("音频状态"), PropertyOrder(-90)]
        [PropertyTooltip("主音量 / 各组音量 / 当前音乐 / 活动与空闲声音数。")]
        private IReadOnlyList<string> InspectorAudio => _impl?.GetAudioDiagnostics();

        protected override void Awake()
        {
            // 先建实现并灌入初始音量，再注册（base.Awake 登记进容器后即可能被解析调用）。
            _impl = new AudioUtility { MasterVolume = _masterVolume };
            foreach (var cfg in _groupVolumes)
            {
                if (string.IsNullOrEmpty(cfg.Group)) continue; // 空行留给还没填完的 Inspector 配置，跳过即可
                _impl.SetGroupVolume(cfg.Group, cfg.Volume);
            }
            base.Awake();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // 释放 Bag + 从容器反注册
            _impl?.Dispose();
            _impl = null;
        }

        // ── IAudioUtility 转发到底层 AudioUtility ─────────────────────────────
        public AudioClip CurrentMusic => _impl.CurrentMusic;

        public void PlayMusic(AudioClip clip, float fadeSeconds = 0.5f, bool loop = true, float volume = 1f)
            => _impl.PlayMusic(clip, fadeSeconds, loop, volume);

        public void StopMusic(float fadeSeconds = 0.5f) => _impl.StopMusic(fadeSeconds);

        public AudioHandle PlaySfx(AudioClip clip, float volume = 1f, float pitch = 1f, bool loop = false, string group = AudioGroups.Sfx)
            => _impl.PlaySfx(clip, volume, pitch, loop, group);

        public AudioHandle PlaySfxAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, bool loop = false, string group = AudioGroups.Sfx, float minDistance = 1f, float maxDistance = 500f)
            => _impl.PlaySfxAt(clip, position, volume, pitch, loop, group, minDistance, maxDistance);

        public void StopAllSfx() => _impl.StopAllSfx();

        public float MasterVolume
        {
            get => _impl.MasterVolume;
            set => _impl.MasterVolume = value;
        }

        public float GetGroupVolume(string group) => _impl.GetGroupVolume(group);

        public void SetGroupVolume(string group, float volume) => _impl.SetGroupVolume(group, volume);
    }
}
