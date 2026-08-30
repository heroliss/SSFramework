#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Framework.Editor
{
    /// <summary>
    /// AssetReference 的 Inspector 绘制器。
    /// 通过 GUID 反查资源并显示 ObjectField，支持拖拽赋值和“资源缺失”提示。
    /// </summary>
    [CustomPropertyDrawer(typeof(AssetReferenceBase), true)]
    public class AssetReferenceDrawer : PropertyDrawer
    {
        private const string GUIDPropName = "_assetGUID";
        private const string PackagePropName = "_packageName";
        private const float InlineObjectMinWidth = 72f;
        private const float InlinePackageMinWidth = 72f;
        private const float PackageMaxWidth = 120f;
        private const float PackageGap = 4f;
        private const string RuntimeDefaultPackageLabel = "运行时默认包";
        private const string RuntimeDefaultPackageTooltip =
            "留空 = 加载时使用该引用绑定的 IAssetUtility 默认包。" +
            "Inspector 不会从所有已打开 Context 中猜测某个全局默认值。";

        internal enum LayoutMode
        {
            Inline,
            Compact,
        }

        internal readonly struct InlineWidths
        {
            public readonly float Object;
            public readonly float Gap;
            public readonly float Package;

            public InlineWidths(float objectWidth, float gap, float packageWidth)
            {
                Object = objectWidth;
                Gap = gap;
                Package = packageWidth;
            }
        }

        // 按 (宿主类型, propertyPath) 缓存字段类型，避免每帧都走反射
        private static readonly Dictionary<(Type, string), Type> _fieldTypeCache = new();

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var guidProp = property.FindPropertyRelative(GUIDPropName);
            var packageProp = property.FindPropertyRelative(PackagePropName);
            Type assetType = ResolveAssetType(property);

            string guid = guidProp?.stringValue ?? "";
            bool isMissing = false;
            Object currentAsset = null;

            if (!string.IsNullOrEmpty(guid))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    currentAsset = AssetDatabase.LoadAssetAtPath(assetPath, assetType);
                    if (currentAsset == null) isMissing = true;
                }
                else
                {
                    isMissing = true;
                }
            }

            float lineHeight = EditorGUIUtility.singleLineHeight;
            float spacing = EditorGUIUtility.standardVerticalSpacing;
            bool hasVisibleLabel = HasVisibleLabel(label);
            var mode = ResolveCurrentLayoutMode(hasVisibleLabel);

            Rect objectRect;
            Rect packageRect;
            if (mode == LayoutMode.Compact)
            {
                float y = position.y;
                if (hasVisibleLabel)
                {
                    EditorGUI.LabelField(new Rect(position.x, y, position.width, lineHeight), label);
                    y += lineHeight + spacing;
                }

                var fieldRect = EditorGUI.IndentedRect(new Rect(position.x, y, position.width, lineHeight));
                objectRect = fieldRect;
                packageRect = new Rect(fieldRect.x, y + lineHeight + spacing, fieldRect.width, lineHeight);
            }
            else
            {
                var lineRect = new Rect(position.x, position.y, position.width, lineHeight);
                var contentRect = EditorGUI.PrefixLabel(lineRect, label);
                var widths = CalculateInlineWidths(contentRect.width);
                objectRect = new Rect(contentRect.x, contentRect.y, widths.Object, contentRect.height);
                packageRect = new Rect(objectRect.xMax + widths.Gap, contentRect.y, widths.Package, contentRect.height);
            }

            Object newAsset = EditorGUI.ObjectField(objectRect, GUIContent.none, currentAsset, assetType, false);
            DrawPackageDropdown(packageRect, packageProp);

            if (isMissing && newAsset == null)
                EditorGUI.LabelField(objectRect, $"资源缺失（{assetType.Name}）", EditorStyles.objectField);

            if (newAsset != currentAsset)
            {
                if (newAsset != null && AssetDatabase.Contains(newAsset))
                    guidProp.stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(newAsset));
                else
                    guidProp.stringValue = "";
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            bool hasVisibleLabel = HasVisibleLabel(label);
            var mode = ResolveCurrentLayoutMode(hasVisibleLabel);
            return CalculateHeight(mode, hasVisibleLabel,
                EditorGUIUtility.singleLineHeight, EditorGUIUtility.standardVerticalSpacing);
        }

        internal static LayoutMode ResolveLayoutMode(bool wideMode, bool hasVisibleLabel, float estimatedControlWidth)
        {
            if (hasVisibleLabel && !wideMode) return LayoutMode.Compact;
            return estimatedControlWidth < InlineObjectMinWidth + PackageGap + InlinePackageMinWidth
                ? LayoutMode.Compact
                : LayoutMode.Inline;
        }

        internal static InlineWidths CalculateInlineWidths(float availableWidth)
        {
            float available = Math.Max(0f, availableWidth);
            float gap = Math.Min(PackageGap, available);
            float controls = available - gap;
            if (controls <= 0f) return new InlineWidths(0f, gap, 0f);

            // 正常宽度优先保证 ObjectField 可辨认；异常窄的自定义 Inspector 即使没有触发 Compact，
            // 也安全平分剩余空间，绝不产生负 Rect 或让包按钮覆盖 ObjectField。
            float package = controls < InlineObjectMinWidth + InlinePackageMinWidth
                ? controls * 0.5f
                : Math.Min(PackageMaxWidth, Math.Max(InlinePackageMinWidth, available * 0.28f));
            package = Math.Min(package, controls);
            return new InlineWidths(controls - package, gap, package);
        }

        internal static float CalculateHeight(LayoutMode mode, bool hasVisibleLabel, float lineHeight, float spacing)
        {
            if (mode == LayoutMode.Inline) return lineHeight;
            int lineCount = hasVisibleLabel ? 3 : 2;
            return lineCount * lineHeight + (lineCount - 1) * spacing;
        }

        private static LayoutMode ResolveCurrentLayoutMode(bool hasVisibleLabel)
        {
            float estimatedControlWidth = EditorGUIUtility.currentViewWidth - 22f;
            if (hasVisibleLabel && EditorGUIUtility.wideMode)
                estimatedControlWidth -= EditorGUIUtility.labelWidth;
            estimatedControlWidth -= EditorGUI.indentLevel * 15f;
            return ResolveLayoutMode(EditorGUIUtility.wideMode, hasVisibleLabel, estimatedControlWidth);
        }

        private static bool HasVisibleLabel(GUIContent label)
            => label != null && label != GUIContent.none && !string.IsNullOrEmpty(label.text);

        private static void DrawPackageDropdown(Rect rect, SerializedProperty packageProp)
        {
            if (packageProp == null)
            {
                EditorGUI.LabelField(
                    rect,
                    new GUIContent(RuntimeDefaultPackageLabel, RuntimeDefaultPackageTooltip),
                    EditorStyles.popup);
                return;
            }

            string current = packageProp.stringValue;
            string text = GetPackageDisplayText(current);
            string tooltip = GetPackageTooltip(current);
            if (!GUI.Button(rect, new GUIContent(text, tooltip), EditorStyles.popup)) return;

            var menu = new GenericMenu();
            menu.AddItem(
                new GUIContent($"（{RuntimeDefaultPackageLabel}）", RuntimeDefaultPackageTooltip),
                string.IsNullOrEmpty(current),
                () => SetPackageName(packageProp, ""));
            var knownPackages = EnumerateKnownPackages();
            if (knownPackages.Count > 0)
            {
                menu.AddSeparator("");
                menu.AddDisabledItem(new GUIContent("已加载配置中的候选（仅用于录入）"));
            }
            foreach (var packageName in knownPackages)
            {
                var captured = packageName;
                menu.AddItem(new GUIContent(captured), current == captured, () => SetPackageName(packageProp, captured));
            }
            menu.AddSeparator("");
            Vector2 popupAnchor = GUIUtility.GUIToScreenPoint(new Vector2(rect.x, rect.yMax));
            menu.AddItem(new GUIContent("自定义..."), false, () => PackageNamePopup.Open(packageProp, popupAnchor));
            menu.DropDown(rect);
        }

        internal static string GetPackageDisplayText(string packageName)
            => string.IsNullOrEmpty(packageName) ? RuntimeDefaultPackageLabel : packageName;

        internal static string GetPackageTooltip(string packageName)
            => string.IsNullOrEmpty(packageName)
                ? RuntimeDefaultPackageTooltip
                : $"显式指定包：{packageName}";

        internal static Rect CalculateUtilityPopupRect(Vector2 anchor, Rect desktop, Vector2 size)
        {
            const float gap = 4f;
            float x = desktop.width >= size.x
                ? Mathf.Clamp(anchor.x, desktop.xMin, desktop.xMax - size.x)
                : desktop.xMin;

            float below = anchor.y + gap;
            float above = anchor.y - size.y - gap;
            float y = below + size.y <= desktop.yMax ? below : above;
            if (desktop.height >= size.y)
                y = Mathf.Clamp(y, desktop.yMin, desktop.yMax - size.y);
            else
                y = desktop.yMin;

            return new Rect(x, y, size.x, size.y);
        }

        // 仅在用户打开下拉菜单时采集录入候选；它们不是当前引用的运行时作用域判断。
        private static List<string> EnumerateKnownPackages()
        {
            var result = new List<string>();
            var seen = new HashSet<string>();
            foreach (AssetUtility utility in Resources.FindObjectsOfTypeAll<AssetUtility>())
            {
                if (utility == null) continue;
                foreach (string name in utility.Settings.EnumeratePackageNames())
                {
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    result.Add(name);
                }
            }
#pragma warning disable CS0618 // 未迁移的外部场景仍需正确绘制 AssetReference。
            foreach (AssetSystemConfigModel legacy in Resources.FindObjectsOfTypeAll<AssetSystemConfigModel>())
            {
                if (legacy == null) continue;
                foreach (string name in legacy.EnumeratePackageNames())
                {
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    result.Add(name);
                }
            }
#pragma warning restore CS0618
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static void SetPackageName(SerializedProperty packageProp, string packageName)
        {
            packageProp.stringValue = packageName ?? "";
            packageProp.serializedObject.ApplyModifiedProperties();
        }

        private sealed class PackageNamePopup : EditorWindow
        {
            private SerializedObject _serializedObject;
            private string _propertyPath;
            private string _value;

            public static void Open(SerializedProperty packageProp, Vector2 anchor)
            {
                var defaultSize = new Vector2(340f, 96f);
                var window = CreateInstance<PackageNamePopup>();
                window._serializedObject = packageProp.serializedObject;
                window._propertyPath = packageProp.propertyPath;
                window._value = packageProp.stringValue;
                window.titleContent = new GUIContent("资源包");
                window.minSize = new Vector2(240f, 84f);
                window.maxSize = new Vector2(720f, 140f);
                Rect desktop = InternalEditorUtility.GetBoundsOfDesktopAtPoint(anchor);
                Rect popupRect = CalculateUtilityPopupRect(anchor, desktop, defaultSize);
                window.position = popupRect;
                window.ShowUtility();
                window.position = popupRect;
            }

            private void OnGUI()
            {
                if (_serializedObject == null || _serializedObject.targetObject == null)
                {
                    EditorGUILayout.HelpBox("原属性已失效，请关闭后重新打开。", MessageType.Info);
                    if (GUILayout.Button("关闭")) Close();
                    return;
                }

                EditorGUILayout.LabelField("资源包名");
                EditorGUI.BeginChangeCheck();
                _value = EditorGUILayout.TextField(_value);
                if (EditorGUI.EndChangeCheck())
                    Apply();

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("确定", GUILayout.Width(72f)))
                    {
                        Apply();
                        Close();
                    }
                }
            }

            private void Apply()
            {
                if (_serializedObject == null || _serializedObject.targetObject == null || string.IsNullOrEmpty(_propertyPath)) return;
                _serializedObject.Update();
                var prop = _serializedObject.FindProperty(_propertyPath);
                if (prop == null) return;
                prop.stringValue = _value?.Trim() ?? "";
                _serializedObject.ApplyModifiedProperties();
            }
        }

        private Type ResolveAssetType(SerializedProperty property)
        {
            Type fieldType = GetFieldTypeCached(property);

            if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(AssetReference<>))
            {
                var genericArg = fieldType.GetGenericArguments()[0];
                return typeof(Component).IsAssignableFrom(genericArg) ? typeof(GameObject) : genericArg;
            }
            return typeof(UnityEngine.Object);
        }

        /// <summary>按 (宿主类型, propertyPath) 缓存字段类型，避免每帧反射。</summary>
        private static Type GetFieldTypeCached(SerializedProperty property)
        {
            var rootType = property.serializedObject.targetObject.GetType();
            var key = (rootType, property.propertyPath);
            if (_fieldTypeCache.TryGetValue(key, out var cached)) return cached;

            var result = ResolveFieldType(property);
            _fieldTypeCache[key] = result;
            return result;
        }

        private static Type ResolveFieldType(SerializedProperty property)
        {
            var path = property.propertyPath.Replace(".Array.data[", "[");
            var segments = path.Split('.');

            Type type = property.serializedObject.targetObject.GetType();
            FieldInfo field = null;

            foreach (var segment in segments)
            {
                if (segment.EndsWith("]"))
                {
                    var bracketIdx = segment.IndexOf('[');
                    var fieldName = segment.Substring(0, bracketIdx);
                    field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null)
                    {
                        type = field.FieldType;
                        if (type.IsArray)
                            type = type.GetElementType();
                        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                            type = type.GetGenericArguments()[0];
                    }
                }
                else
                {
                    field = type.GetField(segment, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null) type = field.FieldType;
                }
            }
            return type;
        }
    }

    /// <summary>
    /// AssetReferenceList 的 Inspector 绘制器。使用 ReorderableList 展示，支持拖拽资源批量添加。
    /// </summary>
    [CustomPropertyDrawer(typeof(AssetReferenceList<>), true)]
    public class AssetReferenceListDrawer : PropertyDrawer
    {
        private readonly Dictionary<string, UnityEditorInternal.ReorderableList> _cache = new();

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var itemsProp = property.FindPropertyRelative("_items");
            GetList(itemsProp, label).DoList(position);
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var itemsProp = property.FindPropertyRelative("_items");
            return GetList(itemsProp, label).GetHeight();
        }

        private UnityEditorInternal.ReorderableList GetList(SerializedProperty itemsProp, GUIContent label)
        {
            string key = itemsProp.propertyPath;
            if (_cache.TryGetValue(key, out var list) && list.serializedProperty == itemsProp)
                return list;

            list = new UnityEditorInternal.ReorderableList(
                itemsProp.serializedObject, itemsProp, true, true, true, true)
            {
                drawHeaderCallback = rect =>
                {
                    EditorGUI.LabelField(rect, label);
                    HandleDrag(rect, itemsProp);
                },
                drawElementCallback = (rect, index, _, _) =>
                {
                    var element = itemsProp.GetArrayElementAtIndex(index);
                    rect.height = EditorGUI.GetPropertyHeight(element, GUIContent.none, false);
                    rect.y += 1;
                    EditorGUI.PropertyField(rect, element, GUIContent.none, false);
                },
                elementHeightCallback = index =>
                {
                    var element = itemsProp.GetArrayElementAtIndex(index);
                    return EditorGUI.GetPropertyHeight(element, GUIContent.none, false) + 2;
                }
            };
            _cache[key] = list;
            return list;
        }

        private void HandleDrag(Rect rect, SerializedProperty itemsProp)
        {
            UnityEngine.Event evt = UnityEngine.Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            if (!rect.Contains(evt.mousePosition)) return;
            if (DragAndDrop.objectReferences == null || DragAndDrop.objectReferences.Length == 0) return;

            Type assetType = ResolveAssetType();
            bool hasValid = false;
            foreach (var obj in DragAndDrop.objectReferences)
            {
                if (MatchType(obj, assetType) != null) { hasValid = true; break; }
            }
            if (!hasValid) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    var matched = MatchType(obj, assetType);
                    if (matched == null) continue;

                    int idx = itemsProp.arraySize;
                    itemsProp.InsertArrayElementAtIndex(idx);
                    var element = itemsProp.GetArrayElementAtIndex(idx);
                    var guidProp = element.FindPropertyRelative("_assetGUID");
                    var packageProp = element.FindPropertyRelative("_packageName");
                    if (guidProp != null)
                    {
                        string path = AssetDatabase.GetAssetPath(matched);
                        guidProp.stringValue = AssetDatabase.AssetPathToGUID(path);
                    }
                    if (packageProp != null)
                        packageProp.stringValue = "";
                }
                itemsProp.serializedObject.ApplyModifiedProperties();
            }
            evt.Use();
        }

        private Type ResolveAssetType()
        {
            var ft = fieldInfo.FieldType;
            if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(AssetReferenceList<>))
            {
                var arg = ft.GetGenericArguments()[0];
                return typeof(Component).IsAssignableFrom(arg) ? typeof(GameObject) : arg;
            }
            return typeof(UnityEngine.Object);
        }

        private static Object MatchType(Object obj, Type targetType)
        {
            if (obj is Component || (obj is GameObject go && !AssetDatabase.Contains(go)))
                return null;
            if (targetType.IsInstanceOfType(obj))
                return obj;
            if (targetType == typeof(Sprite) && obj is Texture2D tex)
            {
                string path = AssetDatabase.GetAssetPath(tex);
                var sprites = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
                foreach (var s in sprites)
                    if (s is Sprite sprite) return sprite;
            }
            return null;
        }
    }
}
#endif
