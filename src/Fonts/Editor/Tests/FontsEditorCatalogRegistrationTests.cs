using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;

namespace Game.Framework.Fonts.Editor.Tests
{
    /// <summary>锁定字体 Editor Module 在 <c>autoReferenced:false</c> 下仍登记自己的配置导航。</summary>
    public sealed class FontsEditorCatalogRegistrationTests
    {
        [Test]
        public void FontsModule_RegistersOwnedConfiguration() =>
            Assert.That(FrameworkConfigRegistry.Snapshot().Select(item => item.Id), Does.Contain("font-charset"));
    }
}
