using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 服务安装器生成配置：声明「哪些目录的服务类 → 生成哪个安装器」的映射（ADR-0019）。
    /// 每个条目独立生成一个静态安装器类（<c>XxxInstaller.Install(builder)</c>），
    /// 装进哪个 Context 由业务在该 Context 的 <c>InstallBindings</c> 里手写一行调用决定——生成器刻意不指认 Context。
    ///
    /// <para>这是<b>项目配置实例</b>，资产入库放项目配置位（如 <c>Assets/Settings/SSFramework/</c>），不放 <c>Framework/</c> 内
    /// （框架抽 UPM 包时项目配置不该进包，ADR-0010/0011）。<b>工程可并存多份</b>（如按子项目、环境或功能域拆分）：
    /// 每份的条目指向各自的扫描目录与输出、生成互不干扰。生成入口：菜单
    /// <c>SSFramework/服务注册/生成服务安装器代码</c>（扫全部 profile）、「配置总览」窗口（按份操作），
    /// 或本资产 Inspector 的生成按钮。</para>
    /// </summary>
    [CreateAssetMenu(fileName = "ServiceInstallerProfile", menuName = "SSFramework/服务安装器配置 (Service Installer Profile)")]
    public sealed class ServiceInstallerProfile : ScriptableObject
    {
        /// <summary>一个安装器条目：N 个扫描目录 → 1 个生成的安装器类。</summary>
        [Serializable]
        public sealed class InstallerEntry
        {
            [Tooltip("扫描目录（文件夹资产，含子目录）。目录下「文件名 = 类名」的纯 C# 服务类（实现 IModel / ISystem / IUtility 派生接口）会被收进本安装器。")]
            public List<UnityEditor.DefaultAsset> ScanFolders = new();

            [Tooltip("安装器输出路径：Assets/ 开头、.cs 结尾（建议 .g.cs）。类名 = 文件名去掉 .g.cs 后缀。新条目留空，需按目标业务程序集明确填写。")]
            public string OutputPath = "";

            [Tooltip("安装器的命名空间。")]
            public string Namespace = "";
        }

        [Tooltip("安装器条目列表。每条独立生成、独立报错，互不影响。")]
        public List<InstallerEntry> Installers = new();

        /// <summary>
        /// 返回工程内**所有**安装器 profile（按资产路径排序，显示稳定）。多份并存是设计意图，
        /// 生成入口逐份生成、互不干扰。一份都没有时返回空列表——不自动创建：扫描目录 / 输出路径
        /// 没有可猜的默认值，空 profile 只会误导（对比能从既有目录约定推导初值的配置类型）。
        /// </summary>
        public static IReadOnlyList<ServiceInstallerProfile> ResolveAll()
        {
            return AssetDatabase.FindAssets("t:" + nameof(ServiceInstallerProfile))
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<ServiceInstallerProfile>)
                .Where(p => p != null)
                .ToList();
        }
    }
}
