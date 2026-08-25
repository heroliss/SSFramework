using Game.Framework.Demo.Modules;
using NUnit.Framework;

namespace Game.Framework.Demo.Tests
{
    /// <summary>锁定存储 Demo 的破坏性操作边界，避免教程改动悄悄扩大持久数据删除范围。</summary>
    public sealed class StorageDemoModuleTests
    {
        [Test]
        public void ResetKeys_ContainsOnlyDataOwnedByThisChapter()
        {
            CollectionAssert.AreEqual(
                new[] { "profile", "save/slot1", "save/slot2", "legacy" },
                StorageDemoModule.ResetKeys,
                "重置按钮必须继续使用显式白名单；新增 key 时需要同步审查它是否确实由本章拥有。");
        }
    }
}
