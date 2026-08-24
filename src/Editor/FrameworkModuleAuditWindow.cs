using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 展示 Framework Module 的真实托管引用闭包、轻量组合档位与删除测试。
    /// </summary>
    public sealed class FrameworkModuleAuditWindow : EditorWindow
    {
        private TextField _report;
        private HelpBox _status;

        [MenuItem("SSFramework/诊断/模块裁剪审计", priority = 20)]
        public static void Open() => GetWindow<FrameworkModuleAuditWindow>("模块裁剪审计").Show();

        public void CreateGUI()
        {
            minSize = new Vector2(360f, 320f);
            var root = rootVisualElement;
            root.Clear();

            var actions = new VisualElement
            {
                name = "module-audit-actions",
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    paddingLeft = 6,
                    paddingRight = 6,
                    paddingTop = 4,
                    paddingBottom = 4,
                },
            };
            actions.Add(new Button(Refresh) { text = "重新审计", tooltip = "重新读取 Player 编译图与当前 DLL 元数据。" });
            actions.Add(new Button(CopyReport) { text = "复制报告", tooltip = "复制纯文本报告，便于提交评审或粘进 issue。" });
            actions.Add(new Button(() => OpenAsset("docs/framework-module-map.md"))
            {
                text = "Module 地图",
                tooltip = "打开程序集职责、依赖方向与删除测试文档。",
            });
            root.Add(actions);

            _status = new HelpBox(
                "报告中的大小是链接 / AOT / 压缩前的托管 DLL 证据，不是最终玩家包体；目标平台结论仍以 Player BuildReport 为准。",
                HelpBoxMessageType.Info)
            {
                name = "module-audit-status",
            };
            root.Add(_status);

            _report = new TextField
            {
                name = "module-audit-report",
                multiline = true,
                isReadOnly = true,
                style =
                {
                    flexGrow = 1,
                    marginLeft = 6,
                    marginRight = 6,
                    marginTop = 4,
                    marginBottom = 6,
                    minHeight = 180,
                },
            };
            root.Add(_report);
            Refresh();
        }

        private void Refresh()
        {
            if (_report == null) return;
            try
            {
                _report.value = FrameworkModuleAudit.CreateReport(FrameworkModuleAudit.Capture());
                _status.text = "审计完成。结果来自当前目标平台的 Player 编译图与已编译 DLL 元数据。";
                _status.messageType = HelpBoxMessageType.Info;
            }
            catch (System.Exception ex)
            {
                _report.value = ex.ToString();
                _status.text = "审计失败；下方保留完整异常，便于定位编译图或程序集读取问题。";
                _status.messageType = HelpBoxMessageType.Error;
            }
        }

        private void CopyReport()
        {
            EditorGUIUtility.systemCopyBuffer = _report?.value ?? string.Empty;
            if (_status != null)
            {
                _status.text = "报告已复制到剪贴板。";
                _status.messageType = HelpBoxMessageType.Info;
            }
        }

        private static void OpenAsset(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) return;
            AssetDatabase.OpenAsset(asset);
            EditorGUIUtility.PingObject(asset);
        }
    }
}
