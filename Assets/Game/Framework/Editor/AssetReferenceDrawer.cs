#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Framework.Editor
{
    /// <summary>
    /// AssetReference 的 Inspector 绘制器。
    /// 通过 GUID 反查资源并显示 ObjectField，支持拖拽赋值和 Missing 提示。
    /// </summary>
    [CustomPropertyDrawer(typeof(AssetReferenceBase), true)]
    public class AssetReferenceDrawer : PropertyDrawer
    {
        private const string GUIDPropName = "_assetGUID";
        private const string PackagePropName = "_packageName";
        private const float PackageMaxWidth = 120f;
        private const float PackageGap = 4f;

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

            position.height = EditorGUIUtility.singleLineHeight;
            var contentRect = EditorGUI.PrefixLabel(position, label);
            var packageWidth = CalculatePackageWidth(contentRect.width);
            var objectRect = new Rect(contentRect.x, contentRect.y, contentRect.width - packageWidth - PackageGap, contentRect.height);
            var packageRect = new Rect(objectRect.xMax + PackageGap, contentRect.y, packageWidth, contentRect.height);

            Object newAsset = EditorGUI.ObjectField(objectRect, GUIContent.none, currentAsset, assetType, false);
            DrawPackageDropdown(packageRect, packageProp);

            if (isMissing && newAsset == null)
                EditorGUI.LabelField(objectRect, $"Missing ({assetType.Name})", EditorStyles.objectField);

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
            => EditorGUIUtility.singleLineHeight;

        private static float CalculatePackageWidth(float availableWidth)
            => Math.Min(PackageMaxWidth, Math.Max(72f, availableWidth * 0.28f));

        private static void DrawPackageDropdown(Rect rect, SerializedProperty packageProp)
        {
            if (packageProp == null)
            {
                EditorGUI.LabelField(rect, "(default)", EditorStyles.popup);
                return;
            }

            var current = packageProp.stringValue;
            var defaultPkg = ResolveDefaultPackageName();
            var defaultLabel = string.IsNullOrEmpty(defaultPkg) ? "(默认包)" : $"默认: {defaultPkg}";
            var text = string.IsNullOrEmpty(current) ? defaultLabel : current;
            var tooltip = string.IsNullOrEmpty(current)
                ? $"留空 = 用默认包（当前默认包：{(string.IsNullOrEmpty(defaultPkg) ? "未配置" : defaultPkg)}）"
                : $"显式指定包：{current}";
            if (!GUI.Button(rect, new GUIContent(text, tooltip), EditorStyles.popup)) return;

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(defaultLabel), string.IsNullOrEmpty(current), () => SetPackageName(packageProp, ""));
            var knownPackages = EnumerateKnownPackages();
            if (knownPackages.Count > 0) menu.AddSeparator("");
            foreach (var packageName in knownPackages)
            {
                var captured = packageName;
                menu.AddItem(new GUIContent(captured), current == captured, () => SetPackageName(packageProp, captured));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("自定义..."), false, () => PackageNamePopup.Open(packageProp));
            menu.DropDown(rect);
        }

        // 解析当前场景里 AssetSystemConfigModel 配的默认包名，用于把「留空」显示成「默认: 具体包名」。
        private static string ResolveDefaultPackageName()
        {
            foreach (var setting in Resources.FindObjectsOfTypeAll<AssetSystemConfigModel>())
                if (setting != null && !string.IsNullOrWhiteSpace(setting.DefaultPackageName))
                    return setting.DefaultPackageName;
            return "";
        }

        private static List<string> EnumerateKnownPackages()
        {
            var result = new List<string>();
            var seen = new HashSet<string>();
            var settings = Resources.FindObjectsOfTypeAll<AssetSystemConfigModel>();
            foreach (var setting in settings)
            {
                if (setting == null) continue;
                foreach (var name in setting.EnumeratePackageNames())
                {
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    result.Add(name);
                }
            }
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

            public static void Open(SerializedProperty packageProp)
            {
                var window = CreateInstance<PackageNamePopup>();
                window._serializedObject = packageProp.serializedObject;
                window._propertyPath = packageProp.propertyPath;
                window._value = packageProp.stringValue;
                window.titleContent = new GUIContent("Package");
                window.minSize = new Vector2(260f, 58f);
                window.maxSize = new Vector2(260f, 58f);
                window.ShowUtility();
            }

            private void OnGUI()
            {
                EditorGUILayout.LabelField("Package Name");
                EditorGUI.BeginChangeCheck();
                _value = EditorGUILayout.TextField(_value);
                if (EditorGUI.EndChangeCheck())
                    Apply();

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("OK", GUILayout.Width(72f)))
                    {
                        Apply();
                        Close();
                    }
                }
            }

            private void Apply()
            {
                if (_serializedObject == null || string.IsNullOrEmpty(_propertyPath)) return;
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
