using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Game.Framework.Audio;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证音频服务（ADR-0022）：三级音量数学与即时生效、音乐单通道切换/交叉淡变、
    /// 音效句柄陈旧安全、AudioSource 池化复用、Dispose 宽容语义。
    /// 断言尽量不依赖音频 DSP 推进（音量是同步写字段、句柄基于活动列表）；
    /// 仅「播完自动回收 / 循环持续 / 暂停保持 / owner 交接」六例需要真实播放，batchmode（CI 无音频设备）下 Ignore。
    /// </summary>
    public class AudioTests
    {
        private AudioUtility _audio;
        private GameObject _listener;
        private AudioClip _clipA;
        private AudioClip _clipB;

        private const string RootName = "[Game.Framework Audio]";

        [SetUp]
        public void SetUp()
        {
            // 测试场景没有相机：补一个 AudioListener，避免 Unity 刷「no audio listeners」警告。
            _listener = new GameObject("TestListener", typeof(AudioListener));
            _clipA = AudioClip.Create("clip-a", 4410, 1, 44100, false); // 0.1s 静音片段
            _clipB = AudioClip.Create("clip-b", 4410, 1, 44100, false);
            _audio = new AudioUtility();
        }

        [TearDown]
        public void TearDown()
        {
            _audio.Dispose();
            // Dispose 里的 Destroy 是帧末延迟销毁；立刻清掉，保证下一个用例 GameObject.Find 拿到的是自己的根节点。
            var leftover = GameObject.Find(RootName);
            if (leftover != null) UnityEngine.Object.DestroyImmediate(leftover);
            UnityEngine.Object.DestroyImmediate(_listener);
            UnityEngine.Object.DestroyImmediate(_clipA);
            UnityEngine.Object.DestroyImmediate(_clipB);
            AudioListener.pause = false;
        }

        // 取当前所有池化 AudioSource（含停用的空闲 voice），断言实际写到源上的音量/参数用。
        private static AudioSource[] FindVoiceSources()
        {
            var root = GameObject.Find(RootName);
            return root == null ? Array.Empty<AudioSource>() : root.GetComponentsInChildren<AudioSource>(true);
        }

        [Test]
        public void VolumeMath_MasterTimesGroupTimesPlayVolume()
        {
            _audio.MasterVolume = 0.5f;
            _audio.SetGroupVolume(AudioGroups.Sfx, 0.5f);
            _audio.PlaySfx(_clipA, volume: 0.8f, loop: true);

            var sources = FindVoiceSources();
            Assert.AreEqual(1, sources.Length);
            Assert.AreEqual(0.5f * 0.5f * 0.8f, sources[0].volume, 1e-4f); // 主 × 组 × 单次
        }

        [Test]
        public void SetVolume_AffectsPlayingVoiceImmediately()
        {
            _audio.PlaySfx(_clipA, loop: true);
            var src = FindVoiceSources()[0];
            Assert.AreEqual(1f, src.volume, 1e-4f);

            _audio.SetGroupVolume(AudioGroups.Sfx, 0.25f); // 滑条场景：改音量即时作用于在播声音
            Assert.AreEqual(0.25f, src.volume, 1e-4f);

            _audio.MasterVolume = 0.5f;
            Assert.AreEqual(0.125f, src.volume, 1e-4f);
        }

        [Test]
        public void Volumes_ClampToUnitRange_UnknownGroupDefaultsToOne()
        {
            Assert.AreEqual(1f, _audio.GetGroupVolume("NeverSetGroup"), 1e-4f); // 组不需要预注册

            _audio.SetGroupVolume("Voice", 1.5f);
            Assert.AreEqual(1f, _audio.GetGroupVolume("Voice"), 1e-4f);

            _audio.MasterVolume = -0.5f;
            Assert.AreEqual(0f, _audio.MasterVolume, 1e-4f);

            // 单次 volume 也 clamp：master/group 已复位为可辨识值
            _audio.MasterVolume = 1f;
            _audio.PlaySfx(_clipA, volume: 2f, loop: true);
            Assert.AreEqual(1f, FindVoiceSources()[0].volume, 1e-4f);
        }

        [Test]
        public void InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => _audio.PlaySfx(null));
            Assert.Throws<ArgumentNullException>(() => _audio.PlayMusic(null));
            Assert.Throws<ArgumentException>(() => _audio.PlaySfx(_clipA, group: null));
            Assert.Throws<ArgumentException>(() => _audio.GetGroupVolume(""));
            Assert.Throws<ArgumentException>(() => _audio.SetGroupVolume(null, 0.5f));
        }

        [Test]
        public void Handle_StopRemovesVoice_StaleHandleIsSafe()
        {
            var handle = _audio.PlaySfx(_clipA, loop: true);
            Assert.IsTrue(handle.IsPlaying);
            Assert.AreEqual(1, _audio.ActiveVoiceCount);

            handle.Stop();
            Assert.IsFalse(handle.IsPlaying);
            Assert.AreEqual(0, _audio.ActiveVoiceCount);

            handle.Stop();                       // 陈旧句柄再停 = 安全 no-op
            Assert.IsFalse(default(AudioHandle).IsPlaying);
            default(AudioHandle).Stop();         // default 句柄同样安全
        }

        [Test]
        public void Handle_DisposeStops_SuitsDisposableBag()
        {
            var handle = _audio.PlaySfx(_clipA, loop: true);
            ((IDisposable)handle).Dispose(); // Bag 的清理路径就是 IDisposable.Dispose
            Assert.IsFalse(handle.IsPlaying);
            Assert.AreEqual(0, _audio.ActiveVoiceCount);
        }

        [Test]
        public void Voice_IsPooled_AndStateResetsBetweenPlays()
        {
            // 3D 播放会改位置与 spatialBlend——停止归还后再 2D 播放，必须拿到复位干净的同一个源。
            var h1 = _audio.PlaySfxAt(_clipA, new Vector3(5f, 0f, 0f), pitch: 1.5f, loop: true);
            var src = FindVoiceSources()[0];
            Assert.AreEqual(5f, src.transform.position.x, 1e-4f);
            Assert.AreEqual(1f, src.spatialBlend, 1e-4f);
            Assert.AreEqual(1.5f, src.pitch, 1e-4f);
            h1.Stop();

            _audio.PlaySfx(_clipB, loop: true);
            var sources = FindVoiceSources();
            Assert.AreEqual(1, sources.Length, "第二次播放应复用池中的 AudioSource，而不是新建");
            Assert.AreSame(src, sources[0]);
            Assert.AreEqual(0f, src.spatialBlend, 1e-4f); // 3D 状态已复位
            Assert.AreEqual(1f, src.pitch, 1e-4f);
            Assert.AreEqual(0f, src.transform.localPosition.x, 1e-4f);
        }

        [Test]
        public void Music_SingleChannel_SwitchAndStop()
        {
            _audio.PlayMusic(_clipA, fadeSeconds: 0f);
            Assert.AreSame(_clipA, _audio.CurrentMusic);
            Assert.AreEqual(1, _audio.ActiveVoiceCount);

            _audio.PlayMusic(_clipA, fadeSeconds: 0f); // 同 clip 幂等：不叠加通道
            Assert.AreEqual(1, _audio.ActiveVoiceCount);

            _audio.PlayMusic(_clipB, fadeSeconds: 0f); // 无淡变切换：旧的立即归还
            Assert.AreSame(_clipB, _audio.CurrentMusic);
            Assert.AreEqual(1, _audio.ActiveVoiceCount);

            _audio.StopMusic(0f);
            Assert.IsNull(_audio.CurrentMusic);
            Assert.AreEqual(0, _audio.ActiveVoiceCount);
        }

        [UnityTest]
        public IEnumerator Music_Crossfade_OldFadesOutNewReachesFullVolume() => UniTask.ToCoroutine(async () =>
        {
            _audio.PlayMusic(_clipA, fadeSeconds: 0f);
            _audio.PlayMusic(_clipB, fadeSeconds: 0.1f);

            // 交叉期：旧 voice 淡出中 + 新 voice 淡入中并存
            Assert.AreSame(_clipB, _audio.CurrentMusic);
            Assert.AreEqual(2, _audio.ActiveVoiceCount);

            await UniTask.Delay(TimeSpan.FromSeconds(0.6), DelayType.Realtime);

            Assert.AreEqual(1, _audio.ActiveVoiceCount, "淡出完成后旧音乐应已归还");
            Assert.AreSame(_clipB, _audio.CurrentMusic);
            foreach (var src in FindVoiceSources())
                if (src.gameObject.activeSelf)
                    Assert.AreEqual(1f, src.volume, 1e-3f, "淡入完成后应到达目标音量");
        });

        [Test]
        public void StopAllSfx_LeavesMusicPlaying()
        {
            _audio.PlayMusic(_clipA, fadeSeconds: 0f);
            var h1 = _audio.PlaySfx(_clipB, loop: true);
            var h2 = _audio.PlaySfx(_clipB, loop: true, group: "Voice");
            Assert.AreEqual(3, _audio.ActiveVoiceCount);

            _audio.StopAllSfx();
            Assert.AreEqual(1, _audio.ActiveVoiceCount);
            Assert.AreSame(_clipA, _audio.CurrentMusic);
            Assert.IsFalse(h1.IsPlaying);
            Assert.IsFalse(h2.IsPlaying);
        }

        [UnityTest]
        public IEnumerator OneShot_AutoRecyclesAfterPlayback()
        {
            if (Application.isBatchMode) Assert.Ignore("batchmode 无音频设备，播放推进不可靠——编辑器内跑本用例");
            return UniTask.ToCoroutine(async () =>
            {
                var handle = _audio.PlaySfx(_clipA); // 0.1s 一次性
                Assert.IsTrue(handle.IsPlaying);

                await UniTask.Delay(TimeSpan.FromSeconds(0.5), DelayType.Realtime);

                Assert.IsFalse(handle.IsPlaying, "一次性音效播完应被驱动循环自动回收");
                Assert.AreEqual(0, _audio.ActiveVoiceCount);
            });
        }

        [UnityTest]
        public IEnumerator NonLoopMusic_AfterFadeInAndPlayback_AutoRecycles() => UniTask.ToCoroutine(async () =>
        {
            if (Application.isBatchMode) Assert.Ignore("batchmode 无音频设备，播放推进不可靠——编辑器内跑本用例");

            // clip 只有 0.1s，淡入故意更长：自然终态必须优先，不能等整个淡入 owner 到期才释放。
            _audio.PlayMusic(_clipA, fadeSeconds: 0.6f, loop: false);
            Assert.AreSame(_clipA, _audio.CurrentMusic);
            Assert.AreEqual(1, _audio.ActiveVoiceCount);

            await UniTask.Delay(TimeSpan.FromSeconds(0.3), DelayType.Realtime);

            Assert.IsNull(_audio.CurrentMusic, "非循环音乐自然结束后，CurrentMusic 应回到 null");
            Assert.AreEqual(0, _audio.ActiveVoiceCount, "自然结束的音乐 voice 应归还池并释放 clip 引用");
        });

        [UnityTest]
        public IEnumerator LoopMusic_OutlivesClipLength_UntilExplicitlyStopped()
        {
            if (Application.isBatchMode) Assert.Ignore("batchmode 无音频设备，播放推进不可靠——编辑器内跑本用例");
            return UniTask.ToCoroutine(async () =>
            {
                _audio.PlayMusic(_clipA, fadeSeconds: 0f, loop: true);

                await UniTask.Delay(TimeSpan.FromSeconds(0.4), DelayType.Realtime);

                Assert.AreSame(_clipA, _audio.CurrentMusic, "循环音乐越过 clip 长度后仍应由显式 owner 持有");
                Assert.AreEqual(1, _audio.ActiveVoiceCount);
                _audio.StopMusic(0f);
                Assert.IsNull(_audio.CurrentMusic);
            });
        }

        [UnityTest]
        public IEnumerator PausedNonLoopMusic_IsNotMistakenForFinished()
        {
            if (Application.isBatchMode) Assert.Ignore("batchmode 无音频设备，播放推进不可靠——编辑器内跑本用例");
            return UniTask.ToCoroutine(async () =>
            {
                AudioListener.pause = true;
                _audio.PlayMusic(_clipA, fadeSeconds: 0f, loop: false);

                await UniTask.Delay(TimeSpan.FromSeconds(0.25), DelayType.Realtime);

                Assert.AreSame(_clipA, _audio.CurrentMusic, "全局暂停期间不能把暂停中的音乐误判为播完");
                Assert.AreEqual(1, _audio.ActiveVoiceCount);

                AudioListener.pause = false;
                await UniTask.Delay(TimeSpan.FromSeconds(0.5), DelayType.Realtime);
                Assert.IsNull(_audio.CurrentMusic);
                Assert.AreEqual(0, _audio.ActiveVoiceCount);
            });
        }

        [UnityTest]
        public IEnumerator CancelledFadeContinuation_CannotTouchReusedVoice() => UniTask.ToCoroutine(async () =>
        {
            if (Application.isBatchMode) Assert.Ignore("batchmode 无音频设备，播放推进不可靠——编辑器内跑本用例");

            var stale = _audio.PlaySfx(_clipA, volume: 0.2f, loop: true);
            stale.Stop(0.2f); // owner A 开始淡出
            stale.Stop(0f);   // 立即归还，取消 owner A

            var current = _audio.PlaySfx(_clipB, volume: 0.8f, loop: true); // 复用同一个 Voice
            var source = FindVoiceSources()[0];
            Assert.AreEqual(0.8f, source.volume, 1e-4f);

            await UniTask.Delay(TimeSpan.FromSeconds(0.3), DelayType.Realtime);

            Assert.IsTrue(current.IsPlaying, "旧淡变迟到恢复不得归还复用后的新播放");
            Assert.AreEqual(1, _audio.ActiveVoiceCount);
            Assert.AreEqual(0.8f, source.volume, 1e-4f, "旧淡变迟到恢复不得覆盖新播放的音量");
            current.Stop();
        });

        [UnityTest]
        public IEnumerator LoopSfx_OutlivesClipLength_UntilStopped()
        {
            if (Application.isBatchMode) Assert.Ignore("batchmode 无音频设备，播放推进不可靠——编辑器内跑本用例");
            return UniTask.ToCoroutine(async () =>
            {
                var handle = _audio.PlaySfx(_clipA, loop: true); // clip 只有 0.1s

                await UniTask.Delay(TimeSpan.FromSeconds(0.4), DelayType.Realtime);

                Assert.IsTrue(handle.IsPlaying, "循环音效不应被自动回收");
                handle.Stop();
                Assert.IsFalse(handle.IsPlaying);
            });
        }

        [UnityTest]
        public IEnumerator Dispose_StopsEverything_FurtherCallsAreSafeNoOps() => UniTask.ToCoroutine(async () =>
        {
            var handle = _audio.PlaySfx(_clipA, loop: true);
            _audio.PlayMusic(_clipB, fadeSeconds: 0f);

            _audio.Dispose();
            Assert.IsFalse(handle.IsPlaying);
            Assert.AreEqual(0, _audio.ActiveVoiceCount);
            Assert.IsNull(_audio.CurrentMusic);

            // Dispose 后误用：Editor/Dev 报 error 帮抓过期引用，但返回失效 handle、不炸游戏。
            LogAssert.Expect(LogType.Error, new Regex("after Dispose"));
            var sink = new CapturingSink();
            Log.AddSink(sink);
            AudioHandle stale;
            try
            {
                stale = _audio.PlaySfx(_clipA);
            }
            finally
            {
                Log.RemoveSink(sink);
            }

            Assert.IsFalse(stale.IsPlaying);
            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(LogLevel.Error, sink.Entries[0].Level);
            Assert.AreEqual(nameof(AudioUtility), sink.Entries[0].Category);
            StringAssert.Contains(nameof(IAudioUtility), sink.Entries[0].Message);
            handle.Stop(); // 陈旧句柄静默

            await UniTask.Yield(); // 让帧末延迟销毁生效
            Assert.IsNull(GameObject.Find(RootName), "Dispose 应销毁音频根节点");
        });

        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }
    }
}
