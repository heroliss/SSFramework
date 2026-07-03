using Game.Framework.Context;
using Game.Framework.System;

namespace Game.Framework.Demo.Modules.Services
{
    /// <summary>演示用「不进生成安装器」的服务。</summary>
    public interface IDemoExcludedService : ISystem
    {
        string Describe();
    }

    /// <summary>
    /// opt-out 样例：标了 <c>[ExcludeFromInstaller]</c>，生成器扫描时跳过（翻 <c>Generated/DemoServicesInstaller.g.cs</c>
    /// 确认里面没有它）。它的构造需要参数——这正是回落手写的典型理由，注册走模块里的
    /// <c>RegisterFactory</c> 显式接线（见 ServiceInstallerDemoModule.InstallBindings）。
    /// </summary>
    [ExcludeFromInstaller]
    public sealed class DemoExcludedService : IDemoExcludedService
    {
        private readonly string _reason;

        public DemoExcludedService(string reason) => _reason = reason;

        public string Describe() => $"我没进生成的安装器（{_reason}）——由模块手写 RegisterFactory 注册进同一个容器。";
    }
}
