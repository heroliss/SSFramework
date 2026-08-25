using System;
using System.Reflection;
using NUnit.Framework;

namespace Game.Framework.Asset.Yoo.Tests
{
    /// <summary>锁定默认 Provider 的 Adapter-local 注册、校验与冲突失败语义。</summary>
    public sealed class AssetProviderRegistrationTests
    {
        [Test]
        public void YooAdapter_RegistersDefaultWithoutCoreKnowingItsTypeName()
        {
            using IAssetProvider provider = (IAssetProvider)InvokeFactory("CreateDefault");

            Assert.That(provider.GetType(), Is.EqualTo(typeof(YooAssetProvider)));
            Assert.That(provider.GetType().Assembly.GetName().Name, Is.EqualTo("Game.Framework.Asset.Yoo"));
        }

        [Test]
        public void Selection_RejectsInvalidAndAmbiguousRegistrations()
        {
            AssertInnerFailure(Array.Empty<Type>(), "没有注册");
            AssertInnerFailure(new[] { typeof(string) }, "不是可构造");
            AssertInnerFailure(new[] { typeof(YooAssetProvider), typeof(YooAssetProvider) }, "多个默认资源 Provider");
        }

        private static object InvokeFactory(string methodName, params object[] arguments)
        {
            Type factory = typeof(IAssetProvider).Assembly.GetType(
                "Game.Framework.AssetProviderFactory", throwOnError: true);
            MethodInfo method = factory.GetMethod(methodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "测试通过反射验证 internal Composition Root，避免 Core 反向知道 Yoo 测试程序集名。 ");
            return method.Invoke(null, arguments);
        }

        private static void AssertInnerFailure(Type[] registrations, string message)
        {
            var exception = Assert.Throws<TargetInvocationException>(() =>
                InvokeFactory("SelectDefaultProviderType", new object[] { registrations }));
            Assert.That(exception?.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception?.InnerException?.Message, Does.Contain(message));
        }
    }
}
