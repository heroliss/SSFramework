using System;
using System.Collections.Generic;
using System.Linq;
using Game.NomadWorkshop;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Game.NomadWorkshop.Editor
{
    /// <summary>
    /// 《游牧工坊》角色资产 Spike 的可重复导入策略与审计入口。
    /// 这里只约束游戏自有的选定模型，不把第三方包的所有 FBX 或通用美术规则扩散到框架。
    /// </summary>
    public static class NomadHumanoidAssetPipeline
    {
        public const string CharacterModelPath =
            "Assets/Game/NomadWorkshop/ThirdParty/QuaterniusUniversalBaseCharacters/Superhero_Male_FullBody.fbx";

        public const string AnimationSourcePath =
            "Assets/Game/NomadWorkshop/ThirdParty/QuaterniusUniversalAnimationLibrary/Source/UAL1_Standard.fbx";

        public const string AnimationFolder = "Assets/Game/NomadWorkshop/Animation";
        public const string ClipFolder = AnimationFolder + "/Clips";
        public const string ControllerPath = AnimationFolder + "/NomadResident.controller";
        public const string MaterialFolder = "Assets/Game/NomadWorkshop/Materials";
        public const string BodyMaterialPath = MaterialFolder + "/ResidentBody.mat";
        public const string EyeMaterialPath = MaterialFolder + "/ResidentEyes.mat";
        public const string HairMaterialPath = MaterialFolder + "/ResidentHair.mat";

        private const string CharacterAssetFolder =
            "Assets/Game/NomadWorkshop/ThirdParty/QuaterniusUniversalBaseCharacters";
        private const string BodyColorTexturePath = CharacterAssetFolder + "/T_Superhero_Male_Ligh.png";
        private const string BodyNormalTexturePath = CharacterAssetFolder + "/T_Superhero_Male_Normal.png";
        private const string EyeColorTexturePath = CharacterAssetFolder + "/T_Eye_Brown.png";
        private const string EyeNormalTexturePath = CharacterAssetFolder + "/T_Eye_Normal.png";

        private static readonly ClipSelection[] SelectedClips =
        {
            new(ResidentAnimationStates.Idle, "Armature|Idle_Loop", "Resident_Idle", true),
            new(ResidentAnimationStates.Move, "Armature|Walk_Loop", "Resident_Walk", true),
            new(ResidentAnimationStates.Pickup, "Armature|PickUp_Table", "Resident_Pickup", false),
            new(ResidentAnimationStates.Work, "Armature|Fixing_Kneeling", "Resident_Work", false),
            new(ResidentAnimationStates.Rest, "Armature|Sitting_Idle_Loop", "Resident_Rest", true),
        };

        /// <summary>按当前 Spike 契约重设 ModelImporter，并在重导入后输出 Avatar 与动作清单。</summary>
        [MenuItem("SSFramework/游牧工坊/配置并审计 Humanoid 资产")]
        public static void ConfigureAndReport()
        {
            HumanoidAssetAudit characterAudit = ApplyCharacterImportPolicy(CharacterModelPath);
            string animationReport;
            if (AssetImporter.GetAtPath(AnimationSourcePath) is ModelImporter)
            {
                HumanoidAssetAudit animationAudit = ApplyAnimationImportPolicy(AnimationSourcePath);
                ExtractSelectedClipsAndBuildController(AnimationSourcePath);
                animationReport = animationAudit.ToMultilineString();
            }
            else
            {
                animationReport = "完整动作源未保留在仓库；正在审计已抽取的五个项目动作。";
            }

            ValidateGeneratedArtifacts();
            Debug.Log(characterAudit.ToMultilineString() + "\n" + animationReport +
                      "\n项目动作与 Animator Controller：验证通过。");
        }

        /// <summary>从完整第三方动作库抽取五个项目动作并生成稳定状态名的 Controller。</summary>
        public static void ExtractSelectedClipsAndBuildController(string sourceAssetPath)
        {
            AnimationClip[] sourceClips = AssetDatabase.LoadAllAssetsAtPath(sourceAssetPath)
                .OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                .ToArray();
            EnsureFolder(ClipFolder);

            for (int i = 0; i < SelectedClips.Length; i++)
            {
                ClipSelection selection = SelectedClips[i];
                AnimationClip source = sourceClips.FirstOrDefault(
                    clip => string.Equals(clip.name, selection.SourceClipName, StringComparison.Ordinal));
                if (source == null)
                    throw new InvalidOperationException(
                        $"动作库缺少必需动作：{selection.SourceClipName}（状态 {selection.StateName}）");

                string outputPath = selection.OutputPath;
                AnimationClip destination = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath);
                if (destination == null)
                {
                    destination = UnityEngine.Object.Instantiate(source);
                    destination.name = selection.AssetName;
                    destination.hideFlags = HideFlags.None;
                    AssetDatabase.CreateAsset(destination, outputPath);
                }
                else
                {
                    EditorUtility.CopySerialized(source, destination);
                    destination.name = selection.AssetName;
                    destination.hideFlags = HideFlags.None;
                    EditorUtility.SetDirty(destination);
                }
            }

            BuildAnimatorController();
            AssetDatabase.SaveAssets();
        }

        /// <summary>验证仓库内最终保留的五个动作和稳定 Controller，不依赖完整第三方源 FBX。</summary>
        public static void ValidateGeneratedArtifacts()
        {
            for (int i = 0; i < SelectedClips.Length; i++)
            {
                ClipSelection selection = SelectedClips[i];
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(selection.OutputPath);
                if (clip == null) throw new InvalidOperationException($"缺少项目动作：{selection.OutputPath}");
                if (!clip.humanMotion) throw new InvalidOperationException($"项目动作不是 Human Motion：{selection.OutputPath}");
            }

            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null) throw new InvalidOperationException($"缺少 Animator Controller：{ControllerPath}");
            if (controller.layers.Length != 1) throw new InvalidOperationException("居民 Controller 必须只保留一个表现层。");

            ChildAnimatorState[] states = controller.layers[0].stateMachine.states;
            if (states.Length != SelectedClips.Length)
                throw new InvalidOperationException($"居民 Controller 必须正好包含 {SelectedClips.Length} 个语义状态。");

            for (int i = 0; i < SelectedClips.Length; i++)
            {
                ClipSelection selection = SelectedClips[i];
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(selection.OutputPath);
                AnimatorState state = states
                    .Select(child => child.state)
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.name, selection.StateName, StringComparison.Ordinal));
                if (state?.motion != clip)
                    throw new InvalidOperationException($"Controller 状态没有引用预期项目动作：{selection.StateName}");
            }

            if (controller.layers[0].stateMachine.defaultState?.name != ResidentAnimationStates.Idle)
                throw new InvalidOperationException("居民 Controller 的默认状态必须是 Idle。");
        }

        /// <summary>
        /// 将指定 FBX 配置为无 Root Motion 的 Unity Humanoid。调用会触发同步重导入，适合显式工具和测试准备，
        /// 不应放入普通运行时代码或无条件 AssetPostprocessor 中。
        /// </summary>
        public static HumanoidAssetAudit ApplyCharacterImportPolicy(string assetPath)
        {
            ConfigureCharacterTextures();
            Material bodyMaterial = CreateOrUpdateMaterial(
                BodyMaterialPath,
                AssetDatabase.LoadAssetAtPath<Texture2D>(BodyColorTexturePath),
                AssetDatabase.LoadAssetAtPath<Texture2D>(BodyNormalTexturePath),
                Color.white,
                0.22f);
            Material eyeMaterial = CreateOrUpdateMaterial(
                EyeMaterialPath,
                AssetDatabase.LoadAssetAtPath<Texture2D>(EyeColorTexturePath),
                AssetDatabase.LoadAssetAtPath<Texture2D>(EyeNormalTexturePath),
                Color.white,
                0.48f);
            Material hairMaterial = CreateOrUpdateMaterial(
                HairMaterialPath,
                null,
                null,
                new Color(0.12f, 0.065f, 0.035f, 1f),
                0.18f);

            ModelImporter importer = RequireModelImporter(assetPath);
            ApplySharedHumanoidSettings(importer);
            importer.importAnimation = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            RemapMaterial(importer, "MI_Superhero_Male", bodyMaterial);
            RemapMaterial(importer, "MI_Eyes", eyeMaterial);
            RemapMaterial(importer, "MI_Hair_1", hairMaterial);
            importer.SaveAndReimport();
            return Audit(assetPath);
        }

        /// <summary>配置动作库源 FBX，并将所有位移动作锁定为原地播放，角色移动仍由模拟展示层拥有。</summary>
        public static HumanoidAssetAudit ApplyAnimationImportPolicy(string assetPath)
        {
            ModelImporter importer = RequireModelImporter(assetPath);
            ApplySharedHumanoidSettings(importer);
            importer.importAnimation = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;

            ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
            for (int i = 0; i < clips.Length; i++)
            {
                ModelImporterClipAnimation clip = clips[i];
                ClipSelection selected = SelectedClips.FirstOrDefault(candidate =>
                    string.Equals(candidate.SourceClipName, clip.name, StringComparison.Ordinal));
                bool shouldLoop = selected != null && selected.ShouldLoop;
                clip.loopTime = shouldLoop;
                clip.loopPose = shouldLoop;
                clip.lockRootRotation = true;
                clip.lockRootHeightY = true;
                clip.lockRootPositionXZ = true;
                clip.keepOriginalOrientation = true;
                clip.keepOriginalPositionY = true;
                clip.keepOriginalPositionXZ = true;
            }

            importer.clipAnimations = clips;
            importer.SaveAndReimport();
            return Audit(assetPath);
        }

        private static ModelImporter RequireModelImporter(string assetPath)
        {
            if (AssetImporter.GetAtPath(assetPath) is not ModelImporter importer)
                throw new InvalidOperationException($"没有找到可配置的模型导入器：{assetPath}");

            return importer;
        }

        private static void ApplySharedHumanoidSettings(ModelImporter importer)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importConstraints = false;
            importer.optimizeGameObjects = false;
            importer.isReadable = false;
            importer.globalScale = 1f;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.animationCompression = ModelImporterAnimationCompression.Optimal;
        }

        private static void BuildAnimatorController()
        {
            EnsureFolder(AnimationFolder);
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
                controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
            ChildAnimatorState[] oldStates = stateMachine.states;
            for (int i = 0; i < oldStates.Length; i++) stateMachine.RemoveState(oldStates[i].state);

            AnimatorState defaultState = null;
            for (int i = 0; i < SelectedClips.Length; i++)
            {
                ClipSelection selection = SelectedClips[i];
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(selection.OutputPath);
                if (clip == null) throw new InvalidOperationException($"没有生成动作资产：{selection.OutputPath}");

                AnimatorState state = stateMachine.AddState(selection.StateName);
                state.motion = clip;
                state.writeDefaultValues = false;
                if (selection.StateName == ResidentAnimationStates.Idle) defaultState = state;
            }

            stateMachine.defaultState = defaultState;
            EditorUtility.SetDirty(controller);
        }

        private static void ConfigureCharacterTextures()
        {
            ConfigureTexture(BodyColorTexturePath, false);
            ConfigureTexture(BodyNormalTexturePath, true);
            ConfigureTexture(EyeColorTexturePath, false);
            ConfigureTexture(EyeNormalTexturePath, true);
        }

        private static void ConfigureTexture(string assetPath, bool normalMap)
        {
            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer)
                throw new InvalidOperationException($"缺少角色贴图：{assetPath}");

            TextureImporterType requiredType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            bool requiredSrgb = !normalMap;
            if (importer.textureType == requiredType && importer.sRGBTexture == requiredSrgb) return;

            importer.textureType = requiredType;
            importer.sRGBTexture = requiredSrgb;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }

        private static Material CreateOrUpdateMaterial(
            string assetPath,
            Texture2D baseMap,
            Texture2D normalMap,
            Color baseColor,
            float smoothness)
        {
            EnsureFolder(MaterialFolder);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("当前项目找不到 Universal Render Pipeline/Lit Shader。");

            Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, assetPath);
            }
            else
            {
                material.shader = shader;
            }

            material.SetTexture("_BaseMap", baseMap);
            material.SetColor("_BaseColor", baseColor);
            material.SetFloat("_Smoothness", smoothness);
            material.SetTexture("_BumpMap", normalMap);
            if (normalMap != null) material.EnableKeyword("_NORMALMAP");
            else material.DisableKeyword("_NORMALMAP");
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        private static void RemapMaterial(ModelImporter importer, string sourceName, Material material)
        {
            var identifier = new AssetImporter.SourceAssetIdentifier(typeof(Material), sourceName);
            importer.RemoveRemap(identifier);
            importer.AddRemap(identifier, material);
        }

        private static void EnsureFolder(string folderPath)
        {
            string[] segments = folderPath.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }

        /// <summary>读取已经导入的子资源，不修改导入设置。</summary>
        public static HumanoidAssetAudit Audit(string assetPath)
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            Avatar avatar = assets.OfType<Avatar>().FirstOrDefault();
            string[] clipNames = assets
                .OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                .Select(clip => clip.name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            string[] humanClipNames = assets
                .OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal) && clip.humanMotion)
                .Select(clip => clip.name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            return new HumanoidAssetAudit(
                assetPath,
                avatar != null,
                avatar != null && avatar.isValid,
                avatar != null && avatar.isHuman,
                clipNames,
                humanClipNames);
        }

        private sealed class ClipSelection
        {
            public ClipSelection(string stateName, string sourceClipName, string assetName, bool shouldLoop)
            {
                StateName = stateName;
                SourceClipName = sourceClipName;
                AssetName = assetName;
                ShouldLoop = shouldLoop;
            }

            public string StateName { get; }

            public string SourceClipName { get; }

            public string AssetName { get; }

            public bool ShouldLoop { get; }

            public string OutputPath => $"{ClipFolder}/{AssetName}.anim";
        }
    }

    /// <summary>可由菜单、测试和后续批处理共同消费的只读导入证据。</summary>
    public sealed class HumanoidAssetAudit
    {
        public HumanoidAssetAudit(
            string assetPath,
            bool hasAvatar,
            bool avatarIsValid,
            bool avatarIsHuman,
            IReadOnlyList<string> clipNames,
            IReadOnlyList<string> humanClipNames)
        {
            AssetPath = assetPath;
            HasAvatar = hasAvatar;
            AvatarIsValid = avatarIsValid;
            AvatarIsHuman = avatarIsHuman;
            ClipNames = clipNames;
            HumanClipNames = humanClipNames;
        }

        public string AssetPath { get; }

        public bool HasAvatar { get; }

        public bool AvatarIsValid { get; }

        public bool AvatarIsHuman { get; }

        public IReadOnlyList<string> ClipNames { get; }

        public IReadOnlyList<string> HumanClipNames { get; }

        public string ToMultilineString()
        {
            string clips = ClipNames.Count == 0 ? "（无）" : string.Join("\n  - ", ClipNames);
            return $"[Nomad Humanoid Audit]\n路径：{AssetPath}\n" +
                   $"Avatar：存在={HasAvatar}，有效={AvatarIsValid}，Humanoid={AvatarIsHuman}\n" +
                   $"动作：{ClipNames.Count} 个，Humanoid 动作：{HumanClipNames.Count} 个\n  - {clips}";
        }
    }
}
