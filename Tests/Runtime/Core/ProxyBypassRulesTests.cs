using System;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// #173: env proxy scheme failures are typed, "*." wildcards exclude the apex, and CIDR rules
    /// apply to IP literals only.
    /// </summary>
    [TestFixture]
    public class ProxyBypassRulesTests
    {
        private static readonly string[] EnvNames =
            { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "NO_PROXY", "no_proxy" };

        [Test]
        public void WildcardRule_MatchesSubdomains_ButNotTheApex()
        {
            var bypass = new[] { "*.example.com" };

            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("api.example.com", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("a.b.EXAMPLE.com", 443, bypass));
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed("example.com", 443, bypass));
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed("badexample.com", 443, bypass));
        }

        [Test]
        public void LeadingDotRule_MatchesApexAndSubdomains_LikeCurl()
        {
            var bypass = new[] { ".example.com" };

            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("example.com", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("api.example.com", 443, bypass));
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed("badexample.com", 443, bypass));
        }

        [Test]
        public void CidrRule_MatchesIpLiteralsOnly_HostNamesAreNotResolved()
        {
            var bypass = new[] { "127.0.0.0/8", "::1/128" };

            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("127.0.0.1", 80, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("::1", 80, bypass));
            // "localhost" resolves to 127.0.0.1/::1, but CIDR rules never resolve host names.
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed("localhost", 80, bypass));
        }

        [Test]
        public void HttpsSchemeProxyFromEnvironment_ThrowsTypedInvalidRequest_WithoutEchoingTheValue()
        {
            var saved = Save();
            try
            {
                Clear();
                Environment.SetEnvironmentVariable("HTTPS_PROXY", "https://user:s3cret@proxy.example:8443");

                var ex = Assert.Throws<UHttpException>(
                    () => ProxyEnvironmentResolver.Resolve(new Uri("https://api.example.com/"), null));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("HTTPS_PROXY", ex.HttpError.Message);
                StringAssert.DoesNotContain("s3cret", ex.ToString());
                StringAssert.DoesNotContain("proxy.example", ex.ToString());
            }
            finally
            {
                Restore(saved);
            }
        }

        [Test]
        public void HttpsSchemeProxyFromEnvironment_BypassedTarget_DoesNotThrow()
        {
            var saved = Save();
            try
            {
                Clear();
                Environment.SetEnvironmentVariable("HTTPS_PROXY", "https://proxy.example:8443");
                Environment.SetEnvironmentVariable("NO_PROXY", ".internal.test");

                Assert.IsNull(ProxyEnvironmentResolver.Resolve(new Uri("https://svc.internal.test/"), null));
            }
            finally
            {
                Restore(saved);
            }
        }

        [Test]
        public void NoProxyWildcard_FromEnvironment_ExcludesTheApex()
        {
            var saved = Save();
            try
            {
                Clear();
                Environment.SetEnvironmentVariable("HTTP_PROXY", "http://proxy.example:8080");
                Environment.SetEnvironmentVariable("NO_PROXY", "localhost, *.corp.test");

                Assert.IsNull(ProxyEnvironmentResolver.Resolve(new Uri("http://svc.corp.test/"), null));
                var apex = ProxyEnvironmentResolver.Resolve(new Uri("http://corp.test/"), null);
                Assert.IsNotNull(apex);
                Assert.AreEqual("proxy.example", apex.Address.Host);
            }
            finally
            {
                Restore(saved);
            }
        }

        private static string[] Save()
        {
            var values = new string[EnvNames.Length];
            for (int i = 0; i < EnvNames.Length; i++)
                values[i] = Environment.GetEnvironmentVariable(EnvNames[i]);
            return values;
        }

        // Windows environment names are case-insensitive: clear every spelling before setting one.
        private static void Clear()
        {
            for (int i = 0; i < EnvNames.Length; i++)
                Environment.SetEnvironmentVariable(EnvNames[i], null);
        }

        private static void Restore(string[] values)
        {
            Clear();
            for (int i = 0; i < EnvNames.Length; i++)
            {
                if (values[i] != null)
                    Environment.SetEnvironmentVariable(EnvNames[i], values[i]);
            }
        }
    }
}
