using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Fonts.Editor.Tests
{
    /// <summary>锁定字集生成的输入所有权，尤其是默认扫描 Assets 时不能把旧输出重新当成输入。</summary>
    public sealed class FontCharsetGeneratorTests
    {
        [Test]
        public void Regenerate_SkipsPreviousOutputSoRemovedCharactersDisappear()
        {
            string assetDirectory = "Assets/FontCharsetTest_" + Guid.NewGuid().ToString("N");
            string absoluteDirectory = Path.GetFullPath(assetDirectory);
            string inputPath = Path.Combine(absoluteDirectory, "Source.txt");
            string outputPath = assetDirectory + "/Charset.txt";
            var profile = ScriptableObject.CreateInstance<FontCharsetProfile>();
            try
            {
                Directory.CreateDirectory(absoluteDirectory);
                File.WriteAllText(inputPath, "新", new System.Text.UTF8Encoding(false));
                File.WriteAllText(Path.GetFullPath(outputPath), "旧", new System.Text.UTF8Encoding(false));

                var serialized = new SerializedObject(profile);
                SetStringArray(serialized.FindProperty("_scanDirs"), assetDirectory);
                SetStringArray(serialized.FindProperty("_filePatterns"), "*.txt");
                serialized.FindProperty("_includeAsciiPrintable").boolValue = false;
                serialized.FindProperty("_extraChars").stringValue = string.Empty;
                serialized.FindProperty("_outputPath").stringValue = outputPath;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var result = FontCharsetGenerator.TryGenerate(profile);

                Assert.That(result.ok, Is.True, result.message);
                Assert.That(File.ReadAllText(Path.GetFullPath(outputPath)), Is.EqualTo("新"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                DeleteAssetDirectory(assetDirectory);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_RejectsScanDirectoryOutsideProject()
        {
            var profile = CreateProfile(
                new[] { "Assets/../../Escape" },
                new[] { "*.txt" },
                includeAscii: true);
            try
            {
                var report = FontCharsetGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message, Does.Contain("字集扫描目录无效"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_AllowsMissingDirectoryButExplainsSkip()
        {
            string missingDirectory =
                "Temp/SSFrameworkFontReadiness_Missing_" + Guid.NewGuid().ToString("N");
            var profile = CreateProfile(
                new[] { missingDirectory },
                new[] { "*.txt" },
                includeAscii: true);
            try
            {
                var report = FontCharsetGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.True, report.Message);
                Assert.That(report.HasWarnings, Is.True);
                Assert.That(report.Message, Does.Contain("扫描目录不存在，将跳过"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [TestCase("../*.md")]
        [TestCase("..\\*.md")]
        [TestCase("/root/*.txt")]
        [TestCase("C:*.txt")]
        public void InspectGenerationPrerequisites_RejectsPatternThatCanEscapeScanRoot(
            string filePattern)
        {
            var profile = CreateProfile(
                new[] { "Assets" },
                new[] { filePattern },
                includeAscii: true);
            try
            {
                var report = FontCharsetGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message, Does.Contain("文件匹配模式无效"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_RejectsScanPathThatIsAFile()
        {
            string root = "Temp/SSFrameworkFontReadiness_" + Guid.NewGuid().ToString("N");
            string scanFile = root + "/Source.txt";
            var profile = CreateProfile(
                new[] { scanFile },
                new[] { "*.txt" },
                includeAscii: true);
            try
            {
                Directory.CreateDirectory(ProjectAbsolute(root));
                File.WriteAllText(ProjectAbsolute(scanFile), "text");

                var report = FontCharsetGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message, Does.Contain("路径当前是文件"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                DeleteProjectDirectory(root);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_RejectsOutputPathThatIsADirectory()
        {
            string assetRoot = "Assets/FontCharsetOutputDirectoryTest_" + Guid.NewGuid().ToString("N");
            string outputDirectory = assetRoot + "/Charset.txt";
            var profile = CreateProfile(
                new[] { "Assets" },
                new[] { "*.txt" },
                includeAscii: true,
                outputPath: outputDirectory);
            try
            {
                Directory.CreateDirectory(ProjectAbsolute(outputDirectory));

                var report = FontCharsetGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message, Does.Contain("目标当前是目录"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                DeleteAssetDirectory(assetRoot);
            }
        }

        [Test]
        public void TryGenerate_EmptyScanOnlyInputSucceedsWithExplicitWarning()
        {
            string tempRoot = "Temp/SSFrameworkFontReadiness_" + Guid.NewGuid().ToString("N");
            string assetRoot = "Assets/FontCharsetEmptyTest_" + Guid.NewGuid().ToString("N");
            var profile = CreateProfile(
                new[] { tempRoot },
                new[] { "*.txt" },
                includeAscii: false,
                outputPath: assetRoot + "/Charset.txt");
            try
            {
                Directory.CreateDirectory(ProjectAbsolute(tempRoot));

                var report = FontCharsetGenerator.InspectGenerationPrerequisites(profile);
                var result = FontCharsetGenerator.TryGenerate(profile);

                Assert.That(report.CanGenerate, Is.True, report.Message);
                Assert.That(report.HasWarnings, Is.True);
                Assert.That(report.Message, Does.Contain("结果可能为空"));
                Assert.That(result.ok, Is.True, result.message);
                Assert.That(result.count, Is.Zero);
                Assert.That(result.message, Does.Contain("⚠").And.Contain("已写入空字集"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                DeleteProjectDirectory(tempRoot);
                DeleteAssetDirectory(assetRoot);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_WarnsWhenNoCharacterSourceCanBeConfirmed()
        {
            var profile = CreateProfile(
                Array.Empty<string>(),
                Array.Empty<string>(),
                includeAscii: false);
            try
            {
                var report = FontCharsetGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.True, report.Message);
                Assert.That(report.HasWarnings, Is.True);
                Assert.That(report.Message,
                    Does.Contain("未配置扫描目录")
                        .And.Contain("未配置文件匹配模式")
                        .And.Contain("结果可能为空"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        private static FontCharsetProfile CreateProfile(
            string[] scanDirectories,
            string[] filePatterns,
            bool includeAscii,
            string outputPath = null)
        {
            var profile = ScriptableObject.CreateInstance<FontCharsetProfile>();
            var serialized = new SerializedObject(profile);
            SetStringArray(serialized.FindProperty("_scanDirs"), scanDirectories);
            SetStringArray(serialized.FindProperty("_filePatterns"), filePatterns);
            serialized.FindProperty("_includeAsciiPrintable").boolValue = includeAscii;
            serialized.FindProperty("_extraChars").stringValue = string.Empty;
            serialized.FindProperty("_outputPath").stringValue =
                outputPath ?? "Assets/Generated/SSFramework/Fonts/ReadinessCharset_" +
                Guid.NewGuid().ToString("N") + ".txt";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return profile;
        }

        private static void SetStringArray(SerializedProperty property, params string[] values)
        {
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                property.GetArrayElementAtIndex(i).stringValue = values[i];
        }

        private static string ProjectAbsolute(string relativePath) =>
            Path.GetFullPath(Path.Combine(
                Directory.GetParent(Application.dataPath)!.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

        private static void DeleteProjectDirectory(string relativePath)
        {
            string absolutePath = ProjectAbsolute(relativePath);
            if (Directory.Exists(absolutePath)) Directory.Delete(absolutePath, true);
            if (File.Exists(absolutePath + ".meta")) File.Delete(absolutePath + ".meta");
        }

        private static void DeleteAssetDirectory(string assetDirectory)
        {
            string absoluteDirectory = ProjectAbsolute(assetDirectory);
            AssetDatabase.Refresh();
            if (!AssetDatabase.DeleteAsset(assetDirectory) && Directory.Exists(absoluteDirectory))
            {
                Directory.Delete(absoluteDirectory, true);
                if (File.Exists(absoluteDirectory + ".meta")) File.Delete(absoluteDirectory + ".meta");
                AssetDatabase.Refresh();
            }
        }
    }
}
