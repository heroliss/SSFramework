using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定资源构建 Module 在关闭预定义程序集隐式引用后仍向通用配置中心贡献自己的 Profile。</summary>
    public sealed class BuildEditorCatalogRegistrationTests
    {
        [Test]
        public void AssetBuildModule_RegistersOwnedConfiguration()
        {
            string[] ids = FrameworkConfigRegistry.Snapshot().Select(item => item.Id).ToArray();
            Assert.That(ids, Does.Contain("asset-build"));
        }
    }
}
