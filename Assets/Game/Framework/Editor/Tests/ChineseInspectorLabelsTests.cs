using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定最常用框架配置的 Inspector 主标签为中文优先，而不改动序列化字段名。</summary>
    public sealed class ChineseInspectorLabelsTests
    {
        [TestCase(typeof(AssetSystemConfigModel), "_packages", "资源包列表（Packages）")]
        [TestCase(typeof(AssetSystemConfigModel), "_defaultPackageName", "默认资源包")]
        [TestCase(typeof(AssetSystemConfigModel), "_playMode", "编辑器运行模式")]
        [TestCase(typeof(AssetSystemConfigModel), "_playerPlayMode", "玩家包运行模式")]
        [TestCase(typeof(AssetPackageConfig), "_name", "资源包名")]
        [TestCase(typeof(AssetPackageConfig), "_autoInitialize", "启动时自动初始化")]
        [TestCase(typeof(AssetPackageConfig), "_enableOnDemandDownload", "允许按需下载")]
        [TestCase(typeof(SceneShortcutProfile), "_entries", "场景快捷入口")]
        [TestCase(typeof(SceneShortcutProfile), "_playFromBootScene", "从 Boot 场景启动 Play")]
        [TestCase(typeof(SceneShortcutProfile), "_bootScene", "Boot 场景")]
        public void SerializedField_HasStableChineseInspectorName(
            Type owner,
            string fieldName,
            string expected)
        {
            FieldInfo field = owner.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"找不到序列化字段：{owner.FullName}.{fieldName}");
            InspectorNameAttribute attribute = field.GetCustomAttribute<InspectorNameAttribute>();
            Assert.That(attribute, Is.Not.Null, $"字段缺少中文 InspectorName：{owner.FullName}.{fieldName}");
            Assert.That(attribute.displayName, Is.EqualTo(expected));
        }

        [TestCase(AssetPlayMode.EditorSimulate, "编辑器模拟（EditorSimulate）")]
        [TestCase(AssetPlayMode.Offline, "离线（Offline）")]
        [TestCase(AssetPlayMode.Host, "主机模式（Host）")]
        [TestCase(AssetPlayMode.Web, "网页远端（Web）")]
        public void AssetPlayMode_HasChineseFirstDisplayName(AssetPlayMode value, string expected)
        {
            FieldInfo field = typeof(AssetPlayMode).GetField(value.ToString());
            InspectorNameAttribute attribute = field?.GetCustomAttribute<InspectorNameAttribute>();
            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute.displayName, Is.EqualTo(expected));
        }
    }
}
