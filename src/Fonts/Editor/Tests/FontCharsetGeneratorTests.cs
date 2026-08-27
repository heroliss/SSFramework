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
                AssetDatabase.Refresh();
                if (!AssetDatabase.DeleteAsset(assetDirectory) && Directory.Exists(absoluteDirectory))
                {
                    Directory.Delete(absoluteDirectory, true);
                    if (File.Exists(absoluteDirectory + ".meta")) File.Delete(absoluteDirectory + ".meta");
                    AssetDatabase.Refresh();
                }
            }
        }

        private static void SetStringArray(SerializedProperty property, string value)
        {
            property.arraySize = 1;
            property.GetArrayElementAtIndex(0).stringValue = value;
        }
    }
}
