using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TurboHTTP.Auth;

namespace TurboHTTP.Tests.Auth
{
    // #798: values the library produces are read-only for callers, and the token provider that
    // holds a fixed secret cannot be subclassed to widen its exposure.
    [TestFixture]
    public class OAuthResultTypeShapeTests
    {
        [TestCase(typeof(OAuthAuthorizationRequest))]
        [TestCase(typeof(OpenIdProviderMetadata))]
        public void ResultType_HasNoPublicSetters(Type type)
        {
            var writable = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod != null && p.SetMethod.IsPublic)
                .Select(p => p.Name)
                .ToArray();

            Assert.IsEmpty(writable, type.Name + " exposes public setters: " + string.Join(", ", writable));
        }

        [Test]
        public void StaticTokenProvider_IsSealed()
        {
            Assert.IsTrue(typeof(StaticTokenProvider).IsSealed);
        }
    }
}
