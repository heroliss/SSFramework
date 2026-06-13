namespace Game.Framework
{
    /// <summary>
    /// 配置表加载状态。由配置初始化 System 写入、挂在配置 Model 上供业务订阅（启动界面等待、失败提示）。
    /// </summary>
    public enum ConfigInitState
    {
        /// <summary>尚未开始加载（初始化 System 还没跑到，或场景里没挂它）。</summary>
        Idle,

        /// <summary>正在预载数据文件并构造表实例。</summary>
        Loading,

        /// <summary>全部表就绪，配置 Model 的 Tables 已可用。</summary>
        Ready,

        /// <summary>加载或构造失败（数据缺失 / 反序列化异常等），详情见错误日志；Tables 保持为空。</summary>
        Failed
    }
}
