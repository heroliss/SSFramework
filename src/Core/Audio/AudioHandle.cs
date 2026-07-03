using System;

namespace Game.Framework.Audio
{
    /// <summary>
    /// 一次音效播放的句柄：查询是否仍在播、（淡出）停止。零分配 struct，一次性音效可直接丢弃不接。
    /// </summary>
    /// <remarks>
    /// <b>陈旧安全</b>：音效播完 / 被停后句柄自动失效，之后 <see cref="Stop"/> 是安全 no-op、
    /// <see cref="IsPlaying"/> 返回 false（内部靠自增 id 区分，voice 复用后旧句柄不会误停新声音）；
    /// <c>default(AudioHandle)</c> 同样安全。<br/>
    /// <b>生命周期</b>：实现 <see cref="IDisposable"/>（Dispose = 立即停），循环音效可 <c>Bag.Add(handle)</c>
    /// 随宿主 View / Context 销毁自动停止——与框架「一切进 Bag」的心智统一。
    /// </remarks>
    public readonly struct AudioHandle : IDisposable
    {
        private readonly AudioUtility _owner;
        private readonly int _id;

        internal AudioHandle(AudioUtility owner, int id)
        {
            _owner = owner;
            _id = id;
        }

        /// <summary>该声音是否仍在播放（含淡出过程中）。陈旧 / default 句柄返回 false。</summary>
        public bool IsPlaying => _owner != null && _owner.IsVoiceActive(_id);

        /// <summary>停止该声音（fadeSeconds &gt; 0 时先淡出）并回收。陈旧 / default 句柄安全 no-op。</summary>
        public void Stop(float fadeSeconds = 0f) => _owner?.StopVoice(_id, fadeSeconds);

        /// <summary>等价 <c>Stop()</c>（立即停止）。让循环音效可进 <c>DisposableBag</c>。</summary>
        public void Dispose() => Stop();
    }
}
