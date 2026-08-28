#if UNITY_EDITOR
using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Demo.PlayMode.Tests
{
    public sealed class DemoDynamicFontAssetTestGuardTests
    {
        private const string FontAssetPath =
            "Assets/Game/Framework/Demo/Res/Fonts/DemoLatin SDF.asset";

        [Test]
        public void Capture_PreExistingDirtySubAsset_FailsBeforeUserChangesCanBeDiscarded()
        {
            UnityEngine.Object subAsset = AssetDatabase.LoadAllAssetsAtPath(FontAssetPath)
                .FirstOrDefault(asset => asset is Material || asset is Texture2D);
            Assert.IsNotNull(subAsset, "测试字体必须包含材质或 atlas 子资产，才能覆盖子资产 dirty 边界。");

            EditorUtility.SetDirty(subAsset);
            try
            {
                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                    DemoDynamicFontAssetTestGuard.ThrowIfTrackedAssetsDirtyBeforeCapture);
                StringAssert.Contains("未保存的内存修改", exception.Message);
                StringAssert.Contains(FontAssetPath, exception.Message);
                StringAssert.Contains(subAsset.name, exception.Message);
            }
            finally
            {
                // 本测试只设置 dirty 标记，没有改序列化数据；撤掉标记，避免给整轮恢复制造无关输入。
                if (subAsset != null) EditorUtility.ClearDirty(subAsset);
            }
        }
    }
}
#endif
