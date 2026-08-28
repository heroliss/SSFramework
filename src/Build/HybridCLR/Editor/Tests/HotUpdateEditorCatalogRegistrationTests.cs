using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定可删除的热更新构建 Module 会自行贡献工具与配置入口。</summary>
    public sealed class HotUpdateEditorCatalogRegistrationTests
    {
        [Test]
        public void HybridClrBuildModule_RegistersOwnedToolAndConfiguration()
        {
            string[] toolIds = FrameworkToolRegistry.Snapshot().Select(item => item.Id).ToArray();
            string[] configIds = FrameworkConfigRegistry.Snapshot().Select(item => item.Id).ToArray();

            Assert.That(toolIds, Does.Contain("hot-update-build"));
            Assert.That(configIds, Does.Contain("hot-update-build"));
        }
    }
}
