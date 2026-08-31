#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using R3;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// <see cref="RP{T}"/> 的 Inspector 绘制器：值内联显示、Quaternion 以 EulerAngles 呈现、
    /// 修改后调 <c>ForceNotify</c> 通知订阅者。
    /// </summary>
    /// <remarks>
    /// <b>为什么是手抄副本：</b>R3 的 <c>SerializableReactivePropertyDrawer</c> 是 <c>internal</c>，
    /// 且注册时<b>未用</b> <c>useForChildren</c>——既无法跨程序集继承复用，子类 <see cref="RP{T}"/> 也不会自动套用其绘制。
    /// 因此这里保留与 R3 内部 drawer 同步的副本（对标 <c>com.cysharp.r3@b95751a30ad5</c> 的
    /// <c>Runtime/SerializableReactiveProperty.cs</c>）。R3 升级时对照该文件更新本类。
    /// </remarks>
    [CustomPropertyDrawer(typeof(RP<>))]
    internal class RPDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var p = property.FindPropertyRelative("value");
            EditorGUI.BeginChangeCheck();

            if (p.propertyType == SerializedPropertyType.Quaternion)
            {
                var quaternionLabel = new GUIContent(label)
                {
                    text = label.text + "（欧拉角 Euler Angles）",
                };
                EditorGUI.PropertyField(position, p, quaternionLabel, true);
            }
            else
            {
                EditorGUI.PropertyField(position, p, label, true);
            }

            if (EditorGUI.EndChangeCheck())
            {
                var paths = property.propertyPath.Split('.');
                var attachedComponent = property.serializedObject.targetObject;
                var targetProp = GetValueRecursive(attachedComponent, 0, paths);
                if (targetProp == null) return;

                property.serializedObject.ApplyModifiedProperties();
                var methodInfo = targetProp.GetType().GetMethod("ForceNotify",
                    BindingFlags.IgnoreCase | BindingFlags.InvokeMethod | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic);
                methodInfo?.Invoke(targetProp, Array.Empty<object>());
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var p = property.FindPropertyRelative("value");
            return p.propertyType == SerializedPropertyType.Quaternion
                ? EditorGUI.GetPropertyHeight(SerializedPropertyType.Vector3, label)
                : EditorGUI.GetPropertyHeight(p);
        }

        // 沿属性路径反射取值，用于 Inspector 修改后调用 ForceNotify 通知订阅者。
        // 逻辑与 R3 的 SerializableReactivePropertyDrawer 保持一致。
        private object GetValueRecursive(object obj, int index, string[] paths)
        {
            var path = paths[index];
            FieldInfo fldInfo = null;
            var type = obj.GetType();
            while (fldInfo == null)
            {
                fldInfo = type.GetField(path,
                    BindingFlags.IgnoreCase | BindingFlags.GetField | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic);

                if (fldInfo != null || type.BaseType == null ||
                    type.BaseType.IsSubclassOf(typeof(ReactiveProperty<>))) break;

                type = type.BaseType;
            }

            if (fldInfo == null && path == "Array")
            {
                try
                {
                    path = paths[++index];
                    var m = Regex.Match(path, @"(.+)\[([0-9]+)*\]");
                    var arrayIndex = int.Parse(m.Groups[2].Value);
                    var arrayValue = (obj as IList)[arrayIndex];
                    return index < paths.Length - 1
                        ? GetValueRecursive(arrayValue, ++index, paths)
                        : arrayValue;
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"RPDrawer 无法解析 {obj.GetType().Name} 的数组属性路径：{string.Join(", ", paths)}。" +
                        "请检查字段是否仍与 R3 SerializableReactiveProperty 的序列化结构一致。",
                        exception);
                }
            }

            if (fldInfo == null)
                throw new InvalidOperationException(
                    $"RPDrawer 无法解析属性路径：{string.Join(", ", paths)}。" +
                    "请检查字段是否已重命名，或 R3 SerializableReactiveProperty 的序列化结构是否发生变化。");

            var v = fldInfo.GetValue(obj);
            return index < paths.Length - 1 ? GetValueRecursive(v, ++index, paths) : v;
        }
    }
}
#endif
