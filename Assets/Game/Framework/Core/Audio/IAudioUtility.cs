using Game.Framework.Utility;
using UnityEngine;

namespace Game.Framework.Audio
{
    /// <summary>
    /// 音频服务工具。框架统一的全局播放入口，业务层通过 <c>GetUtility&lt;IAudioUtility&gt;</c> 访问。
    ///
    /// <para><b>定位：全局播放编排</b>——BGM 单通道切换（自动交叉淡入淡出）、一次性/循环音效（池化 AudioSource，
    /// 播完自动回收）、分组音量（设置页滑条实时作用于所有在播声音）。
    /// <b>不替代</b>挂在对象上的 <c>AudioSource</c> 组件：持续跟随对象的 3D 音源（引擎声、脚步循环）
    /// 直接用组件（Inspector 可调、随对象销毁），框架不抢引擎的活。ADR-0022。</para>
    ///
    /// <para><b>音量模型</b>：实际音量 = <see cref="MasterVolume"/> × 组音量 × 单次 volume × 淡变系数，
    /// 全部 [0,1] clamp、线性幅度。组是开放字符串（框架预置 <see cref="AudioGroups"/> 两个常量，业务自加常量），
    /// 未设置过的组默认 1。音量<b>持久化归业务</b>：存进自己的设置数据（<c>IStorageUtility</c> 整存整取），
    /// 启动时逐组 <see cref="SetGroupVolume"/> 回灌。</para>
    ///
    /// <para><b>clip 从哪来</b>：经资源系统 <c>Bag.Load&lt;AudioClip&gt;(location)</c> 取到再传入——
    /// 加载与播放的生命周期分开管，本工具刻意不做按 location 加载的重载。</para>
    /// </summary>
    /// <remarks>
    /// <b>注册（按生命周期选，同池/存储三选一）：</b>纯 C# 跟随 Context 用
    /// <c>builder.RegisterOwnedUtility(new AudioUtility())</c>（随 Context Dispose 全停，推荐）；
    /// 已有外部 owner 时用 <c>RegisterUtility</c>；要 Inspector 配初始音量 / 跟随场景节点用 <c>MonoAudioUtility</c>。<br/>
    /// <b>线程</b>：主线程独占（框架统一契约）。<br/>
    /// <b>失败语义（宽容，学池不学存储）</b>：clip 为 null、组名为空白时抛参数异常；Dispose 后的播放、停止与音量修改
    /// = Editor/Dev <c>Log.Error</c> + 安全 no-op（丢一声音效不致命），只读查询返回释放时的最终快照；
    /// 停一个已结束的 handle 是安全 no-op（陈旧 handle 是常态）。<br/>
    /// <b>全局暂停</b>：用 Unity 自己的 <c>AudioListener.pause</c>，框架不包装；暂停期间不会把暂停中的声音误当播完回收。
    /// </remarks>
    public interface IAudioUtility : IUtility
    {
        // ── 音乐：全局单通道 ─────────────────────────────────────────────────

        /// <summary>
        /// 播放音乐（全局单通道）：已有音乐在播则交叉淡入淡出切换；同 clip 在播时 = no-op（幂等，场景重入直接调）。
        /// <paramref name="loop"/> 为 false 时，曲目自然结束后自动回收并让 <see cref="CurrentMusic"/> 变为 null。
        /// <paramref name="volume"/> 是本曲的基础音量（乘在 Music 组音量之上，用于曲目间响度对齐）。
        /// </summary>
        void PlayMusic(AudioClip clip, float fadeSeconds = 0.5f, bool loop = true, float volume = 1f);

        /// <summary>停止当前音乐（淡出后回收）。无音乐在播 = no-op。</summary>
        void StopMusic(float fadeSeconds = 0.5f);

        /// <summary>当前音乐通道在播的 clip；无音乐（或已进入淡出）时为 null。</summary>
        AudioClip CurrentMusic { get; }

        // ── 音效：池化 AudioSource ───────────────────────────────────────────

        /// <summary>
        /// 播放 2D 音效。一次性（loop=false）播完自动回收，可直接丢弃返回值；
        /// 循环音效持返回的 <see cref="AudioHandle"/> 停止——或把 handle 丢进 <c>DisposableBag</c> 随宿主自动停。
        /// </summary>
        AudioHandle PlaySfx(AudioClip clip, float volume = 1f, float pitch = 1f, bool loop = false, string group = AudioGroups.Sfx);

        /// <summary>
        /// 在世界坐标播放 3D 音效（spatialBlend = 1，声像与衰减由 <c>AudioListener</c> 相对位置决定）。
        /// 适合「发声体可能先销毁但声音要播完」的一次性音效（爆炸 / 命中）；
        /// 需要跟随对象移动的持续音源请直接挂 <c>AudioSource</c> 组件。
        /// <para><paramref name="minDistance"/> / <paramref name="maxDistance"/> 对应 AudioSource 的距离衰减参数
        /// （对数衰减：距离 ≤ min 全音量，之后按 min/距离 滚降；默认值即引擎默认）。⚠ 引擎默认 min=1 是
        /// 第一人称尺度——固定俯视 / 远机位下监听器到发声点动辄十几个单位，不调 min 会整体衰减到几乎无声；
        /// min 取「监听器到战场的典型距离」量级，场内全响、只留方位与远近差。</para>
        /// </summary>
        AudioHandle PlaySfxAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, bool loop = false, string group = AudioGroups.Sfx, float minDistance = 1f, float maxDistance = 500f);

        /// <summary>立即停止所有音效（不含音乐通道）。场景硬切 / 过场开始等「清场」时机用。</summary>
        void StopAllSfx();

        // ── 分组音量 ─────────────────────────────────────────────────────────

        /// <summary>主音量 [0,1]，乘在所有组之上。Set 即时作用于所有在播声音。</summary>
        float MasterVolume { get; set; }

        /// <summary>某组的音量 [0,1]；未设置过的组返回 1。组名空抛参数异常。</summary>
        float GetGroupVolume(string group);

        /// <summary>设置某组音量 [0,1]（自动 clamp），即时作用于该组所有在播声音。组不需要预注册。</summary>
        void SetGroupVolume(string group, float volume);
    }

    /// <summary>
    /// 框架预置的音量组常量。组本质是开放字符串，业务扩展组（语音 / 环境声）就是自己定义常量类
    /// （与存储 key 同一「常量管理字符串契约」姿势），不需要注册。
    /// </summary>
    public static class AudioGroups
    {
        /// <summary>音乐组：<see cref="IAudioUtility.PlayMusic"/> 的声音固定属于此组。</summary>
        public const string Music = "Music";

        /// <summary>音效组：<see cref="IAudioUtility.PlaySfx"/> 系列的默认组。</summary>
        public const string Sfx = "Sfx";
    }
}
