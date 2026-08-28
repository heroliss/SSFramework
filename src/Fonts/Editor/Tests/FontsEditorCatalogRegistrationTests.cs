using System;
using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;
using UnityEditor;

namespace Game.Framework.Fonts.Editor.Tests
{
    /// <summary>锁定字体 Editor Module 在 <c>autoReferenced:false</c> 下仍登记自己的配置导航。</summary>
    public sealed class FontsEditorCatalogRegistrationTests
    {
        [Test]
        public void FontsModule_RegistersOwnedConfiguration() =>
            Assert.That(FrameworkConfigRegistry.Snapshot().Select(item => item.Id), Does.Contain("font-charset"));

        [Test]
        public void FontCharsetWorkbench_ConsumesSharedOperationGate()
        {
            string source = ReadScriptSource(nameof(FontCharsetWindow));

            Assert.That(source, Does.Contain("FrameworkEditorOperationGate.CanStart"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isPlayingOrWillChangePlaymode"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isCompiling"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isUpdating"));
            Assert.That(source, Does.Not.Contain("BuildPipeline.isBuildingPlayer"));
        }

        private static string ReadScriptSource(string typeName)
        {
            string[] paths = AssetDatabase.FindAssets(typeName + " t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith("/" + typeName + ".cs", StringComparison.Ordinal))
                .ToArray();
            Assert.That(paths, Has.Length.EqualTo(1), "应精确找到 owner Module 内的窗口源码。");
            return AssetDatabase.LoadAssetAtPath<MonoScript>(paths[0]).text;
        }
    }
}
