#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using YooAsset.Editor; // BundleCollectorSettingData

namespace Game.Framework.Build
{
    /// <summary>
    /// 把「构建收集器（AssetBundleCollector）里定义的包名」注入运行时的
    /// <see cref="Game.Framework.AssetPackageConfig.EditorBuildPackageNamesProvider"/>，
    /// 让 <c>AssetUtility.Settings</c> 的包名字段能从收集器下拉选。
    ///
    /// <para>为什么走这条「反向依赖」：包名真源在 <c>BundleCollectorSettingData</c>（<c>YooAsset.Editor</c> 程序集），
    /// 而运行时程序集 <c>Game.Framework</c> 不能引用 <c>YooAsset.Editor</c>。于是由运行时<b>定义钩子</b>、本构建编辑器模块
    /// （可引用 YooAsset.Editor）<b>填充钩子</b>，既不破 asmdef 边界、也不用反射。</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class AssetPackageNameProviderInstaller
    {
        static AssetPackageNameProviderInstaller()
        {
            Game.Framework.AssetPackageConfig.EditorBuildPackageNamesProvider = GetCollectorPackageNames;
        }

        private static IEnumerable<string> GetCollectorPackageNames()
        {
            var setting = BundleCollectorSettingData.Setting;
            if (setting == null || setting.Packages == null)
                return Array.Empty<string>();
            return setting.Packages
                .Select(p => p.PackageName)
                .Where(n => !string.IsNullOrWhiteSpace(n));
        }
    }
}
#endif
