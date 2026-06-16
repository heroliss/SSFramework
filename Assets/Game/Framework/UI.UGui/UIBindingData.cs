using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Framework.UI.UGui
{
    /// <summary>
    /// 挂在窗口 prefab 根上的「节点绑定清单」——记录哪些子节点、各绑哪些组件、生成的字段叫什么。
    /// 由编辑器侧的绑定工具增删，代码生成器据此产出 <c>*.nodes.g.cs</c>。
    /// </summary>
    /// <remarks>
    /// <b>纯编辑期元数据，运行时不被读取、不跑任何逻辑</b>——窗口运行时取节点靠生成代码里的 <c>transform.Find</c>
    /// （<see cref="UGuiWindowBase.BindNode{T}"/>），不读本组件。它存在的唯一意义是把绑定选择<b>随 prefab 一起序列化</b>，
    /// 从而吃到 Unity 预制体编辑的原生「撤销 / 脏标记 / 保存」机制（数据在 prefab 内容里而非 <c>.meta</c>，故能参与 Stage 的 dirty/undo/save）。
    /// 代价是会随 prefab 进包（一个无逻辑数据组件，footprint 极小）；真机要零残留可在资源构建期剥离本组件。
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("")] // 不出现在 Add Component 菜单——只由绑定工具增删，避免误手加到非根节点
    public sealed class UIBindingData : MonoBehaviour
    {
        [SerializeField] private List<UIBindingEntry> _entries = new();

        // 本窗口的生成目标覆盖：留空 = 用全工程 UICodeGenProfile 默认；非空 = 本 prefab 单独指定。
        // 纯编辑期配置，运行时不读。让「不同 prefab 生成到不同目录 / 命名空间」无需全局规则，且把生成目标随 prefab 带走。
        [SerializeField] private string _namespaceOverride;
        [SerializeField] private string _outputDirOverride;
        [SerializeField] private string _generatedDirOverride;

        /// <summary>全部绑定条目。编辑器工具直接增删（经 <c>Undo</c>），运行时不读。</summary>
        public List<UIBindingEntry> Entries => _entries;

        /// <summary>生成代码命名空间覆盖（留空 = 用 Profile 默认）。编辑期配置。</summary>
        public string NamespaceOverride { get => _namespaceOverride; set => _namespaceOverride = value; }

        /// <summary>手写逻辑 <c>&lt;Name&gt;.cs</c> 输出目录覆盖（留空 = 用 Profile 默认）。编辑期配置。</summary>
        public string OutputDirOverride { get => _outputDirOverride; set => _outputDirOverride = value; }

        /// <summary>生成的 <c>&lt;Name&gt;.nodes.g.cs</c> 输出目录覆盖（留空 = 用 Profile 默认）。编辑期配置。</summary>
        public string GeneratedDirOverride { get => _generatedDirOverride; set => _generatedDirOverride = value; }

        /// <summary>按相对根的路径找条目（无则 <c>null</c>）。</summary>
        public UIBindingEntry Find(string path) => _entries.Find(e => e.Path == path);

        /// <summary>移除某路径的整条绑定，返回是否移除了至少一条。</summary>
        public bool RemovePath(string path) => _entries.RemoveAll(e => e.Path == path) > 0;
    }

    /// <summary>清单里的一条绑定：一个 prefab 节点 → 一个或多个字段（每个组件一个字段）。</summary>
    [Serializable]
    public sealed class UIBindingEntry
    {
        /// <summary>相对窗口根（prefab 根）的节点路径，空串 = 根自身。生成代码以它喂 <c>transform.Find</c>。</summary>
        public string Path;

        /// <summary>
        /// 该节点要生成字段的组件类型 id（<c>FullName, AssemblyName</c> 形式，不含版本——稳定可解析，
        /// 区分 <c>UnityEngine.UI.Text</c> 与 <c>TMPro.TMP_Text</c>）。一个组件 = 一个字段。
        /// </summary>
        public List<string> ComponentTypes = new();

        /// <summary>生成字段名前缀（多组件时各自加组件名后缀）。留空 = 由节点名推导。</summary>
        public string FieldName;
    }
}
