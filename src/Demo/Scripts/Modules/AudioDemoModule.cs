using System;
using Game.Framework.Audio;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·音频：全局播放编排——音乐单通道（切换自动交叉淡变）、池化音效（一次性播完自动回收、
    /// 循环音效 handle 进 Bag 随宿主自动停）、分组音量（主 × 组 × 单次，滑条即时生效）。
    /// 本章的 <see cref="IAudioUtility"/> 经 <see cref="InstallBindings"/> 用 RegisterOwned 注册（纯 C# 服务的标准路径）。
    /// </summary>
    public sealed class AudioDemoModule : DemoModuleBase
    {
        public override string Id => "audio";
        public override string Title => "音频 · BGM 与音效";
        public override string Category => "能力";
        public override int Order => 40;
        public override string Summary =>
            "全局播放编排：音乐单通道（PlayMusic 切换自动交叉淡变）+ 池化音效（PlaySfx 播完自动回收、" +
            "循环音效 handle 进 Bag 自动停）+ 分组音量实时生效。不替代挂在对象上的 AudioSource 组件。ADR-0022。";

        // demo 不带音频资产：程序生成正弦波 clip（真实项目的 clip 经资源系统 Bag.Load<AudioClip>(location) 加载）。
        private AudioClip _musicA;
        private AudioClip _musicB;
        private AudioClip _sfxBlip;
        private AudioClip _sfxHum;

        private AudioHandle _loopHandle;

        /// <summary>
        /// 纯 C# 服务的标准注册路径：RegisterOwned = 随 Context Dispose 自动全停（这里即退出 Play / 切走本章）。
        /// 挂场景节点、要 Inspector 配初始音量的项目用 MonoAudioUtility（同一套逻辑的 Mono 壳）。
        /// ⚠ 本方法在临时实例上被调（见 DemoModuleBase 说明），Build 要用的对象不能存字段、只能从 Context 解析。
        /// </summary>
        public override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterOwned(new AudioUtility(), typeof(IAudioUtility));
        }

        public override void Build(DemoModuleHost host)
        {
            var audio = this.GetUtility<IAudioUtility>();
            // 活动声音数不在接口上（诊断成员）；本章注册的就是内核默认实现，向下转型仅用于展示。
            var impl = audio as AudioUtility;

            // 生成演示用音频（整数周期数保证循环无爆点；音量刻意压低），随 Bag 销毁。
            _musicA = CreateTone("demo-music-A", 220f, 2f);
            _musicB = CreateTone("demo-music-B", 294f, 2f);
            _sfxBlip = CreateTone("demo-sfx-blip", 880f, 0.25f, decay: true);
            _sfxHum = CreateTone("demo-sfx-hum", 110f, 1f);
            Bag.Add(Disposable.Create(() =>
            {
                UnityEngine.Object.Destroy(_musicA);
                UnityEngine.Object.Destroy(_musicB);
                UnityEngine.Object.Destroy(_sfxBlip);
                UnityEngine.Object.Destroy(_sfxHum);
            }));

            // ── 定位 ──
            host.AddSectionTitle("定位：全局播放编排，不替代挂在对象上的 AudioSource");
            host.AddNote("音频服务管三件事：**BGM 单通道**（切换自动交叉淡变）、**一次性 / 循环音效**（池化 AudioSource，播完自动回收，不用每处手挂组件）、**分组音量**（设置页滑条实时作用于所有在播声音）。需要跟随对象移动的持续 3D 音源（引擎声、脚步循环）**直接挂 `AudioSource` 组件**——引擎组件可跨层，Inspector 可调、随对象销毁，框架不抢引擎的活。",
                new CodeRef("Assets/Game/Framework/Core/Audio/IAudioUtility.cs", "public interface IAudioUtility", "音频入口契约"));
            host.AddSubNote("clip 从哪来：经资源系统 `Bag.Load<AudioClip>(location)` 取到再传入——加载与播放的生命周期分开管，音频服务刻意不做按 location 加载的重载。本章的 clip 是程序生成的正弦波（demo 不带音频资产）。");

            // ── 注册方式 ──
            host.AddSectionTitle("注册：纯 C# 服务的三选一");
            host.AddNote("本章的 `IAudioUtility` 在 `InstallBindings` 里 `RegisterOwned` 注册（随 Context Dispose 自动全停，纯 C# 服务推荐路径）。另两条路：全局唯一不管释放用 `RegisterValue`；要 Inspector 配初始音量 / 跟随场景节点用 `MonoAudioUtility`（同一套逻辑的 Mono 壳，挂 Context 子节点即注册）。",
                CodeRef.Here("builder.RegisterOwned(new AudioUtility()", "本章的注册代码"));

            // ── 音乐单通道 ──
            host.AddSectionTitle("音乐：全局单通道，切换自动交叉淡变");
            var musicLabel = host.AddValueDisplay();
            musicLabel.style.whiteSpace = WhiteSpace.Normal;
            musicLabel.schedule.Execute(() =>
            {
                var cur = audio.CurrentMusic;
                musicLabel.text = $"当前音乐：{(cur != null ? cur.name : "（无）")}　|　活动声音数：{impl?.ActiveVoiceCount ?? 0}";
            }).Every(200);

            host.AddActionRow("播放音乐 A（PlayMusic，淡入 0.5s）", () => audio.PlayMusic(_musicA),
                CodeRef.Here("audio.PlayMusic(_musicA)", "播放音乐"));
            host.AddActionRow("切到音乐 B（交叉淡变 1s——旧的淡出、新的淡入）", () => audio.PlayMusic(_musicB, fadeSeconds: 1f),
                CodeRef.Here("audio.PlayMusic(_musicB, fadeSeconds: 1f)", "切换音乐"));
            host.AddActionRow("停止音乐（StopMusic，淡出 1s）", () => audio.StopMusic(1f),
                CodeRef.Here("audio.StopMusic(1f)", "停止音乐"));
            host.AddNote("单通道语义：同时只有一首 BGM，`PlayMusic` 就是「切到这首」——业务不用管上一首是谁、有没有在播。**同 clip 在播时重复调用是 no-op**（幂等）：连点两次「播放音乐 A」不会重头再来，场景重入直接调即可。交叉期活动声音数会短暂 +1（旧的在独立淡出），淡出完自动回收。淡变走 unscaled 时间——游戏暂停（timeScale = 0）时切 BGM 照常过渡。");

            // ── 音效 ──
            host.AddSectionTitle("音效：池化 AudioSource，一次性自动回收、循环用 handle 停");
            var sfxLabel = host.AddValueDisplay("一次性音效 fire-and-forget；循环音效持 handle 停，或丢进 Bag 随宿主自动停。");
            sfxLabel.style.whiteSpace = WhiteSpace.Normal;

            host.AddActionRow("播放一次性音效（PlaySfx，播完自动回收）", () =>
            {
                audio.PlaySfx(_sfxBlip);
                sfxLabel.text = "已播放 ✓ 返回值可直接丢弃——播完由框架自动回收 AudioSource（看上方活动声音数短暂 +1 又回落）。";
            }, CodeRef.Here("audio.PlaySfx(_sfxBlip)", "一次性音效"));
            host.AddActionRow("播放一次性音效（随机 pitch ±10%）", () =>
            {
                audio.PlaySfx(_sfxBlip, pitch: 1f + UnityEngine.Random.Range(-0.1f, 0.1f));
                sfxLabel.text = "已播放（随机 pitch）✓ 音效变体不需要专门 API——一个参数的事，业务侧自由组合。";
            }, CodeRef.Here("audio.PlaySfx(_sfxBlip, pitch:", "参数组合出变体"));
            host.AddActionRow("播放循环音效（loop: true，handle 进 Bag）", () =>
            {
                if (_loopHandle.IsPlaying)
                {
                    sfxLabel.text = "循环音效已在播——handle.IsPlaying 查询状态；先停掉再播。";
                    return;
                }
                _loopHandle = audio.PlaySfx(_sfxHum, volume: 0.6f, loop: true);
                Bag.Add(_loopHandle); // handle 实现 IDisposable：切走本章（Bag.Dispose）自动停，不会留一个响不停的循环
                sfxLabel.text = "循环音效已开 ✓ 它不会自动结束——handle 已丢进 Bag，就算忘了手动停，切走本章也会随宿主自动停。";
            }, CodeRef.Here("Bag.Add(_loopHandle)", "循环音效随宿主自动停"));
            host.AddActionRow("停止循环音效（handle.Stop，淡出 0.3s）", () =>
            {
                if (!_loopHandle.IsPlaying)
                {
                    sfxLabel.text = "循环音效不在播。陈旧 handle 再 Stop 也是安全 no-op——不用先判空再停。";
                    return;
                }
                _loopHandle.Stop(0.3f);
                sfxLabel.text = "已停止 ✓（淡出 0.3s 后回收）。此后这个 handle 变陈旧：IsPlaying = false、再 Stop 是 no-op。";
            }, CodeRef.Here("_loopHandle.Stop(0.3f)", "停止循环音效"));
            host.AddActionRow("停止全部音效（StopAllSfx，音乐不受影响）", () =>
            {
                audio.StopAllSfx();
                sfxLabel.text = "已清场 ✓ 全部音效立即停止回收；音乐通道不受影响。场景硬切 / 过场开始用它。";
            }, CodeRef.Here("audio.StopAllSfx()", "清场"));
            host.AddNote("池化对业务透明：AudioSource 挂在 DontDestroyOnLoad 的 `[Game.Framework Audio]` 节点下复用（Hierarchy 里可观察），播放高频音效不产生 Instantiate/Destroy 抖动。同时发声数不设上限——Unity 自带 voice 虚拟化（超出可听上限自动静音低优先级），框架不重复造限流。");

            // ── 分组音量 ──
            host.AddSectionTitle("分组音量：主 × 组 × 单次 三级乘法，即时生效");
            host.AddNote("每个声音的实际音量 = 主音量 × 组音量 × 单次 volume 参数（× 淡变系数）。组是开放字符串：框架预置 `AudioGroups.Music` / `AudioGroups.Sfx` 两个常量，业务加「语音」「环境声」就是自己定义常量，不需要注册。拖下面的滑条，**在播声音立刻变**——设置页三条滑条的实现就是这三行绑定。",
                CodeRef.Here("audio.SetGroupVolume(AudioGroups.Music", "滑条绑定音量"));

            AddVolumeSlider(host, "主音量（MasterVolume）", audio.MasterVolume, v => audio.MasterVolume = v);
            AddVolumeSlider(host, "音乐组（AudioGroups.Music）", audio.GetGroupVolume(AudioGroups.Music),
                v => audio.SetGroupVolume(AudioGroups.Music, v));
            AddVolumeSlider(host, "音效组（AudioGroups.Sfx）", audio.GetGroupVolume(AudioGroups.Sfx),
                v => audio.SetGroupVolume(AudioGroups.Sfx, v));

            host.AddSubNote("音量**持久化归业务**：存进自己的设置数据（`IStorageUtility` 整存整取，见「本地存储」章），启动时读出来逐组 `SetGroupVolume` 回灌。框架不悄悄写盘——存哪些组、什么时机存是业务决策。");

            // ── 刻意不做 ──
            host.AddSectionTitle("刻意不做");
            host.AddConcept("不上 AudioMixer", "分组音量是纯代码乘法，零配置开箱即用；mixer 的效果链 / 闪避 / snapshot 属于混音工程，需要的项目直接换 `IAudioUtility` 实现——接口本身就是接缝（FMOD / Wwise 同理，是「第二实现」不是「第二 provider」）。");
            host.AddConcept("不做挂点跟随 3D", "跟随对象的持续音源是 `AudioSource` 组件的活；`PlaySfxAt` 只覆盖「发声体可能先销毁但声音要播完」的一次性位置音效（爆炸 / 命中）。");
            host.AddConcept("不做播放列表 / 随机变体", "业务一行参数组合的事（上面「随机 pitch」按钮即示范），专门 API 只会更难用。");
            host.AddConcept("不包装全局暂停", "`AudioListener.pause` 就是 Unity 的全局开关，包一层没有增益；框架只保证暂停期间不把暂停中的声音误当播完回收。");

            host.AddTip("速记：BGM = PlayMusic/StopMusic（单通道、自动淡变、幂等）；音效 = PlaySfx（一次性丢弃返回值，循环 handle 进 Bag）；音量 = Master + SetGroupVolume（即时生效，持久化归业务）。深度见 framework-guide 音频章 / ADR-0022。");
        }

        // 音量滑条：demo 用 UI Toolkit 原生 Slider 直连音量 API（设置页同款绑定方式）。
        private void AddVolumeSlider(DemoModuleHost host, string label, float initial, Action<float> onChange)
        {
            var slider = new Slider(label, 0f, 1f) { value = initial, showInputField = true };
            slider.AddToClassList("demo-slider");
            slider.RegisterValueChangedCallback(evt => onChange(evt.newValue));
            host.Content.Add(slider);
        }

        // 生成正弦波测试音：整数 Hz × 整数秒 = 整周期循环无爆点；decay 给一次性音效加线性衰减包络。
        private static AudioClip CreateTone(string name, float frequency, float seconds, bool decay = false, float gain = 0.2f)
        {
            const int rate = 44100;
            int count = Mathf.CeilToInt(rate * seconds);
            var data = new float[count];
            for (int i = 0; i < count; i++)
            {
                float amp = decay ? gain * (1f - (float)i / count) : gain;
                data[i] = Mathf.Sin(2f * Mathf.PI * frequency * i / rate) * amp;
            }
            var clip = AudioClip.Create(name, count, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
