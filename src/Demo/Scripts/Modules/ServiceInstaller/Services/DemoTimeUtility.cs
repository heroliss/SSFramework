using System;
using Game.Framework.Utility;

namespace Game.Framework.Demo.Modules.Services
{
    /// <summary>演示用时间工具：给问候语提供当前时间文本。</summary>
    public interface IDemoTimeUtility : IUtility
    {
        string NowText();
    }

    /// <summary>
    /// 实现：文件名 = 类名、实现层接口、公共无参构造、纯 C# ——正好落在服务安装器生成器的扫描口径里，
    /// 生成产物见 <c>Generated/DemoServicesInstaller.g.cs</c>（注册契约 = 具体类型 + IDemoTimeUtility）。
    /// </summary>
    public sealed class DemoTimeUtility : IDemoTimeUtility
    {
        public string NowText() => DateTime.Now.ToString("HH:mm:ss");
    }
}
