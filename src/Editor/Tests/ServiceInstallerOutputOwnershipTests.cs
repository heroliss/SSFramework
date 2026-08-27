using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定服务安装器条目的输出文件唯一性，避免批量生成静默覆盖。</summary>
    public sealed class ServiceInstallerOutputOwnershipTests
    {
        [Test]
        public void GenerateEntry_NullEntryReturnsStructuredFailure()
        {
            var result = ServiceInstallerGenerator.GenerateEntry(null);
            Assert.That(result.ok, Is.False);
            Assert.That(result.message, Does.Contain("不能为空"));
        }

        [Test]
        public void ValidateOutputOwnership_AcceptsUniqueFilesInSameDirectory()
        {
            var profile = CreateProfile(
                "Assets/Generated/Installers/First.g.cs",
                "Assets/Generated/Installers/Second.g.cs");
            try
            {
                var result = ServiceInstallerGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.True, result.message);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ValidateOutputOwnership_RejectsNormalizedDuplicateFiles()
        {
            var profile = CreateProfile(
                "Assets/Generated/Installers/Duplicate.g.cs",
                "Assets/Generated/Other/../Installers/Duplicate.g.cs");
            try
            {
                var result = ServiceInstallerGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("所有权冲突"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ValidateOutputOwnership_RejectsCaseOnlyDuplicateFiles()
        {
            var profile = CreateProfile(
                "Assets/Generated/Installers/Case.g.cs",
                "Assets/Generated/installers/case.g.cs");
            try
            {
                var result = ServiceInstallerGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("所有权冲突"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ValidateOutputOwnership_RejectsEscapingFile()
        {
            var profile = CreateProfile("Assets/../../Installer.g.cs");
            try
            {
                var result = ServiceInstallerGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("输出路径无效"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static ServiceInstallerProfile CreateProfile(params string[] outputPaths)
        {
            var profile = ScriptableObject.CreateInstance<ServiceInstallerProfile>();
            profile.name = "InstallerOwnershipTest";
            foreach (string path in outputPaths)
                profile.Installers.Add(new ServiceInstallerProfile.InstallerEntry { OutputPath = path });
            return profile;
        }
    }
}
