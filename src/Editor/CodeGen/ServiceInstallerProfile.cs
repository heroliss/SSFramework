using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 服务安装器生成配置：声明「哪些目录的服务类 → 生成哪个安装器」的映射（ADR-0019）。
    /// 每个条目独立生成一个静态安装器类（<c>XxxInstaller.Install(builder)</c>），
    /// 装进哪个 Context 由业务在该 Context 的 <c>InstallBindings</c> 里手写一行调用决定——生成器刻意不指认 Context。
    ///
    /// <para>这是<b>项目配置实例</b>，资产入库放项目配置位（如 <c>Assets/Game/Settings/</c>），不放 <c>Framework/</c> 内
    /// （框架抽 UPM 包时项目配置不该进包，ADR-0010/0011）。生成入口：菜单
    /// <c>SSFramework/服务注册/生成服务安装器代码</c>（扫全部 profile）或本资产 Inspector 的生成按钮。</para>
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

            [Tooltip("安装器输出路径：Assets/ 开头、.cs 结尾（建议 .g.cs）。类名 = 文件名去掉 .g.cs 后缀。")]
            public string OutputPath = "Assets/Game/Main/Generated/MainServicesInstaller.g.cs";

            [Tooltip("安装器的命名空间。")]
            public string Namespace = "Game.Main";
        }

        [Tooltip("安装器条目列表。每条独立生成、独立报错，互不影响。")]
        public List<InstallerEntry> Installers = new();
    }
}
