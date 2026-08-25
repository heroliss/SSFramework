#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 资源包名的 Unity 原生绘制器。候选值只是录入辅助，序列化契约仍是普通字符串，
    /// 因而 Core 不需要 Inspector 插件，构建 Adapter 缺失时也仍可手工配置。
    /// </summary>
    [CustomPropertyDrawer(typeof(DefaultAssetPackageNameAttribute))]
    internal sealed class DefaultAssetPackageNameDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var labelRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            EditorGUI.LabelField(labelRect, label);
            Rect control = EditorGUI.IndentedRect(new Rect(
                position.x,
                labelRect.yMax + EditorGUIUtility.standardVerticalSpacing,
                position.width,
                EditorGUIUtility.singleLineHeight));
            string value = property.stringValue;
            string text = property.hasMultipleDifferentValues
                ? "—"
                : string.IsNullOrEmpty(value)
                ? "（无默认包）"
                : value;

            if (GUI.Button(control, new GUIContent(text, BuildTooltip(value)), EditorStyles.popup))
                OpenMenu(control, property);
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight * 2f + EditorGUIUtility.standardVerticalSpacing;

        private static string BuildTooltip(string value)
            => string.IsNullOrEmpty(value)
                ? "无默认包：所有加载都必须显式提供 packageName。"
                : $"不带 packageName 的便捷加载将使用 {value}。";

        private static void OpenMenu(Rect anchor, SerializedProperty property)
        {
            var menu = new GenericMenu();
            string current = property.stringValue;
            UnityEngine.Object[] targets = property.serializedObject.targetObjects;
            string propertyPath = property.propertyPath;
            menu.AddItem(
                new GUIContent("（无默认包 · Load 须带 packageName）"),
                !property.hasMultipleDifferentValues && string.IsNullOrEmpty(current),
                () => Assign(targets, propertyPath, string.Empty));

            var names = EnumerateConfiguredPackages(property.serializedObject);
            if (names.Count > 0) menu.AddSeparator(string.Empty);
            foreach (string name in names)
            {
                string captured = name;
                menu.AddItem(new GUIContent(captured),
                    !property.hasMultipleDifferentValues && current == captured,
                    () => Assign(targets, propertyPath, captured));
            }
            menu.DropDown(anchor);
        }

        private static List<string> EnumerateConfiguredPackages(SerializedObject serializedObject)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnityEngine.Object target in serializedObject.targetObjects)
            {
                if (target is not AssetSystemConfigModel config) continue;
                foreach (string name in config.EnumeratePackageNames())
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) result.Add(name);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static void Assign(UnityEngine.Object[] targets, string propertyPath, string value)
        {
            foreach (UnityEngine.Object target in targets)
            {
                if (target == null) continue;
                var serializedObject = new SerializedObject(target);
                SerializedProperty property = serializedObject.FindProperty(propertyPath);
                if (property == null) continue;
                property.stringValue = value;
                serializedObject.ApplyModifiedProperties();
            }
        }
    }

    [CustomPropertyDrawer(typeof(BuildAssetPackageNameAttribute))]
    internal sealed class BuildAssetPackageNameDrawer : PropertyDrawer
    {
        private const float ButtonWidth = 24f;
        private const float Gap = 3f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var labelRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            EditorGUI.LabelField(labelRect, label);
            Rect control = EditorGUI.IndentedRect(new Rect(
                position.x,
                labelRect.yMax + EditorGUIUtility.standardVerticalSpacing,
                position.width,
                EditorGUIUtility.singleLineHeight));
            float fieldWidth = Mathf.Max(0f, control.width - ButtonWidth - Gap);
            var fieldRect = new Rect(control.x, control.y, fieldWidth, control.height);
            var menuRect = new Rect(fieldRect.xMax + Gap, control.y, ButtonWidth, control.height);

            bool previousMixed = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            string entered = EditorGUI.TextField(fieldRect, property.stringValue);
            if (EditorGUI.EndChangeCheck()) property.stringValue = entered;
            EditorGUI.showMixedValue = previousMixed;
            if (GUI.Button(menuRect, new GUIContent("▾", "从构建收集器已知的包名中选择"), EditorStyles.miniButton))
                OpenMenu(menuRect, property);
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight * 2f + EditorGUIUtility.standardVerticalSpacing;

        private static void OpenMenu(Rect anchor, SerializedProperty property)
        {
            var menu = new GenericMenu();
            string current = property.stringValue;
            UnityEngine.Object[] targets = property.serializedObject.targetObjects;
            string propertyPath = property.propertyPath;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool any = false;
            foreach (string name in AssetPackageConfig.EnumerateEditorPackageNames())
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                any = true;
                string captured = name;
                menu.AddItem(new GUIContent(captured),
                    !property.hasMultipleDifferentValues && current == captured,
                    () => Assign(targets, propertyPath, captured));
            }

            if (!any)
                menu.AddDisabledItem(new GUIContent("暂无构建包候选，可直接手填"));
            menu.DropDown(anchor);
        }

        private static void Assign(UnityEngine.Object[] targets, string propertyPath, string value)
        {
            foreach (UnityEngine.Object target in targets)
            {
                if (target == null) continue;
                var serializedObject = new SerializedObject(target);
                SerializedProperty property = serializedObject.FindProperty(propertyPath);
                if (property == null) continue;
                property.stringValue = value;
                serializedObject.ApplyModifiedProperties();
            }
        }
    }
}
#endif
