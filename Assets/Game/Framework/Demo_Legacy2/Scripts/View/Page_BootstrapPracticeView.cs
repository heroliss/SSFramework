using System;
using System.Collections.Generic;
using System.Text;
using Game.Framework.Common;
using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// 章节 7 — 启动实践：列出当前 Context 已注册的层。
    /// </summary>
    /// <remarks>
    /// 演示意图：让用户**亲眼看到 Container 里到底有什么**，验证 InstallBindings + MonoXxxBase 自动注册的结果。<br/>
    /// 在 Inspector 配 <c>_targets</c>（要检查的层类型列表，如 CounterModel / ICounterSystem / IFormatterUtility），
    /// 点 Refresh 把每个类型的解析结果（✓ / ✗）写到文本区。
    /// </remarks>
    public sealed class Page_BootstrapPracticeView : MonoViewBase
    {
        [Serializable]
        public sealed class TargetEntry
        {
            [Tooltip("展示名（如 \"CounterModel\" / \"ICounterSystem\"）。")]
            public string DisplayName;

            [Tooltip("完整类型名（如 \"Game.Framework.Demo.Model.CounterModel, Game.Framework.Demo\"）。" +
                     "通过 Type.GetType() 查找——同程序集时只需类型全名即可。")]
            public string FullTypeName;
        }

        [Header("要检查的层")]
        [SerializeField] private List<TargetEntry> _targets = new();

        [Header("UI")]
        [SerializeField] private Button _refreshBtn;
        [SerializeField] private TMP_Text _output;

        protected override void Awake()
        {
            base.Awake();
            Bag.Subscribe(_refreshBtn.onClick, Refresh);
            Refresh();
        }

        private static Type ResolveType(string fullTypeName)
        {
            var type = Type.GetType(fullTypeName, throwOnError: false);
            if (type != null) return type;

            string typeName = fullTypeName.Split(',')[0].Trim();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(typeName, throwOnError: false);
                if (type != null) return type;
            }
            return null;
        }

        private void Refresh()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _targets.Count; i++)
            {
                var t = _targets[i];
                if (t == null || string.IsNullOrWhiteSpace(t.FullTypeName))
                {
                    sb.AppendLine("(空条目)");
                    continue;
                }
                var type = ResolveType(t.FullTypeName);
                if (type == null)
                {
                    sb.AppendLine($"[MISS] {t.DisplayName ?? t.FullTypeName} — 类型未找到");
                    continue;
                }
                bool ok = this.ExecuteCommand(new Command.CheckResolveCommand(type));
                sb.AppendLine(ok
                    ? $"[OK]   {t.DisplayName ?? type.Name}"
                    : $"[MISS] {t.DisplayName ?? type.Name} — 未注册");
            }
            _output.text = sb.ToString();
        }
    }
}
