using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 热更代码清单——构建管线生成、作为 RawFile 随代码包（CodePackage）下发，
    /// <c>HotUpdateLauncher</c> 据此决定「补哪些元数据、按什么顺序加载哪些热更 DLL」。
    ///
    /// 清单随资源走而不是烤进包体，意味着**热更程序集列表本身可热更**：
    /// 发版后新增热更程序集（如新玩法库）只需重发代码包，无需发新客户端。
    ///
    /// 两个列表都由构建管线自动生成，不手工维护：
    /// <list type="bullet">
    ///   <item><see cref="AotMetadataDlls"/> 来自 HybridCLR 生成的 <c>AOTGenericReferences.PatchedAOTAssemblyList</c>
    ///         （出包时被 IL2CPP 裁剪后的 AOT DLL，供 <c>LoadMetadataForAOTAssembly</c> 补泛型元数据）。</item>
    ///   <item><see cref="HotUpdateDlls"/> 按 asmdef 引用图拓扑排序（被依赖的在前），
    ///         引导器按序 <c>Assembly.Load</c> 即可，无需处理依赖解析。</item>
    /// </list>
    ///
    /// 序列化用 <see cref="JsonUtility"/>（仅公共字段、无多态需求，Boot 层不引第三方 JSON 库）。
    /// </summary>
    [Serializable]
    public sealed class HotUpdateManifest
    {
        /// <summary>代码包内清单文件的固定 location（YooAsset 可寻址名）。构建管线与引导器共用此约定。</summary>
        public const string Location = "hotupdate_manifest";

        /// <summary>本次代码构建的版本标识（与资源包版本同源，便于排查「代码-资源不配套」问题）。</summary>
        public string Version;

        /// <summary>需补充元数据的 AOT 程序集文件名（含 .dll.bytes 之类后缀由构建管线统一，引导器原样取用）。</summary>
        public List<string> AotMetadataDlls = new();

        /// <summary>热更程序集文件名，已按依赖拓扑排序，按序加载。</summary>
        public List<string> HotUpdateDlls = new();

        public static HotUpdateManifest FromJson(string json) => JsonUtility.FromJson<HotUpdateManifest>(json);

        public string ToJson(bool prettyPrint = false) => JsonUtility.ToJson(this, prettyPrint);
    }
}
