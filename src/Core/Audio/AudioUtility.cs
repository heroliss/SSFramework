using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
using Game.Framework.Pool;
using UnityEngine;

namespace Game.Framework.Audio
{
    /// <summary>
    /// <see cref="IAudioUtility"/> 的默认实现：池化 AudioSource（复用 <see cref="ObjectPool{T}"/> 原语）+
    /// 中央驱动自动回收 + per-voice UniTask 淡入淡出 + 主×组×单次三级音量。
    /// </summary>
    /// <remarks>
    /// <b>注册（按生命周期选，同 PoolUtility 三选一）：</b>纯 C# 跟随 Context 用
    /// <c>builder.RegisterOwned(new AudioUtility(), typeof(IAudioUtility))</c>（随 Context Dispose 全停，推荐）；
    /// 全局唯一、不关心释放用 <c>RegisterValue</c>；要 Inspector 配初始音量 / 跟随场景节点用 <see cref="MonoAudioUtility"/>。<br/>
    /// <b>不依赖 Context / 其它工具</b>：池化用的是 <see cref="ObjectPool{T}"/> 类而非 IPoolUtility 服务
    /// （工具间不互拉依赖，同 PoolUtility 不拉 IAssetUtility 的先例）。主线程独占。<br/>
    /// 首次播放时惰性创建一个 DontDestroyOnLoad 的 <c>[Game.Framework Audio]</c> 根节点，池化 AudioSource 全挂它下面
    /// （保持激活——要出声，与对象池的「停用停放」相反；空闲 voice 单独停用自己的子节点）。
    /// 根节点或 voice 节点被外部销毁时自愈：死 voice 被丢弃、下次租借重建，不会散落或复活已 Dispose 的根。<br/>
    /// <b>Dispose 后宽容 no-op</b>（Editor/Dev <see cref="Log.Error"/> 帮抓过期引用）：销毁根节点即全部停声，
    /// 之后的播放调用返回失效 handle——丢一声音效不致命，不学存储的 fail-fast（ADR-0022 §6）。<br/>
    /// <b>淡变用 unscaled 时间</b>：游戏暂停（timeScale = 0）时音乐切换照常淡入淡出。
    /// </remarks>
    public sealed class AudioUtility : IAudioUtility, IAudioHandleOwner, IDisposable
    {
        // 内部播放单元：池化的 AudioSource + 本次播放的状态。归还时由 ResetVoice 整体复位。
        private sealed class Voice
        {
            public AudioSource Source;                 // 挂在根节点下的池化源；外部销毁后为 fake-null，租借时自愈重建
            public int Id = -1;                        // 本次播放的句柄 id（自增分配）；归还后置 -1，陈旧 handle 查不到
            public string Group;
            public float BaseVolume;                   // 单次播放的基础音量（PlaySfx/PlayMusic 的 volume 参数）
            public float FadeScale = 1f;               // 淡入淡出系数 0..1，由淡变任务驱动
            public bool IsMusic;                       // 音乐通道 voice；循环曲目显式停止，非循环曲目自然结束后由驱动回收
            public CancellationTokenSource FadeCts;    // 当前淡变任务的 owner；完成 / 接管 / 归还时清空并释放
        }

        private readonly ObjectPool<Voice> _pool;
        private readonly List<Voice> _active = new();
        private readonly Dictionary<string, float> _groupVolumes = new();
        private readonly Func<float> _unscaledDeltaTime;

        // 驱动循环与 Dispose 共用的生命周期取消源（淡变任务各有自己的 per-voice cts）。
        private readonly CancellationTokenSource _lifeCts = new();

        private Transform _root;
        private Voice _music;          // 当前音乐通道 voice；上一首在淡出期间已不被此字段引用，但仍在 _active 里走完淡出
        private int _nextId = 1;
        private float _masterVolume = 1f;
        private bool _driverRunning;
        private bool _disposed;

        public AudioUtility() : this(() => Time.unscaledDeltaTime)
        {
        }

        /// <summary>
        /// 测试 Seam：允许用确定的帧增量验证淡变 owner 与回收竞态，避免依赖编辑器焦点和墙钟。
        /// 生产入口始终使用 <see cref="Time.unscaledDeltaTime"/>。
        /// </summary>
        internal AudioUtility(Func<float> unscaledDeltaTime)
        {
            _unscaledDeltaTime = unscaledDeltaTime ?? throw new ArgumentNullException(nameof(unscaledDeltaTime));
            // onReturn 钩子做完整复位（停声、断 clip 引用、取消淡变、停用节点），保证池里的 voice 永远是干净的。
            _pool = new ObjectPool<Voice>(CreateVoice, onReturn: ResetVoice);
        }

        // ── 音乐单通道 ─────────────────────────────────────────────────────────

        public AudioClip CurrentMusic => _music != null && _music.Source != null ? _music.Source.clip : null;

        public void PlayMusic(AudioClip clip, float fadeSeconds = 0.5f, bool loop = true, float volume = 1f)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip), "PlayMusic 的 clip 不能为 null——停音乐用 StopMusic。");
            if (WarnIfDisposed(nameof(PlayMusic))) return;

            // 幂等：同 clip 在播（含淡入中）直接返回，场景重入不需要先查状态再决定要不要调。
            if (_music != null && _music.Source != null && _music.Source.clip == clip && _music.Source.isPlaying)
                return;

            // 旧音乐独立淡出后归还（快速连切时每个旧 voice 各自完成淡出，互不打断）；新音乐独立淡入。
            var old = _music;
            _music = null;
            FadeOutAndReturn(old, fadeSeconds);

            var voice = RentVoice(AudioGroups.Music, Mathf.Clamp01(volume), isMusic: true);
            var src = voice.Source;
            src.clip = clip;
            src.loop = loop;
            voice.FadeScale = fadeSeconds > 0f ? 0f : 1f; // 先定初始音量再 Play，避免起播那一帧的爆音
            ApplyVolume(voice);
            src.Play();
            if (fadeSeconds > 0f)
                StartFade(voice, 0f, 1f, fadeSeconds, thenReturn: false);

            _music = voice;
            EnsureDriver();
        }

        public void StopMusic(float fadeSeconds = 0.5f)
        {
            if (WarnIfDisposed(nameof(StopMusic))) return;
            var old = _music;
            _music = null;
            FadeOutAndReturn(old, fadeSeconds);
        }

        // ── 音效 ──────────────────────────────────────────────────────────────

        public AudioHandle PlaySfx(AudioClip clip, float volume = 1f, float pitch = 1f, bool loop = false, string group = AudioGroups.Sfx)
            => PlayCore(clip, null, volume, pitch, loop, group, 1f, 500f);

        public AudioHandle PlaySfxAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, bool loop = false, string group = AudioGroups.Sfx, float minDistance = 1f, float maxDistance = 500f)
            => PlayCore(clip, position, volume, pitch, loop, group, minDistance, maxDistance);

        private AudioHandle PlayCore(AudioClip clip, Vector3? position, float volume, float pitch, bool loop, string group, float minDistance, float maxDistance)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            ValidateGroup(group);
            if (WarnIfDisposed(nameof(PlaySfx))) return default;

            var voice = RentVoice(group, Mathf.Clamp01(volume), isMusic: false);
            var src = voice.Source;
            src.clip = clip;
            src.loop = loop;
            src.pitch = pitch;
            if (position.HasValue)
            {
                src.transform.position = position.Value;
                src.spatialBlend = 1f; // 完全 3D；2D 路径保持默认 0（归还时复位）
                src.minDistance = minDistance;
                src.maxDistance = maxDistance;
            }
            ApplyVolume(voice);
            src.Play();
            EnsureDriver();
            return new AudioHandle(this, voice.Id);
        }

        public void StopAllSfx()
        {
            if (WarnIfDisposed(nameof(StopAllSfx))) return;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var v = _active[i];
                if (!v.IsMusic) ReturnVoice(v);
            }
        }

        // ── 分组音量 ──────────────────────────────────────────────────────────

        public float MasterVolume
        {
            get => _masterVolume;
            set
            {
                _masterVolume = Mathf.Clamp01(value);
                ApplyAllVolumes();
            }
        }

        public float GetGroupVolume(string group)
        {
            ValidateGroup(group);
            return _groupVolumes.TryGetValue(group, out float vol) ? vol : 1f;
        }

        public void SetGroupVolume(string group, float volume)
        {
            ValidateGroup(group);
            _groupVolumes[group] = Mathf.Clamp01(volume);
            ApplyAllVolumes();
        }

        private static void ValidateGroup(string group)
        {
            if (string.IsNullOrEmpty(group))
                throw new ArgumentException("音量组名不能为空。", nameof(group));
        }

        // ── 句柄支撑（AudioHandle 经 IAudioHandleOwner 调用；陈旧 id 查不到 = 安全 no-op）──
        // 显式实现：这两个成员只服务句柄委托，不进 AudioUtility 的公开表面。

        bool IAudioHandleOwner.IsVoiceActive(int id) => !_disposed && FindVoice(id) != null;

        void IAudioHandleOwner.StopVoice(int id, float fadeSeconds)
        {
            if (_disposed) return; // Dispose 已全停，handle 的事后 Stop 静默即可（不算误用）
            FadeOutAndReturn(FindVoice(id), fadeSeconds);
        }

        private Voice FindVoice(int id)
        {
            if (id <= 0) return null; // default handle 的 id 为 0
            for (int i = 0; i < _active.Count; i++)
                if (_active[i].Id == id)
                    return _active[i];
            return null;
        }

        // ── 诊断 ──────────────────────────────────────────────────────────────

        /// <summary>当前活动（在播，含淡出中）的声音数。诊断 / 测试用，不参与播放逻辑。</summary>
        public int ActiveVoiceCount => _active.Count;

        /// <summary>
        /// 诊断用：主音量 / 各组音量 / 当前音乐 / 活动与空闲声音数的概要。
        /// 仅供 Inspector 只读展示（<see cref="MonoAudioUtility"/>），不参与播放逻辑。
        /// </summary>
        public IReadOnlyList<string> GetAudioDiagnostics()
        {
            var list = new List<string> { $"主音量 {_masterVolume:0.##}" };
            foreach (var kv in _groupVolumes)
                list.Add($"组 {kv.Key} 音量 {kv.Value:0.##}");
            var music = CurrentMusic;
            if (music != null)
                list.Add($"音乐 {music.name}");
            list.Add($"活动声音 {_active.Count}（空闲池 {_pool.CountInactive}）");
            return list;
        }

        /// <summary>
        /// 释放音频服务：取消驱动与所有淡变、销毁根节点（连带全部 AudioSource，声音即刻全停）、清空池。
        /// 之后的调用是安全 no-op（Editor/Dev <see cref="Log.Error"/> 帮抓过期引用）。幂等。
        /// </summary>
        /// <remarks>
        /// 由 <c>ContainerBuilder.RegisterOwned</c> + <c>GameContext.Dispose</c>（纯 C# 路径），
        /// 或 <c>MonoAudioUtility.OnDestroy</c>（Mono 路径）调用，让音频生命周期跟随其归属 Context / GameObject。
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _lifeCts.Cancel();
            _lifeCts.Dispose();
            foreach (var v in _active)
                CancelFade(v);
            _active.Clear();
            _music = null;
            _pool.Clear();

            if (_root != null)
                UnityEngine.Object.Destroy(_root.gameObject);
            _root = null;
        }

        // ── voice 生命周期 ────────────────────────────────────────────────────

        private Voice CreateVoice() => new Voice { Source = CreateSourceObject() };

        // 在根节点下建一个带 AudioSource 的子节点。根节点惰性创建；voice 节点被外部销毁后由 RentVoice 自愈重建。
        private AudioSource CreateSourceObject()
        {
            var root = EnsureRoot();
            var go = new GameObject("Voice");
            if (root != null)
                go.transform.SetParent(root, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            return src;
        }

        private Voice RentVoice(string group, float baseVolume, bool isMusic)
        {
            var v = _pool.Rent();
            if (v.Source == null) // 节点被外部销毁（含根节点被删）：自愈重建
                v.Source = CreateSourceObject();
            v.Id = _nextId++;
            v.Group = group;
            v.BaseVolume = baseVolume;
            v.FadeScale = 1f;
            v.IsMusic = isMusic;
            v.Source.gameObject.SetActive(true);
            _active.Add(v);
            return v;
        }

        // 归还 voice（幂等：已归还的直接返回）。驱动回收 / 手动 Stop / 淡出完成 / StopAllSfx 都收敛到这里。
        private void ReturnVoice(Voice v)
        {
            if (v == null || !_active.Remove(v)) return;
            if (ReferenceEquals(v, _music)) _music = null;
            _pool.Return(v); // 触发 ResetVoice 完整复位
        }

        // onReturn 钩子：完整复位到「干净空闲」——停声、断 clip 引用（不钉住可卸载的音频资源）、取消淡变、停用节点。
        private void ResetVoice(Voice v)
        {
            v.Id = -1;
            CancelFade(v);
            v.FadeScale = 1f;
            var src = v.Source;
            if (src == null) return; // 节点已被外部销毁：留给 RentVoice 自愈
            src.Stop();
            src.clip = null;
            src.loop = false;
            src.pitch = 1f;
            src.spatialBlend = 0f;
            src.minDistance = 1f;   // 3D 路径可能改过距离衰减：还原引擎默认
            src.maxDistance = 500f;
            src.transform.localPosition = Vector3.zero;
            src.gameObject.SetActive(false);
        }

        // ── 淡入淡出（per-voice UniTask，unscaled 时间）───────────────────────

        // 淡出到 0 后归还。fadeSeconds<=0 或源已死 = 立即归还；null / 已归还 = no-op。
        private void FadeOutAndReturn(Voice v, float fadeSeconds)
        {
            if (v == null || !_active.Contains(v)) return;
            if (fadeSeconds <= 0f || v.Source == null)
            {
                ReturnVoice(v);
                return;
            }
            StartFade(v, v.FadeScale, 0f, fadeSeconds, thenReturn: true);
        }

        private void StartFade(Voice v, float from, float to, float seconds, bool thenReturn)
        {
            CancelFade(v); // 新淡变接管：旧任务（若在跑）取消，FadeScale 从 from 重新推进
            var owner = new CancellationTokenSource();
            v.FadeCts = owner;
            RunFade(v, from, to, seconds, thenReturn, owner).Forget();
        }

        private async UniTaskVoid RunFade(
            Voice v,
            float from,
            float to,
            float seconds,
            bool thenReturn,
            CancellationTokenSource owner)
        {
            try
            {
                CancellationToken ct = owner.Token;
                float elapsed = 0f;
                while (elapsed < seconds)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, ct);
                    // Cancel 可能发生在本次 Yield 已完成、continuation 已排队之后；恢复时重新确认 owner，
                    // 防止旧任务给已被新淡变接管、甚至已经回池复用的 voice 写状态。
                    if (!ReferenceEquals(v.FadeCts, owner)) return;
                    elapsed += Mathf.Max(0f, _unscaledDeltaTime());
                    v.FadeScale = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / seconds));
                    ApplyVolume(v);
                }
                if (thenReturn && ReferenceEquals(v.FadeCts, owner))
                    ReturnVoice(v);
            }
            catch (OperationCanceledException) { /* 新淡变接管或 voice 已归还：正常取消 */ }
            catch (Exception e)
            {
                if (!ReferenceEquals(v.FadeCts, owner)) return;
                Log.Error(
                    "音量淡变任务意外停止。",
                    e,
                    nameof(AudioUtility),
                    v.Source);
                // 淡出代表调用方已经交出该 voice 的所有权；异常时也不能让旧声音和 clip 永久滞留。
                // 日志 sink 也可能重入音频 API，归还前再验一次 owner。
                if (thenReturn && ReferenceEquals(v.FadeCts, owner))
                    ReturnVoice(v);
            }
            finally { ReleaseFadeOwner(v, owner); }
        }

        // 旧淡变的 continuation 可能晚于新淡变恢复；只允许当前 owner 清自己的槽，不能误释放接管者。
        private static void ReleaseFadeOwner(Voice v, CancellationTokenSource owner)
        {
            if (!ReferenceEquals(v.FadeCts, owner)) return;
            v.FadeCts = null;
            owner.Dispose();
        }

        private static void CancelFade(Voice v)
        {
            var owner = v.FadeCts;
            if (owner == null) return;
            // 先摘 owner 再 Cancel：取消回调即使同步恢复，也只会看到“已交出”，不会与这里重复清理。
            v.FadeCts = null;
            try { owner.Cancel(); }
            finally { owner.Dispose(); }
        }

        // ── 音量应用 ──────────────────────────────────────────────────────────

        private void ApplyVolume(Voice v)
        {
            var src = v.Source;
            if (src == null) return;
            src.volume = _masterVolume * GetGroupVolume(v.Group) * v.BaseVolume * v.FadeScale;
        }

        private void ApplyAllVolumes()
        {
            for (int i = 0; i < _active.Count; i++)
                ApplyVolume(_active[i]);
        }

        // ── 中央驱动：自动回收播完的一次性声音 ─────────────────────────────────
        // 有活动 voice 时每帧扫一遍（数量级 <32，成本可忽略），无活动自动停跑、下次播放再启动。

        private void EnsureDriver()
        {
            if (_driverRunning || _disposed) return;
            _driverRunning = true;
            RunDriver(_lifeCts.Token).Forget();
        }

        private async UniTaskVoid RunDriver(CancellationToken ct)
        {
            try
            {
                while (_active.Count > 0)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, ct);
                    for (int i = _active.Count - 1; i >= 0; i--)
                    {
                        var v = _active[i];
                        if (v.Source == null)
                        {
                            // 节点被外部销毁：丢弃不回池（池里不留死实例；下次租借经自愈重建补上）。
                            if (ReferenceEquals(v, _music)) _music = null;
                            CancelFade(v);
                            _active.RemoveAt(i);
                            continue;
                        }
                        // 非循环音乐的播放终态优先于淡入：短曲可能在淡入尚未结束时就自然播完，
                        // 此时已经没有可淡的声音，应立即归还并取消 owner，不能继续钉住 clip 到淡入时长结束。
                        if (v.IsMusic && !v.Source.loop && !v.Source.isPlaying && !AudioListener.pause)
                        {
                            ReturnVoice(v);
                            continue;
                        }
                        if (v.FadeCts != null) continue;      // 其余淡变仍由任务持有；淡出完成会自行归还
                        if (v.IsMusic && v.Source.loop) continue; // 循环音乐只由 PlayMusic/StopMusic 显式切换
                        // AudioListener.pause 是全局暂停：暂停中的声音不是播完，不能回收。
                        if (!v.Source.isPlaying && !AudioListener.pause)
                            ReturnVoice(v);
                    }
                }
            }
            catch (OperationCanceledException) { /* Dispose：正常取消 */ }
            catch (Exception e)
            {
                Log.Error(
                    "音频 Voice 回收驱动意外停止。",
                    e,
                    nameof(AudioUtility),
                    _root);
            }
            finally { _driverRunning = false; }
        }

        // ── 根节点 ────────────────────────────────────────────────────────────

        // 惰性创建根节点：激活（要出声）+ DontDestroyOnLoad（跨场景存活，随本工具 Dispose 销毁）。
        // _root == null 同时识别「从未创建」和 Unity fake-null（被外部销毁）——都重建；Dispose 后返回 null（不复活）。
        private Transform EnsureRoot()
        {
            if (_disposed) return null;
            if (_root == null)
            {
                var go = new GameObject("[Game.Framework Audio]");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _root = go.transform;
            }
            return _root;
        }

        // Dispose 后误用诊断：音频已随其归属 Context/GameObject 释放，继续调用多半是持有了过期引用。
        // 仅 Editor/Dev 报错（对齐 PoolUtility 风格）；返回 true 让调用方安全 no-op，不炸游戏。
        private bool WarnIfDisposed(string op)
        {
            if (!_disposed) return false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Log.Error(
                $"音频服务已释放，不能再调用 '{op}'；请检查是否持有了过期的 IAudioUtility 引用。",
                category: nameof(AudioUtility));
#endif
            return true;
        }
    }
}
