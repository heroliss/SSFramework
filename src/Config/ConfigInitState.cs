namespace Game.Framework
{
    /// <summary>
    /// 配置表加载状态。由配置服务（<c>MonoConfigUtilityBase</c>）在加载过程中写入，供业务订阅（启动界面等待、失败提示）。
    /// </summary>
    public enum ConfigInitState
    {
        /// <summary>自加载组件的 <c>Start</c> 尚未执行。</summary>
        Idle,

        /// <summary>正在预载数据文件并构造表实例。</summary>
        Loading,

        /// <summary>全部表就绪，配置 Utility 的 Tables 已可用。</summary>
        Ready,

        /// <summary>
        /// 加载或构造失败（数据缺失 / 反序列化异常等）；Tables 保持为空，<c>EnsureReady</c> 会抛出保存的根异常。
        /// 下游在 owner 未取消时自发抛出的取消异常会作为内部异常包装，避免与生命周期取消混淆。
        /// </summary>
        Failed
    }
}
