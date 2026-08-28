using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor.Tests
{
    /// <summary>锁定 Hierarchy 装饰器使用 Unity 6000.3 的强类型对象身份与选择 API。</summary>
    public sealed class UIBindingHierarchyDecoratorTests
    {
        [Test]
        public void EntitySelection_ResolvesObjectAndTracksCurrentSelection()
        {
            UnityEngine.Object[] previousSelection = Selection.objects;
            var gameObject = new GameObject("UIBindingEntitySelectionProbe")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                EntityId entityId = gameObject.GetEntityId();
                Assert.That(EditorUtility.EntityIdToObject(entityId), Is.SameAs(gameObject));

                Selection.activeGameObject = gameObject;
                Assert.That(UIBindingHierarchyDecorator.IsSelected(entityId), Is.True);

                Selection.objects = Array.Empty<UnityEngine.Object>();
                Assert.That(UIBindingHierarchyDecorator.IsSelected(entityId), Is.False);
            }
            finally
            {
                Selection.objects = previousSelection;
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
    }
}
