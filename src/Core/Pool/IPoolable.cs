namespace Game.Framework.Pool
{
    /// <summary>
    /// 池化对象可选实现：在租借/归还时机收到回调。
    /// </summary>
    /// <remarks>
    /// <b>OnRent：</b>对象从池中取出、交给调用方之前调用——用于"激活"（如重置计时、订阅）。<br/>
    /// <b>OnReturn：</b>对象归还入池之前调用——<b>在此清理状态</b>（清空字段、退订、停协程），避免脏数据被下一个租借者看到。<br/>
    /// 不实现此接口也能入池——只是少了自动回调，状态清理需由 <c>GetPool</c> 传入的 <c>onReturn</c> 委托或调用方自理。
    /// </remarks>
    public interface IPoolable
    {
        /// <summary>从池取出、交付调用方前触发。</summary>
        void OnRent();

        /// <summary>归还入池前触发；在此清理状态。</summary>
        void OnReturn();
    }
}
