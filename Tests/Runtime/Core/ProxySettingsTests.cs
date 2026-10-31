using System;
using System.Net;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class ProxySettingsTests
    {
        [Test]
        public void NoProxyBypass_SkipsProxy()
        {
            var bypass = new[] { ".example.com", "*.corp.local", "10.0.0.0/8", "api.test:8443" };

            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("api.example.com", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("example.com", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("svc.corp.local", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("10.1.2.3", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("api.test", 8443, bypass));
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed("api.test", 443, bypass));
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed("other.net", 443, bypass));
        }

        [Test]
        public void EnvProxyPrecedence_ResolvesCorrectly()
        {
            var originalHttps = Environment.GetEnvironmentVariable("HTTPS_PROXY");
            var originalHttpsLower = Environment.GetEnvironmentVariable("https_proxy");
            var originalHttp = Environment.GetEnvironmentVariable("HTTP_PROXY");
            var originalHttpLower = Environment.GetEnvironmentVariable("http_proxy");
            var originalNoProxy = Environment.GetEnvironmentVariable("NO_PROXY");
            var originalNoProxyLower = Environment.GetEnvironmentVariable("no_proxy");

            try
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://secure-proxy.example:8080");
                Environment.SetEnvironmentVariable("https_proxy", null);
                Environment.SetEnvironmentVariable("HTTP_PROXY", "http://plain-proxy.example:8080");
                Environment.SetEnvironmentVariable("http_proxy", null);
                Environment.SetEnvironmentVariable("NO_PROXY", "localhost,.internal.local");
                Environment.SetEnvironmentVariable("no_proxy", null);

                var httpsResult = ProxyEnvironmentResolver.Resolve(new Uri("https://api.example.com/resource"), null);
                var httpResult = ProxyEnvironmentResolver.Resolve(new Uri("http://api.example.com/resource"), null);
                var bypassed = ProxyEnvironmentResolver.Resolve(new Uri("https://localhost/resource"), null);

                Assert.IsNotNull(httpsResult);
                Assert.AreEqual("secure-proxy.example", httpsResult.Address.Host);

                Assert.IsNotNull(httpResult);
                Assert.AreEqual("plain-proxy.example", httpResult.Address.Host);

                Assert.IsNull(bypassed);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", originalHttps);
                Environment.SetEnvironmentVariable("https_proxy", originalHttpsLower);
                Environment.SetEnvironmentVariable("HTTP_PROXY", originalHttp);
                Environment.SetEnvironmentVariable("http_proxy", originalHttpLower);
                Environment.SetEnvironmentVariable("NO_PROXY", originalNoProxy);
                Environment.SetEnvironmentVariable("no_proxy", originalNoProxyLower);
            }
        }

        [Test]
        public void HttpsProxy_DoesNotFallbackToHttpProxy_ByDefault()
        {
            var originalHttps = Environment.GetEnvironmentVariable("HTTPS_PROXY");
            var originalHttpsLower = Environment.GetEnvironmentVariable("https_proxy");
            var originalHttp = Environment.GetEnvironmentVariable("HTTP_PROXY");
            var originalHttpLower = Environment.GetEnvironmentVariable("http_proxy");

            try
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
                Environment.SetEnvironmentVariable("https_proxy", null);
                Environment.SetEnvironmentVariable("HTTP_PROXY", "http://plain-proxy.example:8080");
                Environment.SetEnvironmentVariable("http_proxy", null);

                var httpsResult = ProxyEnvironmentResolver.Resolve(
                    new Uri("https://api.example.com/resource"),
                    configured: null);

                Assert.IsNull(httpsResult);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", originalHttps);
                Environment.SetEnvironmentVariable("https_proxy", originalHttpsLower);
                Environment.SetEnvironmentVariable("HTTP_PROXY", originalHttp);
                Environment.SetEnvironmentVariable("http_proxy", originalHttpLower);
            }
        }

        [Test]
        public void HttpsProxy_FallbackToHttpProxy_WhenEnabled()
        {
            var originalHttps = Environment.GetEnvironmentVariable("HTTPS_PROXY");
            var originalHttpsLower = Environment.GetEnvironmentVariable("https_proxy");
            var originalHttp = Environment.GetEnvironmentVariable("HTTP_PROXY");
            var originalHttpLower = Environment.GetEnvironmentVariable("http_proxy");

            try
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
                Environment.SetEnvironmentVariable("https_proxy", null);
                Environment.SetEnvironmentVariable("HTTP_PROXY", "http://plain-proxy.example:8080");
                Environment.SetEnvironmentVariable("http_proxy", null);

                var configured = new ProxySettings
                {
                    UseEnvironmentVariables = true,
                    AllowHttpProxyFallbackForHttps = true
                };

                var httpsResult = ProxyEnvironmentResolver.Resolve(
                    new Uri("https://api.example.com/resource"),
                    configured);

                Assert.IsNotNull(httpsResult);
                Assert.AreEqual("plain-proxy.example", httpsResult.Address.Host);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", originalHttps);
                Environment.SetEnvironmentVariable("https_proxy", originalHttpsLower);
                Environment.SetEnvironmentVariable("HTTP_PROXY", originalHttp);
                Environment.SetEnvironmentVariable("http_proxy", originalHttpLower);
            }
        }

        [Test]
        public void Cidr_OutOfRangePrefix_IsIgnored()
        {
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed("10.1.2.3", 443, new[] { "10.0.0.0/33" }));
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed("::1", 443, new[] { "2001:db8::/129" }));
        }

        [Test]
        public void Clone_Credentials_AreDeepCloned()
        {
            var original = new ProxySettings
            {
                Address = new Uri("http://proxy.example:8080"),
                Credentials = new NetworkCredential("user-a", "pass-a", "domain-a")
            };

            var clone = original.Clone();
            original.Credentials.UserName = "user-b";
            original.Credentials.Password = "pass-b";
            original.Credentials.Domain = "domain-b";

            Assert.AreEqual("user-a", clone.Credentials.UserName);
            Assert.AreEqual("pass-a", clone.Credentials.Password);
            Assert.AreEqual("domain-a", clone.Credentials.Domain);
        }

        [Test]
        public void Cidr_Ipv4MappedIpv6_MatchesIpv4Rule()
        {
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("::ffff:192.168.1.50", 443, new[] { "192.168.0.0/16" }));
        }
    }
}
