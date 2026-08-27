using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定 Luban Editor Module 自己拥有配置中心登记，中央窗口无需知道该可选程序集。</summary>
    public sealed class ConfigEditorCatalogRegistrationTests
    {
        [Test]
        public void LubanModule_RegistersOwnedConfiguration() =>
            Assert.That(FrameworkConfigRegistry.Snapshot().Select(item => item.Id), Does.Contain("luban"));
    }
}
