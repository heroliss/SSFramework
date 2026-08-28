using System;
using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;
using UnityEditor;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定 Luban Editor Module 自己拥有配置中心登记，中央窗口无需知道该可选程序集。</summary>
    public sealed class ConfigEditorCatalogRegistrationTests
    {
        [Test]
        public void LubanModule_RegistersOwnedConfiguration() =>
            Assert.That(FrameworkConfigRegistry.Snapshot().Select(item => item.Id), Does.Contain("luban"));

        [Test]
        public void LubanWorkbench_ConsumesSharedOperationGate()
        {
            string source = ReadScriptSource(nameof(LubanConfigOverviewWindow));

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
