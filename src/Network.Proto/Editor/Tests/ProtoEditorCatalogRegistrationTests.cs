using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;

namespace Game.Framework.Network.Proto.Editor.Tests
{
    /// <summary>锁定 Protobuf Editor Module 在 <c>autoReferenced:false</c> 下仍登记自己的配置导航。</summary>
    public sealed class ProtoEditorCatalogRegistrationTests
    {
        [Test]
        public void ProtoModule_RegistersOwnedConfiguration() =>
            Assert.That(FrameworkConfigRegistry.Snapshot().Select(item => item.Id), Does.Contain("protobuf"));
    }
}
