using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;

namespace Game.Framework.UI.UGui.Editor.Tests
{
    /// <summary>锁定 UGUI Editor Module 自己登记根 Profile 与目录级覆盖的配置卡片。</summary>
    public sealed class UIEditorCatalogRegistrationTests
    {
        [Test]
        public void UGuiModule_RegistersOwnedConfiguration()
        {
            var descriptor = FrameworkConfigRegistry.Snapshot().Single(item => item.Id == "ui-binding");
            Assert.That(descriptor.ProfileType, Is.EqualTo(typeof(UICodeGenProfile)));
            Assert.That(descriptor.SecondaryProfileType, Is.EqualTo(typeof(UICodeGenDirConfig)));
        }
    }
}
