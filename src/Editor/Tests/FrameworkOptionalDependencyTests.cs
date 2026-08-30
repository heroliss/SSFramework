using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Model;
using Game.Framework.View;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定“框架原生基线不需要付费 Inspector 插件”的删除测试。</summary>
    public sealed class FrameworkOptionalDependencyTests
    {
        [Test]
        public void ReusableBaseline_HasNoSirenixCompileDependency()
        {
            UnityEditor.Compilation.Assembly[] allCompiled = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Concat(CompilationPipeline.GetAssemblies(AssembliesType.Player))
                .GroupBy(assembly => assembly.name, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            UnityEditor.Compilation.Assembly[] compiled = allCompiled
                .Where(assembly => IsFrameworkAssembly(assembly.name))
                .ToArray();
            Assert.That(compiled.Any(assembly => assembly.name == "Game.Framework"), Is.True,
                "Framework 编译图为空；拒绝把未扫描到源码/程序集误判为零依赖。");

            var assemblyPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (UnityEditor.Compilation.Assembly assembly in allCompiled)
            {
                AddAssemblyPath(assemblyPaths, assembly.outputPath);
                foreach (string referencePath in assembly.compiledAssemblyReferences ?? Array.Empty<string>())
                    AddAssemblyPath(assemblyPaths, referencePath);
            }

            var violations = new List<string>();
            int scannedSources = 0;
            int scannedAsmdefs = 0;
            foreach (UnityEditor.Compilation.Assembly assembly in compiled)
            {
                string asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                if (!string.IsNullOrEmpty(asmdefPath))
                {
                    scannedAsmdefs++;
                    string asmdef = File.ReadAllText(FrameworkModuleSourceCatalog.Resolve(asmdefPath).PhysicalPath);
                    if (asmdef.Contains("Sirenix", StringComparison.Ordinal))
                        violations.Add(assembly.name + " asmdef → " + asmdefPath);
                }

                string forbiddenPath = FindForbiddenDependencyPath(assembly.name, assemblyPaths);
                if (!string.IsNullOrEmpty(forbiddenPath))
                    violations.Add(assembly.name + " IL closure → " + forbiddenPath);

                if (IsTestAssembly(assembly.name)) continue;
                foreach (string sourcePath in assembly.sourceFiles ?? Array.Empty<string>())
                {
                    if (!sourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                    scannedSources++;
                    string source = File.ReadAllText(FrameworkModuleSourceCatalog.Resolve(sourcePath).PhysicalPath);
                    if (source.Contains("Sirenix", StringComparison.Ordinal))
                        violations.Add(assembly.name + " source → " + sourcePath);
                }
            }

            Assert.That(scannedSources, Is.GreaterThan(0), "Framework 生产源码扫描数必须大于 0。");
            Assert.That(scannedAsmdefs, Is.GreaterThan(0), "Framework asmdef 扫描数必须大于 0。");

            Assert.That(violations, Is.Empty,
                "通用基线出现 Sirenix 源码、asmdef 或已编译 IL 直接依赖；可选增强只能属于获准的 Game.Framework.Odin.* Adapter：\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void CoreMonoHosts_UseUnityNativeSerializationBase()
        {
            Assert.That(typeof(MonoGameContextBase).BaseType, Is.EqualTo(typeof(MonoBehaviour)));
            Assert.That(typeof(MonoViewBase).BaseType, Is.EqualTo(typeof(MonoBehaviour)));
            Assert.That(typeof(MonoModelBase).BaseType?.BaseType, Is.EqualTo(typeof(MonoBehaviour)));
        }

        [Test]
        public void CoreMonoWiringFields_UseConcreteUnitySerializationAndPlayModeLock()
        {
            AssertWiringField(typeof(MonoGameContextBase), "_parentContextHost", typeof(MonoGameContextBase));
            AssertWiringField(typeof(MonoGameContextBase), "_inheritFromParent", typeof(bool));
            AssertWiringField(typeof(MonoGameContextBase), "_inheritFromGlobal", typeof(bool));
            AssertWiringField(typeof(MonoViewBase), "_targetContext", typeof(MonoGameContextBase));
            AssertWiringField(typeof(MonoModelBase).BaseType, "_targetContext", typeof(MonoGameContextBase));
        }

        [Test]
        public void BuildSizeProbe_SourcePreparationHasNoPaidPluginCopyRecipe()
        {
            string source = File.ReadAllText(FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                "FrameworkBuildSizeProbe.cs", "Game.Framework.Editor").PhysicalPath);

            Assert.That(source, Does.Not.Contain("Assets/Plugins/Sirenix"));
        }

        [Test]
        public void NativeMonoInspectors_AreFallbackAndInstalledOdinAdapterOverridesConcreteOdinType()
        {
            string source = File.ReadAllText(FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                "FrameworkMonoInspectors.cs", "Game.Framework.Editor").PhysicalPath);

            int fallbackCount = source.Split("isFallback = true", StringSplitOptions.None).Length - 1;
            Assert.That(fallbackCount, Is.EqualTo(5),
                "无 Odin 时的五个 Mono Inspector 必须保持 fallback。");
            Assert.That(source, Does.Contain("finishedDefaultHeaderGUI"),
                "遵循默认 Header 流程的业务 Editor 接管后仍应保留框架诊断入口。");

            Type odinEditorType = Type.GetType(
                "Game.Framework.Odin.Editor.FrameworkOdinInspector, Game.Framework.Odin.Editor");
            Type registrationType = Type.GetType(
                "Game.Framework.Odin.Editor.FrameworkOdinEditorRegistration, Game.Framework.Odin.Editor");
            if (odinEditorType == null || registrationType == null) return;

            MethodInfo registerNow = registrationType.GetMethod(
                "RegisterNow", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo isOdinEnabled = registrationType.GetMethod(
                "IsOdinEnabledForType",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Type) },
                modifiers: null);
            Assert.That(registerNow, Is.Not.Null);
            Assert.That(isOdinEnabled, Is.Not.Null);

            var gameObject = new GameObject("OptionalOdinEditorProbe");
            UnityEditor.Editor editor = null;
            try
            {
                var assetConfig = gameObject.AddComponent<AssetSystemConfigModel>();
                bool expectedOdin = (bool)isOdinEnabled.Invoke(null, new object[] { typeof(AssetSystemConfigModel) });
                registerNow.Invoke(null, null);
                editor = UnityEditor.Editor.CreateEditor(assetConfig);
                Type expectedEditor = expectedOdin ? odinEditorType : typeof(MonoModelInspector);
                Assert.That(expectedEditor.IsAssignableFrom(editor.GetType()), Is.True,
                    "Adapter 所有权必须与 Odin Inspector 总开关、程序集分类和逐类型设置一致；" +
                    "禁用或排除 Odin 时还必须明确回退 Framework 原生 Inspector，不能落到无诊断的 OdinEditor。" +
                    $"期望 Editor：{expectedEditor.AssemblyQualifiedName}\n" +
                    $"实际 Editor：{editor.GetType().AssemblyQualifiedName}");

                Type demoModelType = Type.GetType(
                    "Game.Framework.Demo.Modules.MonoScoreModel, Game.Framework.Demo");
                if (demoModelType != null)
                {
                    UnityEngine.Object.DestroyImmediate(editor);
                    editor = null;
                    Component demoModel = gameObject.AddComponent(demoModelType);
                    bool demoExpectedOdin = (bool)isOdinEnabled.Invoke(null, new object[] { demoModelType });
                    registerNow.Invoke(null, null);
                    editor = UnityEditor.Editor.CreateEditor(demoModel);
                    Type demoExpectedEditor = demoExpectedOdin ? odinEditorType : typeof(MonoModelInspector);
                    Assert.That(demoExpectedEditor.IsAssignableFrom(editor.GetType()), Is.True,
                        "Demo 具体组件也必须落到能绘制 Framework 诊断的 Editor。" +
                        $"期望 Editor：{demoExpectedEditor.AssemblyQualifiedName}\n" +
                        $"实际 Editor：{editor.GetType().AssemblyQualifiedName}");
                }
            }
            finally
            {
                if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void OptionalModuleDiagnostics_RegisterThroughSharedInspectorSeam()
        {
            Type fontsType = Type.GetType("Game.Framework.Fonts.MonoLocaleFonts, Game.Framework.Fonts");
            if (fontsType == null) return;

            Assert.That(FrameworkInspectorDiagnostics.HasRegistrationFor(fontsType), Is.True,
                "Fonts 等可选 Module 的诊断必须经通用 contributor 接缝注册，不能只依赖 Odin 不保证触发的 Header 回调。");
        }

        private static void AssertWiringField(Type owner, string name, Type expectedType)
        {
            FieldInfo field = owner?.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"{owner?.Name}.{name} 必须保留稳定序列化字段名。");
            Assert.That(field.FieldType, Is.EqualTo(expectedType), $"{owner.Name}.{name} 类型漂移。");
            Assert.That(field.IsDefined(typeof(SerializeField), false), Is.True,
                $"{owner.Name}.{name} 必须由 Unity 原生序列化。");
            Assert.That(field.IsDefined(typeof(LockInPlayModeAttribute), false), Is.True,
                $"{owner.Name}.{name} 必须声明 Awake 后禁改语义。");
        }

        private static bool IsFrameworkAssembly(string name) =>
            (name == "Game.Framework" || name.StartsWith("Game.Framework.", StringComparison.Ordinal)) &&
            name != "Game.Framework.Odin" &&
            !name.StartsWith("Game.Framework.Odin.", StringComparison.Ordinal);

        private static bool IsTestAssembly(string name) =>
            name.Contains(".Test", StringComparison.Ordinal) || name.Contains(".Tests", StringComparison.Ordinal);

        private static bool IsSirenixAssembly(string name) =>
            name.StartsWith("Sirenix.", StringComparison.Ordinal) || name == "Sirenix";

        private static bool IsOdinAdapterAssembly(string name) =>
            name == "Game.Framework.Odin" || name.StartsWith("Game.Framework.Odin.", StringComparison.Ordinal);

        private static void AddAssemblyPath(IDictionary<string, string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(name) && !paths.ContainsKey(name)) paths[name] = path;
        }

        private static string FindForbiddenDependencyPath(
            string root,
            IReadOnlyDictionary<string, string> assemblyPaths)
        {
            var pending = new Queue<(string name, string path)>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { root };
            pending.Enqueue((root, root));
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!assemblyPaths.TryGetValue(current.name, out string path)) continue;
                foreach (string reference in FrameworkModuleAudit.ReadAssemblyReferences(path))
                {
                    string dependencyPath = current.path + " → " + reference;
                    if (IsSirenixAssembly(reference) || IsOdinAdapterAssembly(reference)) return dependencyPath;
                    if (visited.Add(reference)) pending.Enqueue((reference, dependencyPath));
                }
            }
            return string.Empty;
        }
    }
}
